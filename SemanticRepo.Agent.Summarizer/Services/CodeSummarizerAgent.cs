using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Summarizer.Infrastructure;
using SemanticRepo.Contracts.Summarization;

namespace SemanticRepo.Agent.Summarizer.Services;

public sealed class CodeSummarizerAgent(
    ISummaryEvidenceCollector collector,
    ISummaryGenerator generator,
    IOptions<SummarizerOptions> options,
    IOptions<SummaryModelOptions> modelOptions,
    ILogger<CodeSummarizerAgent> logger) : ICodeSummarizerAgent, IDisposable
{
    private readonly SemaphoreSlim generationSlots = new(options.Value.MaxConcurrentRequests);
    private readonly object statusLock = new();
    private SummarizerStatusDto status = new(true, modelOptions.Value.Model, 0, 0, null, null);

    public Task<SummarizerStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (statusLock)
            return Task.FromResult(status);
    }

    public Task<CodeSummaryDto> SummarizeCodeAsync(SummarizeCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        if (!request.Scope.IsValid(out var error))
            throw new ArgumentException(error, nameof(request));
        ValidateText(request.TypeFullName, 2000, nameof(request.TypeFullName), optional: true);
        ValidateText(request.MethodName, 500, nameof(request.MethodName), optional: true);
        ValidateText(request.RepositoryPath, 4096, nameof(request.RepositoryPath), optional: true);
        ValidatePreferences(request.Focus, request.Detail);
        if (request.MethodName is not null && request.TypeFullName is null)
            throw new ArgumentException("MethodName requires TypeFullName.", nameof(request));
        if (request.OverloadIndex < 0 || request.MethodName is null && request.OverloadIndex != 0)
            throw new ArgumentOutOfRangeException(nameof(request), "OverloadIndex must be non-negative and requires MethodName.");
        if (request.MaxSearchResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxSearchResults must be between 1 and 100.");
        return ExecuteAsync(async token =>
        {
            var collected = await collector.CollectAsync(request, token);
            return await GenerateAsync(collected.Subject, request.Focus, request.Detail, collected.Evidence, collected.Limitations, token);
        }, ct);
    }

    public Task<CodeSummaryDto> SummarizeEvidenceAsync(SummarizeEvidenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Subject, 4096, nameof(request.Subject));
        ValidatePreferences(request.Focus, request.Detail);
        ValidateEvidence(request.Evidence);
        var evidence = request.Evidence.ToArray();
        return ExecuteAsync(token => GenerateAsync(request.Subject, request.Focus, request.Detail, evidence,
            ["This summary uses caller-supplied evidence; it has not been independently verified against the repository."], token), ct);
    }

    private async Task<CodeSummaryDto> ExecuteAsync(Func<CancellationToken, Task<CodeSummaryDto>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
        var entered = false;
        try
        {
            await generationSlots.WaitAsync(timeout.Token);
            entered = true;
            lock (statusLock)
                status = status with { ActiveRequests = status.ActiveRequests + 1 };
            var result = await operation(timeout.Token);
            lock (statusLock)
                status = status with { CompletedRequests = status.CompletedRequests + 1, LastCompletedAt = result.GeneratedAt, LastError = null };
            logger.LogInformation("Generated code summary using {EvidenceCount} evidence items and {SectionCount} sections.",
                result.Evidence.Count, result.Sections.Count);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            const string message = "Summarization timed out while waiting for an agent or model response.";
            lock (statusLock)
                status = status with { LastError = message };
            throw new TimeoutException(message, exception);
        }
        catch (Exception exception)
        {
            lock (statusLock)
                status = status with { LastError = exception.Message };
            throw;
        }
        finally
        {
            if (entered)
            {
                lock (statusLock)
                    status = status with { ActiveRequests = status.ActiveRequests - 1 };
                generationSlots.Release();
            }
        }
    }

    private async Task<CodeSummaryDto> GenerateAsync(string subject, string? focus, SummaryDetail detail,
        IReadOnlyList<SummaryEvidenceDto> input, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        if (input.Count == 0)
            throw new InvalidOperationException("No source evidence was found. Narrow the scope or supply evidence explicitly.");
        var budget = new EvidenceBudget(options.Value);
        foreach (var item in input)
            budget.Add(item);
        var evidence = budget.Evidence.ToArray();
        var generated = await generator.GenerateAsync(subject, focus, detail, evidence, ct);
        ValidateGenerated(generated, evidence);
        var limitations = warnings.Concat(generated.Limitations).ToList();
        if (budget.WasLimited || evidence.Any(item => item.IsTruncated))
            limitations.Add("Evidence exceeded the configured context budget; some items were omitted or truncated.");
        var distinctLimitations = limitations.Distinct(StringComparer.Ordinal).ToArray();
        return new CodeSummaryDto(subject, generated.Title,
            Render(generated, evidence, distinctLimitations), generated.Sections, evidence,
            distinctLimitations, modelOptions.Value.Model, DateTimeOffset.UtcNow);
    }

    private static void ValidatePreferences(string? focus, SummaryDetail detail)
    {
        ValidateText(focus, 4000, nameof(focus), optional: true);
        if (!Enum.IsDefined(detail))
            throw new ArgumentOutOfRangeException(nameof(detail));
    }

    private static void ValidateEvidence(IReadOnlyList<SummaryEvidenceDto> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count is < 1 or > 256)
            throw new ArgumentException("Provide between 1 and 256 evidence items.", nameof(evidence));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Id is null || !Regex.IsMatch(item.Id, @"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z") || !identifiers.Add(item.Id))
                throw new ArgumentException("Evidence IDs must be unique, start with a letter, and contain at most 64 letters, digits, underscores or hyphens.", nameof(evidence));
            ValidateText(item.Kind, 100, nameof(item.Kind));
            ValidateText(item.Content, 262144, nameof(item.Content));
            characters += item.Content.Length;
            if (item.Source is { } source)
            {
                ValidateText(source.FilePath, 4096, nameof(source.FilePath));
                ValidateText(source.Symbol, 2000, nameof(source.Symbol), optional: true);
                if (source.StartLine < 1 || source.EndLine < source.StartLine)
                    throw new ArgumentException("Source ranges must be inclusive, one-based, and ordered.", nameof(evidence));
            }
        }
        if (characters > 1_048_576)
            throw new ArgumentException("Evidence input must not exceed 1048576 characters.", nameof(evidence));
    }

    private static void ValidateText(string? text, int maximum, string name, bool optional = false)
    {
        if (text is null && optional)
            return;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
    }

    private static void ValidateGenerated(GeneratedSummary result, IReadOnlyList<SummaryEvidenceDto> evidence)
    {
        static bool TextValid(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum;
        if (result is null || !TextValid(result.Title, 200) || result.Sections is not { Count: >= 1 and <= 8 }
            || result.Limitations is not { Count: <= 16 } || result.Limitations.Any(item => !TextValid(item, 1000)))
            throw new InvalidDataException("The model returned an invalid summary structure.");
        var identifiers = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var section in result.Sections)
        {
            if (section is null || !TextValid(section.Heading, 100) || section.Claims is not { Count: >= 1 and <= 12 })
                throw new InvalidDataException("The model returned an invalid summary section.");
            foreach (var claim in section.Claims)
            {
                if (claim is null || !TextValid(claim.Text, 2000) || claim.EvidenceIds is not { Count: >= 1 and <= 64 }
                    || claim.EvidenceIds.Any(identifier => identifier is null || !identifiers.Contains(identifier)))
                    throw new InvalidDataException("Every summary claim must cite evidence IDs that were supplied to the model.");
            }
        }
    }

    private static string Render(GeneratedSummary summary, IReadOnlyList<SummaryEvidenceDto> evidence, IReadOnlyList<string> limitations)
    {
        var markdown = new StringBuilder().Append("# ").AppendLine(Escape(summary.Title));
        foreach (var section in summary.Sections)
        {
            markdown.AppendLine().Append("## ").AppendLine(Escape(section.Heading)).AppendLine();
            foreach (var claim in section.Claims)
            {
                markdown.Append("- ").Append(Escape(claim.Text));
                foreach (var identifier in claim.EvidenceIds.Distinct(StringComparer.Ordinal))
                    markdown.Append(" [^").Append(identifier).Append(']');
                markdown.AppendLine();
            }
        }
        if (limitations.Count > 0)
        {
            markdown.AppendLine().AppendLine("## Limitations").AppendLine();
            foreach (var limitation in limitations)
                markdown.Append("- ").AppendLine(Escape(limitation));
        }
        var cited = summary.Sections.SelectMany(section => section.Claims).SelectMany(claim => claim.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        markdown.AppendLine();
        foreach (var item in evidence.Where(item => cited.Contains(item.Id)))
        {
            markdown.Append("[^").Append(item.Id).Append("]: ").Append(Escape(item.Kind));
            if (item.Source is { } source)
                markdown.Append(" — ").Append(Escape(source.FilePath)).Append(':').Append(source.StartLine)
                    .Append('-').Append(source.EndLine).Append(source.Symbol is null ? "" : " (" + Escape(source.Symbol) + ")");
            if (item.IsTruncated)
                markdown.Append(" (truncated)");
            markdown.AppendLine();
        }
        return markdown.ToString();
    }

    private static string Escape(string text)
    {
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ');
        return Regex.Replace(flattened, @"[\\`*_{}\[\]()#+.!|<>~&]", match => "\\" + match.Value);
    }

    public void Dispose()
    {
        lock (statusLock)
            status = status with { IsReady = false };
        generationSlots.Dispose();
    }
}
