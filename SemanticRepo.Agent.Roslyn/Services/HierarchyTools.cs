using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Hierarchy;

namespace SemanticRepo.Agent.Roslyn.Services;

public sealed class HierarchyTools(IRoslynWorkspaceManager manager) : IHierarchyTools
{
    public async Task<TypeHierarchyDto?> GetTypeHierarchyAsync(GetTypeHierarchyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var type = await SymbolUtilities.FindTypeAsync(workspace, request.TypeFullName, ct);
        if (type is null)
            return null;
        IEnumerable<INamedTypeSymbol> derived = [];
        if (request.IncludeDerivedTypes)
        {
            derived = type.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindDerivedInterfacesAsync(type, workspace.Solution, cancellationToken: ct)
                : type.TypeKind == TypeKind.Class
                    ? await SymbolFinder.FindDerivedClassesAsync(type, workspace.Solution, cancellationToken: ct)
                    : [];
        }
        return new TypeHierarchyDto(SymbolUtilities.Name(type), type.BaseType is null ? null : SymbolUtilities.Name(type.BaseType),
            type.AllInterfaces.Select(SymbolUtilities.Name).Order(StringComparer.Ordinal).ToArray(), Names(derived, workspace));
    }

    public async Task<InterfaceImplementationsDto> GetInterfaceImplementationsAsync(GetInterfaceImplementationsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var type = await SymbolUtilities.FindTypeAsync(workspace, request.InterfaceFullName, ct);
        if (type is null)
            return new InterfaceImplementationsDto(request.InterfaceFullName, []);
        if (type.TypeKind != TypeKind.Interface)
            throw new ArgumentException($"'{request.InterfaceFullName}' is not an interface.", nameof(request));
        var implementations = await SymbolFinder.FindImplementationsAsync(type, workspace.Solution, cancellationToken: ct);
        return new InterfaceImplementationsDto(SymbolUtilities.Name(type), Names(implementations.OfType<INamedTypeSymbol>()
            .Where(implementation => implementation.TypeKind == TypeKind.Class), workspace));
    }

    private static string[] Names(IEnumerable<INamedTypeSymbol> types, RoslynWorkspaceLease workspace)
        => types.Where(type => SymbolUtilities.InScope(type, workspace)).Select(SymbolUtilities.Name)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
