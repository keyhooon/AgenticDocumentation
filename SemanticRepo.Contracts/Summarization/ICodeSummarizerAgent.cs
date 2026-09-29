using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Contracts.Summarization;

public enum SummaryDetail
{
    Brief = 0,
    Standard = 1,
    Detailed = 2
}

public sealed record SummarizeCodeRequest(
    CodeScopeDto Scope,
    string? TypeFullName = null,
    string? MethodName = null,
    int OverloadIndex = 0,
    string? Focus = null,
    SummaryDetail Detail = SummaryDetail.Standard,
    string? RepositoryPath = null,
    int MaxSearchResults = 8);

public sealed record SummarySourceDto(
    string FilePath,
    int StartLine,
    int EndLine,
    string? Symbol = null);

public sealed record SummaryEvidenceDto(
    string Id,
    string Kind,
    string Content,
    SummarySourceDto? Source = null,
    bool IsTruncated = false);

public sealed record SummarizeEvidenceRequest(
    string Subject,
    IReadOnlyList<SummaryEvidenceDto> Evidence,
    string? Focus = null,
    SummaryDetail Detail = SummaryDetail.Standard);

public sealed record SummaryClaimDto(string Text, IReadOnlyList<string> EvidenceIds);

public sealed record SummarySectionDto(string Heading, IReadOnlyList<SummaryClaimDto> Claims);

public sealed record CodeSummaryDto(
    string Subject,
    string Title,
    string SummaryMarkdown,
    IReadOnlyList<SummarySectionDto> Sections,
    IReadOnlyList<SummaryEvidenceDto> Evidence,
    IReadOnlyList<string> Limitations,
    string Model,
    DateTimeOffset GeneratedAt);

public sealed record SummarizerStatusDto(
    bool IsReady,
    string Model,
    int ActiveRequests,
    long CompletedRequests,
    DateTimeOffset? LastCompletedAt,
    string? LastError);

public interface ICodeSummarizerAgent
{
    Task<CodeSummaryDto> SummarizeCodeAsync(SummarizeCodeRequest request, CancellationToken ct = default);
    Task<CodeSummaryDto> SummarizeEvidenceAsync(SummarizeEvidenceRequest request, CancellationToken ct = default);
    Task<SummarizerStatusDto> GetStatusAsync(CancellationToken ct = default);
}
