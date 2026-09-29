using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Hierarchy;
using SemanticRepo.Contracts.Roslyn.Metadata;
using SemanticRepo.Contracts.Roslyn.Symbols;
using SemanticRepo.Contracts.Roslyn.Syntax;

namespace SemanticRepo.Contracts.Roslyn.Hub
{
    /// <summary>
    /// Runtime capability and health descriptor for the Roslyn worker engine.
    /// </summary>
    public sealed record RoslynEngineStatusDto(
        bool IsReady,
        string WorkspaceStatus, // e.g. "Loaded", "Compiling", "Idle"
        int LoadedProjectsCount,
        IReadOnlyList<string> AvailableTools
    );

    /// <summary>
    /// Unified RPC entry point aggregating all Roslyn AST and semantic analysis capabilities.
    /// Exposes tools to agent orchestrators and external consumers over StreamJsonRpc.
    /// </summary>
    public interface IRoslynToolHub :
        ISyntaxTools,
        IHierarchyTools,
        ISymbolTools,
        ICompilerMetadataTools
    {
        /// <summary>
        /// Validates connection health and returns the operational state of the Roslyn workspace.
        /// </summary>
        Task<RoslynEngineStatusDto> GetEngineStatusAsync(CancellationToken ct = default);

        /// <summary>
        /// Explicitly triggers indexing or pre-compilation for the specified scope.
        /// </summary>
        Task<bool> WarmupScopeAsync(CodeScopeDto scope, CancellationToken ct = default);
    }
}