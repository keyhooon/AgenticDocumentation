using SemanticRepo.Contracts.Summarization;

namespace SemanticRepo.Agent.Summarizer.Services;

public sealed record CollectedSummaryEvidence(
    string Subject,
    IReadOnlyList<SummaryEvidenceDto> Evidence,
    IReadOnlyList<string> Limitations);

public interface ISummaryEvidenceCollector
{
    Task<CollectedSummaryEvidence> CollectAsync(SummarizeCodeRequest request, CancellationToken ct = default);
}
