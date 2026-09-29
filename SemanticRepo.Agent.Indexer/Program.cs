using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Hosting;
using SemanticRepo.Agent.Indexer.Infrastructure;
using SemanticRepo.Agent.Indexer.Services;
using SemanticRepo.Contracts.Indexing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<IndexerOptions>().Bind(builder.Configuration.GetSection("Indexer"))
    .Validate(options => options.ScanIntervalSeconds >= 1 && options.BatchSize is >= 1 and <= 128
        && options.ChunkCharacters is >= 128 and <= 16000 && options.MaxFileBytes is >= 128 and <= 16_777_216,
        "Invalid indexer limits: interval >= 1, batch 1..128, chunk 128..16000, file size 128..16777216.")
    .Validate(options => options.IncludedExtensions is { Length: > 0 }
        && options.IncludedExtensions.All(extension => !string.IsNullOrWhiteSpace(extension) && extension.StartsWith('.'))
        && options.ExcludedDirectories is not null, "Indexer file extensions and exclusions are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<EmbeddingOptions>().Bind(builder.Configuration.GetSection("Embeddings"))
    .Validate(options => IsHttpEndpoint(options.Endpoint) && !string.IsNullOrWhiteSpace(options.Model)
        && options.Dimensions > 0 && options.TimeoutSeconds > 0
        && options.DocumentPrefix is not null && options.QueryPrefix is not null, "Invalid embedding configuration.")
    .ValidateOnStart();
builder.Services.AddOptions<QdrantOptions>().Bind(builder.Configuration.GetSection("Qdrant"))
    .Validate(options => IsHttpEndpoint(options.Endpoint) && !string.IsNullOrWhiteSpace(options.Collection)
        && Regex.IsMatch(options.Collection, "^[a-zA-Z0-9_-]{1,100}$"), "Invalid Qdrant endpoint or collection name.")
    .ValidateOnStart();
builder.Services.AddOptions<IndexerRpcOptions>().Bind(builder.Configuration.GetSection("IndexerRpc"))
    .Validate(options => IPAddress.TryParse(options.Address, out _) && options.Port is > 0 and <= 65535
        && options.MaxConnections > 0, "Invalid indexer RPC address, port or connection limit.")
    .ValidateOnStart();

builder.Services.AddSingleton<IndexIdentity>();
builder.Services.AddSingleton<RepositoryScanner>();
builder.Services.AddSingleton<CodeChunker>();
builder.Services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>((services, http) =>
{
    var settings = services.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
    http.BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
});
builder.Services.AddHttpClient<ICodeVectorStore, QdrantCodeVectorStore>((services, http) =>
{
    var settings = services.GetRequiredService<IOptions<QdrantOptions>>().Value;
    http.BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(120);
    if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        http.DefaultRequestHeaders.Add("api-key", settings.ApiKey);
});
builder.Services.AddSingleton<ICodeIndexAgent, CodeIndexAgent>();
builder.Services.AddHostedService<IndexerRpcServerWorker>();
builder.Services.AddHostedService<RepositoryIndexWorker>();

await builder.Build().RunAsync();

static bool IsHttpEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
    && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
