using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SemanticRepo.Contracts.Indexing;

namespace SemanticRepo.Agent.Indexer.Infrastructure;

public sealed class QdrantCodeVectorStore(HttpClient http, IndexIdentity identity, IOptions<EmbeddingOptions> embeddings) : ICodeVectorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string collectionPath = "collections/" + Uri.EscapeDataString(identity.CollectionName);

    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        using var existing = await http.GetAsync(collectionPath, ct);
        if (existing.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await http.PutAsJsonAsync(collectionPath, new
            {
                vectors = new { size = embeddings.Value.Dimensions, distance = "Cosine" }
            }, ct);
            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
                created.EnsureSuccessStatusCode();
        }
        else
        {
            existing.EnsureSuccessStatusCode();
        }
        using var details = await SendAsync(HttpMethod.Get, collectionPath, null, ct);
        var vectors = details.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
        if (!vectors.TryGetProperty("size", out var size) || size.GetInt32() != embeddings.Value.Dimensions
            || vectors.GetProperty("distance").GetString() != "Cosine")
            throw new InvalidOperationException("The Qdrant collection has incompatible vector dimensions or distance. Choose another Qdrant:Collection.");
        foreach (var field in new[] { "repositoryId", "filePath" })
        {
            using var indexed = await SendAsync(HttpMethod.Put, collectionPath + "/index?wait=true",
                new { field_name = field, field_schema = "keyword" }, ct);
        }
    }

    public async Task<IReadOnlyList<IndexedChunkState>> ReadStateAsync(string repositoryId, CancellationToken ct = default)
    {
        var states = new List<IndexedChunkState>();
        JsonElement? offset = null;
        do
        {
            using var page = await SendAsync(HttpMethod.Post, collectionPath + "/points/scroll", new
            {
                filter = Filter(repositoryId),
                limit = 256,
                offset,
                with_payload = new[] { "filePath", "revision", "chunkIndex", "chunkCount" },
                with_vector = false
            }, ct);
            var result = page.RootElement.GetProperty("result");
            foreach (var point in result.GetProperty("points").EnumerateArray())
            {
                var payload = point.GetProperty("payload");
                states.Add(new IndexedChunkState(payload.GetProperty("filePath").GetString()!,
                    payload.GetProperty("revision").GetString()!, payload.GetProperty("chunkIndex").GetInt32(),
                    payload.GetProperty("chunkCount").GetInt32()));
            }
            offset = result.TryGetProperty("next_page_offset", out var next) && next.ValueKind != JsonValueKind.Null ? next.Clone() : null;
        } while (offset is not null);
        return states;
    }

    public async Task UpsertAsync(IReadOnlyList<CodeVectorPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0)
            return;
        using var result = await SendAsync(HttpMethod.Put, collectionPath + "/points?wait=true", new
        {
            points = points.Select(point => new { id = point.Id, vector = point.Vector, payload = point.Payload })
        }, ct);
    }

    public async Task DeleteFileAsync(string repositoryId, string filePath, string? exceptRevision = null, CancellationToken ct = default)
    {
        using var result = await SendAsync(HttpMethod.Post, collectionPath + "/points/delete?wait=true", new
        {
            filter = new
            {
                must = new[] { Match("repositoryId", repositoryId), Match("filePath", filePath) },
                must_not = exceptRevision is null ? [] : new[] { Match("revision", exceptRevision) }
            }
        }, ct);
    }

    public async Task<IReadOnlyList<CodeSearchHitDto>> SearchAsync(string repositoryId, float[] vector, int limit,
        double? minimumScore, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(collectionPath + "/points/query", new
        {
            query = vector,
            filter = Filter(repositoryId),
            limit,
            score_threshold = minimumScore,
            with_payload = true,
            with_vector = false
        }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return body.RootElement.GetProperty("result").GetProperty("points").EnumerateArray().Select(point =>
        {
            var payload = point.GetProperty("payload").Deserialize<CodePointPayload>(JsonOptions)
                ?? throw new InvalidOperationException("Qdrant returned an invalid code payload.");
            return new CodeSearchHitDto(payload.FilePath, payload.StartLine, payload.EndLine, payload.Language,
                payload.Symbol, payload.Content, point.GetProperty("score").GetDouble());
        }).ToArray();
    }

    private static object Match(string field, string value) => new { key = field, match = new { value } };
    private static object Filter(string repositoryId) => new { must = new[] { Match("repositoryId", repositoryId) } };

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
