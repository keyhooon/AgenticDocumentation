using System.Net;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Summarizer.Hosting;
using SemanticRepo.Agent.Summarizer.Infrastructure;
using SemanticRepo.Agent.Summarizer.Services;
using SemanticRepo.Contracts.Summarization;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<SummarizerOptions>().Bind(builder.Configuration.GetSection("Summarizer"))
    .Validate(options => options.MaxEvidenceCharacters is >= 1024 and <= 128000
        && options.MaxEvidenceItems is >= 1 and <= 256 && options.MaxConcurrentRequests is >= 1 and <= 32
        && options.RequestTimeoutSeconds is >= 1 and <= 3600, "Invalid summarizer context, concurrency, or timeout limits.")
    .ValidateOnStart();
builder.Services.AddOptions<SummaryModelOptions>().Bind(builder.Configuration.GetSection("SummaryModel"))
    .Validate(options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.Query)
        && string.IsNullOrEmpty(endpoint.Fragment) && !string.IsNullOrWhiteSpace(options.Model)
        && options.TimeoutSeconds is >= 1 and <= 3600 && options.ContextTokens is >= 2048 and <= 131072
        && options.MaxOutputTokens is >= 128 && options.MaxOutputTokens < options.ContextTokens,
        "Invalid summary model endpoint, model, timeout, or token limits.")
    .ValidateOnStart();
foreach (var name in new[] { "Roslyn", "Indexer" })
{
    builder.Services.AddOptions<AgentEndpointOptions>(name).Bind(builder.Configuration.GetSection(name))
        .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535
            && options.TimeoutSeconds is >= 1 and <= 3600, "Invalid upstream agent host, port, or timeout.")
        .ValidateOnStart();
}
builder.Services.AddOptions<SummarizerRpcOptions>().Bind(builder.Configuration.GetSection("SummarizerRpc"))
    .Validate(options => IPAddress.TryParse(options.Address, out _) && options.Port is > 0 and <= 65535
        && options.MaxConnections is >= 1 and <= 1024, "Invalid summarizer RPC address, port, or connection limit.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ISummaryGenerator, OllamaSummaryGenerator>((services, http) =>
{
    var settings = services.GetRequiredService<IOptions<SummaryModelOptions>>().Value;
    http.BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
});
builder.Services.AddSingleton<ISummaryEvidenceCollector, SummaryEvidenceCollector>();
builder.Services.AddSingleton<ICodeSummarizerAgent, CodeSummarizerAgent>();
builder.Services.AddHostedService<SummarizerRpcServerWorker>();

await builder.Build().RunAsync();
