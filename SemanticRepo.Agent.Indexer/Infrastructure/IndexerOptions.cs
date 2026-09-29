namespace SemanticRepo.Agent.Indexer.Infrastructure;

public sealed class IndexerOptions
{
    public string? RepositoryPath { get; set; }
    public int ScanIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 16;
    public int ChunkCharacters { get; set; } = 2400;
    public int MaxFileBytes { get; set; } = 1_048_576;
    public string[] IncludedExtensions { get; set; } =
    [
        ".cs", ".fs", ".vb", ".razor", ".cshtml", ".csproj", ".fsproj", ".props", ".targets",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".go", ".rs", ".c", ".cpp", ".h",
        ".html", ".css", ".scss", ".sql", ".md"
    ];
    public string[] ExcludedDirectories { get; set; } =
    ["bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target", "venv", "__pycache__"];
}

public sealed class EmbeddingOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class QdrantOptions
{
    public string Endpoint { get; set; } = "http://localhost:6333";
    public string Collection { get; set; } = "semanticrepo_code";
    public string? ApiKey { get; set; }
}

public sealed class IndexerRpcOptions
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5056;
    public int MaxConnections { get; set; } = 32;
}
