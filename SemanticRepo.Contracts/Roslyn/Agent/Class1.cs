using SemanticRepo.Contracts.Roslyn.Common;


namespace SemanticRepo.Contracts.Roslyn.Agent
{
    /// <summary>
    /// Records evidence and facts extracted by the Roslyn Agent from the codebase.
    /// </summary>
    public sealed record RoslynAgentEvidenceDto(
        string Kind,           // "MethodBody" | "Reference" | "TypeDecl" | "CallGraph" | "DI"
        string Description,    // Concise summary of the discovered fact
        string? FilePath = null,
        int? Line = null,
        string? Snippet = null
    );

    /// <summary>
    /// Audit log of tools invoked by the agent during the ReAct loop.
    /// </summary>
    public sealed record RoslynAgentToolCallDto(
        string ToolName,       // e.g., "GetMethodBody" or "FindSymbolReferences"
        string ArgumentsJson,  // Arguments passed to the tool
        bool Succeeded,
        string? SummaryResult = null
    );

    /// <summary>
    /// Final structured response from the Roslyn Semantic Agent to other agents or clients.
    /// </summary>
    public sealed record RoslynAgentAnswerDto(
        string AnswerMarkdown,
        IReadOnlyList<RoslynAgentEvidenceDto> Evidence,
        IReadOnlyList<RoslynAgentToolCallDto> ToolCalls,
        IReadOnlyList<string> FollowUpQuestions
    );

    /// <summary>
    /// Request parameters for querying the Roslyn Semantic Agent.
    /// </summary>
    public sealed record AskRoslynAgentRequest(
        CodeScopeDto Scope,
        string Query,
        int MaxToolIterations = 5,
        IReadOnlyList<string>? ContextKeywords = null
    );

    /// <summary>
    /// The autonomous semantic agent interface used to answer code-based queries 
    /// by orchestrating internal Roslyn tools.
    /// </summary>
    public interface IRoslynSemanticAgent
    {
        /// <summary>
        /// Executes an investigative loop to provide a grounded answer to the specified query.
        /// </summary>
        Task<RoslynAgentAnswerDto> AskAsync(AskRoslynAgentRequest request, CancellationToken ct = default);
    }
}
