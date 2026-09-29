using System.Text;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Infrastructure;
using SemanticRepo.Contracts.Indexing;

namespace SemanticRepo.Agent.Indexer.Services;

public sealed class CodeIndexAgent(
    RepositoryScanner scanner,
    CodeChunker chunker,
    IEmbeddingGenerator embeddings,
    ICodeVectorStore store,
    IndexIdentity identity,
    IOptions<IndexerOptions> options,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<CodeIndexAgent> logger) : ICodeIndexAgent, IDisposable
{
    private readonly SemaphoreSlim indexingGate = new(1, 1);
    private CodeIndexStatusDto status = new(false, identity.CollectionName, embeddingOptions.Value.Model, null, null, null);

    public Task<CodeIndexStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref status));
    }

    public async Task<IndexRepositoryResult> IndexRepositoryAsync(IndexRepositoryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = IndexIdentity.RepositoryPath(request.RepositoryPath);
        var repositoryId = IndexIdentity.RepositoryId(root);
        await indexingGate.WaitAsync(ct);
        Volatile.Write(ref status, status with { IsIndexing = true, LastError = null });
        try
        {
            var scan = scanner.Scan(root, ct);
            await store.EnsureCollectionAsync(ct);
            var previous = (await store.ReadStateAsync(repositoryId, ct)).GroupBy(state => state.FilePath)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var errors = scan.Errors.ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var indexedFiles = 0;
            var unchangedFiles = 0;
            var removedFiles = 0;
            var writtenChunks = 0;
            foreach (var file in scan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = RepositoryScanner.RelativePath(root, file);
                seen.Add(relative);
                string content;
                try
                {
                    content = await scanner.ReadAsync(file, ct);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    errors.Add(new IndexingErrorDto(relative, exception.Message));
                    continue;
                }
                var revision = IndexIdentity.Hash(content);
                previous.TryGetValue(relative, out var oldChunks);
                if (!request.Force && IsComplete(oldChunks, revision))
                {
                    unchangedFiles++;
                    continue;
                }
                var chunks = chunker.Chunk(relative, content, ct);
                var points = new List<CodeVectorPoint>();
                foreach (var batch in chunks.Select((chunk, index) => (Chunk: chunk, Index: index)).Chunk(options.Value.BatchSize))
                {
                    var inputs = batch.Select(item => $"File: {item.Chunk.FilePath}\nLanguage: {item.Chunk.Language}\nSymbol: {item.Chunk.Symbol}\n{item.Chunk.Content}").ToArray();
                    var vectors = await embeddings.EmbedAsync(inputs, false, ct);
                    if (vectors.Count != batch.Length)
                        throw new InvalidOperationException("Embedding count does not match the code chunk count.");
                    for (var position = 0; position < batch.Length; position++)
                    {
                        var (chunk, index) = batch[position];
                        var payload = new CodePointPayload(repositoryId, relative, revision, index, chunks.Count,
                            chunk.StartLine, chunk.EndLine, chunk.Language, chunk.Symbol, chunk.Content);
                        points.Add(new CodeVectorPoint(IndexIdentity.PointId(repositoryId, relative, revision, index), vectors[position], payload));
                    }
                }
                await store.UpsertAsync(points, ct);
                await store.DeleteFileAsync(repositoryId, relative, chunks.Count == 0 ? null : revision, ct);
                indexedFiles++;
                writtenChunks += points.Count;
            }
            if (errors.Count == 0)
            {
                foreach (var missing in previous.Keys.Where(path => !seen.Contains(path)))
                {
                    await store.DeleteFileAsync(repositoryId, missing, ct: ct);
                    removedFiles++;
                }
            }
            var result = new IndexRepositoryResult(repositoryId, identity.CollectionName, indexedFiles,
                unchangedFiles, removedFiles, writtenChunks, errors.Count == 0, errors.ToArray());
            Volatile.Write(ref status, status with { LastCompletedAt = DateTimeOffset.UtcNow, LastResult = result });
            logger.LogInformation("Indexed {IndexedFiles} files ({Chunks} chunks); {UnchangedFiles} unchanged, {RemovedFiles} removed, {Errors} errors in {Repository}",
                indexedFiles, writtenChunks, unchangedFiles, removedFiles, errors.Count, root);
            return result;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref status, status with { LastError = exception.Message });
            throw;
        }
        finally
        {
            Volatile.Write(ref status, status with { IsIndexing = false });
            indexingGate.Release();
        }
    }

    public async Task<SearchCodeResult> SearchCodeAsync(SearchCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (request.Query.Length > 8000)
            throw new ArgumentException("Search queries must not exceed 8000 characters.", nameof(request));
        if (request.MaxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxResults must be between 1 and 100.");
        if (request.MinimumScore is { } score && (!double.IsFinite(score) || score is < -1 or > 1))
            throw new ArgumentOutOfRangeException(nameof(request), "MinimumScore must be between -1 and 1.");
        var repositoryId = IndexIdentity.RepositoryId(request.RepositoryPath);
        var vectors = await embeddings.EmbedAsync([request.Query], true, ct);
        var matches = await store.SearchAsync(repositoryId, vectors[0], request.MaxResults, request.MinimumScore, ct);
        return new SearchCodeResult(repositoryId, matches);
    }

    private static bool IsComplete(IndexedChunkState[]? chunks, string revision)
        => chunks is { Length: > 0 } && chunks.All(chunk => chunk.Revision == revision && chunk.ChunkCount == chunks.Length)
            && chunks.Select(chunk => chunk.ChunkIndex).Order().SequenceEqual(Enumerable.Range(0, chunks.Length));

    public void Dispose() => indexingGate.Dispose();
}
