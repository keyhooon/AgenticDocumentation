using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Contracts.Roslyn.Symbols
{
    /// <summary>
    /// Represents an occurrence or reference to a symbol within the source code.
    /// </summary>
    public sealed record SymbolReferenceDto(
        string SymbolDisplayName,
        string ContainingTypeFullName,
        string ContainingMemberName,
        SourceSpanDto Span,
        string ReferenceKind // "Read" | "Write" | "Invoke" | "TypeUse" | ...
    );

    /// <summary>
    /// Request parameters to locate all usages and references of a specific symbol.
    /// </summary>
    public sealed record FindSymbolReferencesRequest(
        CodeScopeDto Scope,
        string SymbolFullName,
        int MaxResults = 5000
    );

    /// <summary>
    /// Result containing all discovered references for the requested symbol.
    /// </summary>
    public sealed record FindSymbolReferencesResponse(
        string SymbolFullName,
        IReadOnlyList<SymbolReferenceDto> References
    );

    /// <summary>
    /// Semantic tools for symbol resolution and call/usage analysis.
    /// </summary>
    public interface ISymbolTools
    {
        /// <summary>
        /// Finds all locations where the given symbol is referenced or invoked across the target scope.
        /// </summary>
        Task<FindSymbolReferencesResponse> FindSymbolReferencesAsync(FindSymbolReferencesRequest request, CancellationToken ct = default);
    }
}