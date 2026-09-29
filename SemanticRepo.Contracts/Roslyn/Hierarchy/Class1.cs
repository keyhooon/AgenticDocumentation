using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Contracts.Roslyn.Hierarchy
{
    /// <summary>
    /// Represents inheritance and interface implementation hierarchy for a given type.
    /// </summary>
    public sealed record TypeHierarchyDto(
        string TypeFullName,
        string? BaseTypeFullName,
        IReadOnlyList<string> ImplementedInterfaces,
        IReadOnlyList<string> DerivedTypes
    );

    /// <summary>
    /// Request parameters to inspect type inheritance hierarchies.
    /// </summary>
    public sealed record GetTypeHierarchyRequest(
        CodeScopeDto Scope,
        string TypeFullName,
        bool IncludeDerivedTypes = true
    );

    /// <summary>
    /// Request parameters to discover interface implementations.
    /// </summary>
    public sealed record GetInterfaceImplementationsRequest(
        CodeScopeDto Scope,
        string InterfaceFullName
    );

    /// <summary>
    /// Discovered implementations for a targeted interface.
    /// </summary>
    public sealed record InterfaceImplementationsDto(
        string InterfaceFullName,
        IReadOnlyList<string> Implementations
    );

    /// <summary>
    /// Deterministic tools for resolving inheritance and interface relationships.
    /// </summary>
    public interface IHierarchyTools
    {
        /// <summary>
        /// Traverses the type inheritance chain and finds derived subclasses.
        /// </summary>
        Task<TypeHierarchyDto?> GetTypeHierarchyAsync(GetTypeHierarchyRequest request, CancellationToken ct = default);

        /// <summary>
        /// Discovers all concrete and abstract classes implementing the given interface.
        /// </summary>
        Task<InterfaceImplementationsDto> GetInterfaceImplementationsAsync(GetInterfaceImplementationsRequest request, CancellationToken ct = default);
    }
}
