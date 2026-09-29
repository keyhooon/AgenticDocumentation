namespace SemanticRepo.Contracts.Indexing;

public sealed record IndexRepositoryRequest(string RepositoryPath, bool Force = false);

public sealed record IndexingErrorDto(string FilePath, string Message);

public sealed record IndexRepositoryResult(
    string RepositoryId,
    string CollectionName,
    int IndexedFiles,
    int UnchangedFiles,
    int RemovedFiles,
    int WrittenChunks,
    bool PrunedStaleFiles,
    IReadOnlyList<IndexingErrorDto> Errors);

public sealed record SearchCodeRequest(
    string RepositoryPath,
    string Query,
    int MaxResults = 10,
    double? MinimumScore = null);

public sealed record CodeSearchHitDto(
    string FilePath,
    int StartLine,
    int EndLine,
    string Language,
    string? Symbol,
    string Content,
    double Score);

public sealed record SearchCodeResult(string RepositoryId, IReadOnlyList<CodeSearchHitDto> Matches);

public sealed record CodeIndexStatusDto(
    bool IsIndexing,
    string CollectionName,
    string EmbeddingModel,
    DateTimeOffset? LastCompletedAt,
    IndexRepositoryResult? LastResult,
    string? LastError);

public interface ICodeIndexAgent
{
    Task<IndexRepositoryResult> IndexRepositoryAsync(IndexRepositoryRequest request, CancellationToken ct = default);
    Task<SearchCodeResult> SearchCodeAsync(SearchCodeRequest request, CancellationToken ct = default);
    Task<CodeIndexStatusDto> GetStatusAsync(CancellationToken ct = default);
}
