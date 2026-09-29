var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var qdrant = builder.AddContainer("code-vectors", "qdrant/qdrant", "v1.14.1")
    .WithHttpEndpoint(targetPort: 6333, name: "http")
    .WithVolume("semanticrepo-code-vectors", "/qdrant/storage");

builder.AddProject<Projects.SemanticRepo_Agent_Indexer>("code-indexer")
    .WithEnvironment("Qdrant__Endpoint", qdrant.GetEndpoint("http"))
    .WithEnvironment("Embeddings__Endpoint", builder.Configuration["Embeddings:Endpoint"] ?? "http://localhost:11434")
    .WithEnvironment("Indexer__RepositoryPath", builder.Configuration["Indexer:RepositoryPath"] ?? "")
    .WaitFor(qdrant);

var apiService = builder.AddProject<Projects.AgenticDocumentation_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.AgenticDocumentation_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
