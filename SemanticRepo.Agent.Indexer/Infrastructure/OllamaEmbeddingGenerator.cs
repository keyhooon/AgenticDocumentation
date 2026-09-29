using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SemanticRepo.Agent.Indexer.Infrastructure;

public sealed class OllamaEmbeddingGenerator(HttpClient http, IOptions<EmbeddingOptions> options) : IEmbeddingGenerator
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];
        var settings = options.Value;
        var prefix = isQuery ? settings.QueryPrefix : settings.DocumentPrefix;
        using var response = await http.PostAsJsonAsync("api/embed", new
        {
            model = settings.Model,
            input = texts.Select(text => prefix + text).ToArray(),
            truncate = false
        }, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama embedding request failed ({(int)response.StatusCode}). Ensure model '{settings.Model}' is pulled and the input fits its context window.", null, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var vectors = body.RootElement.GetProperty("embeddings").Deserialize<float[][]>()
            ?? throw new InvalidOperationException("Ollama returned no embeddings.");
        if (vectors.Length != texts.Count || vectors.Any(vector => vector is null || vector.Length != settings.Dimensions
            || vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0)))
            throw new InvalidOperationException($"Ollama must return one nonzero, finite {settings.Dimensions}-dimensional vector per input. Check Embeddings:Dimensions and the model.");
        return vectors;
    }
}
