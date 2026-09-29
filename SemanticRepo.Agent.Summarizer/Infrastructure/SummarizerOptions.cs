namespace SemanticRepo.Agent.Summarizer.Infrastructure;

public sealed class SummarizerOptions
{
    public int MaxEvidenceCharacters { get; set; } = 24000;
    public int MaxEvidenceItems { get; set; } = 64;
    public int MaxConcurrentRequests { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 300;
}

public sealed class SummaryModelOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public int TimeoutSeconds { get; set; } = 180;
    public int ContextTokens { get; set; } = 16384;
    public int MaxOutputTokens { get; set; } = 2048;
}

public sealed class AgentEndpointOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class SummarizerRpcOptions
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5057;
    public int MaxConnections { get; set; } = 32;
}
