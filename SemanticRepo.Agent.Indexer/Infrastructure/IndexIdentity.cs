using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SemanticRepo.Agent.Indexer.Infrastructure;

public sealed class IndexIdentity
{
    public IndexIdentity(IOptions<QdrantOptions> qdrant, IOptions<EmbeddingOptions> embeddings, IOptions<IndexerOptions> indexing)
    {
        var configuration = JsonSerializer.Serialize(new
        {
            Schema = 1,
            embeddings.Value.Model,
            embeddings.Value.Dimensions,
            embeddings.Value.DocumentPrefix,
            embeddings.Value.QueryPrefix,
            indexing.Value.ChunkCharacters
        });
        CollectionName = qdrant.Value.Collection + "_" + Hash(configuration)[..12];
    }

    public string CollectionName { get; }

    public static string RepositoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string RepositoryId(string path)
    {
        var normalized = RepositoryPath(path);
        return Hash(OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized);
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static Guid PointId(string repositoryId, string filePath, string revision, int chunkIndex)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { repositoryId, filePath, revision, chunkIndex })))[..16]);
}
