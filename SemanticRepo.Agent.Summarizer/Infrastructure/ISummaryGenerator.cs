using SemanticRepo.Contracts.Summarization;

namespace SemanticRepo.Agent.Summarizer.Infrastructure;

public sealed record GeneratedSummary(
    string Title,
    IReadOnlyList<SummarySectionDto> Sections,
    IReadOnlyList<string> Limitations);

public interface ISummaryGenerator
{
    Task<GeneratedSummary> GenerateAsync(string subject, string? focus, SummaryDetail detail,
        IReadOnlyList<SummaryEvidenceDto> evidence, CancellationToken ct = default);
}
