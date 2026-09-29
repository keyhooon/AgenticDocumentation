using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Summarizer.Infrastructure;
using SemanticRepo.Contracts.Indexing;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Hierarchy;
using SemanticRepo.Contracts.Roslyn.Hub;
using SemanticRepo.Contracts.Roslyn.Metadata;
using SemanticRepo.Contracts.Roslyn.Syntax;
using SemanticRepo.Contracts.Summarization;

namespace SemanticRepo.Agent.Summarizer.Services;

public sealed class SummaryEvidenceCollector(
    IOptionsMonitor<AgentEndpointOptions> endpoints,
    IOptions<SummarizerOptions> options,
    ILogger<SummaryEvidenceCollector> logger) : ISummaryEvidenceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<CollectedSummaryEvidence> CollectAsync(SummarizeCodeRequest request, CancellationToken ct = default)
    {
        var budget = new EvidenceBudget(options.Value);
        var limitations = new List<string>();
        var nextId = 0;
        void Add(string kind, string content, SummarySourceDto? source = null)
            => budget.Add(new SummaryEvidenceDto($"E{++nextId}", kind, content, source));

        string subject;
        var endpoint = endpoints.Get("Roslyn");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
            using var connection = await AgentRpcConnection<IRoslynToolHub>.ConnectAsync(endpoint, timeout.Token);
            var roslyn = connection.Proxy;
            if (request.TypeFullName is { } typeName)
            {
                var declaration = await roslyn.GetTypeDeclarationAsync(new(request.Scope, typeName), timeout.Token)
                    ?? throw new KeyNotFoundException($"Type '{typeName}' was not found in the supplied scope.");
                subject = declaration.FullName;
                if (request.MethodName is { } methodName)
                {
                    if (!declaration.Methods.Any(method => method.Name == methodName))
                        throw new KeyNotFoundException($"Method '{typeName}.{methodName}' was not found.");
                    subject += $".{methodName} (overload {request.OverloadIndex})";
                    var body = await roslyn.GetMethodBodyAsync(new(request.Scope, typeName, methodName, request.OverloadIndex), timeout.Token);
                    if (body is null)
                        throw new InvalidOperationException("The requested method overload has no available source body. Summarize the containing type instead.");
                    Add("MethodBody", body.BodyText, Source(body.Span, typeName + "." + methodName));
                }
                Add("TypeDeclaration", JsonSerializer.Serialize(declaration, JsonOptions), Source(declaration.DeclaredAt, declaration.FullName));
                var hierarchy = await roslyn.GetTypeHierarchyAsync(new(request.Scope, typeName), timeout.Token);
                if (hierarchy is not null)
                    Add("TypeHierarchy", JsonSerializer.Serialize(hierarchy, JsonOptions), Source(declaration.DeclaredAt, declaration.FullName));
                if (request.MethodName is null)
                    limitations.Add("Type analysis includes declarations and hierarchy; method implementations were not inspected.");
            }
            else
            {
                var skeleton = await roslyn.GetProjectSkeletonAsync(new(request.Scope), timeout.Token);
                subject = skeleton.ScopeId;
                foreach (var declaration in skeleton.Types)
                    Add("TypeDeclaration", JsonSerializer.Serialize(declaration, JsonOptions), Source(declaration.DeclaredAt, declaration.FullName));
                var registrations = await roslyn.GetDIRegistrationsAsync(new GetDIRegistrationsRequest(request.Scope), timeout.Token);
                foreach (var registration in registrations.Registrations)
                    Add("DIRegistration", JsonSerializer.Serialize(registration, JsonOptions), Source(registration.DeclaredAt, registration.ServiceType));
                limitations.Add("Scope analysis is based on declarations and DI registrations; private members and method bodies are not exhaustively inspected.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.RepositoryPath))
        {
            var indexer = endpoints.Get("Indexer");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(indexer.TimeoutSeconds));
            try
            {
                using var connection = await AgentRpcConnection<ICodeIndexAgent>.ConnectAsync(indexer, timeout.Token);
                var query = request.Focus ?? $"Explain the responsibilities and dependencies of {request.TypeFullName ?? "the repository"}";
                var result = await connection.Proxy.SearchCodeAsync(new(request.RepositoryPath, query, request.MaxSearchResults), timeout.Token);
                foreach (var hit in result.Matches)
                    Add("IndexedCode", hit.Content, new SummarySourceDto(hit.FilePath, hit.StartLine, hit.EndLine, hit.Symbol));
                limitations.Add(result.Matches.Count == 0
                    ? "Index search returned no additional evidence. Ensure the repository has been indexed."
                    : "Indexed snippets can lag behind the working tree and do not prove complete repository coverage.");
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Optional code index search failed during summarization.");
                limitations.Add("Code index search was unavailable; only Roslyn evidence was used.");
            }
        }
        if (budget.WasLimited)
            limitations.Add("Evidence exceeded the configured context budget; some items were omitted or truncated.");
        return new CollectedSummaryEvidence(subject, budget.Evidence, limitations);
    }

    private static SummarySourceDto? Source(SourceSpanDto? span, string? symbol)
        => span is null ? null : new SummarySourceDto(span.FilePath, span.StartLine,
            Math.Max(span.StartLine, span.EndColumn == 1 ? span.EndLine - 1 : span.EndLine), symbol);
}
