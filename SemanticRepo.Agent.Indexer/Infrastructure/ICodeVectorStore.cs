using SemanticRepo.Contracts.Indexing;

namespace SemanticRepo.Agent.Indexer.Infrastructure;

public sealed record CodeChunk(string FilePath, int StartLine, int EndLine, string Language, string? Symbol, string Content);

public sealed record CodeVectorPoint(Guid Id, float[] Vector, CodePointPayload Payload);

public sealed record CodePointPayload(
    string RepositoryId,
    string FilePath,
    string Revision,
    int ChunkIndex,
    int ChunkCount,
    int StartLine,
    int EndLine,
    string Language,
    string? Symbol,
    string Content);

public sealed record IndexedChunkState(string FilePath, string Revision, int ChunkIndex, int ChunkCount);

public interface ICodeVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<IndexedChunkState>> ReadStateAsync(string repositoryId, CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyList<CodeVectorPoint> points, CancellationToken ct = default);
    Task DeleteFileAsync(string repositoryId, string filePath, string? exceptRevision = null, CancellationToken ct = default);
    Task<IReadOnlyList<CodeSearchHitDto>> SearchAsync(string repositoryId, float[] vector, int limit, double? minimumScore, CancellationToken ct = default);
}
