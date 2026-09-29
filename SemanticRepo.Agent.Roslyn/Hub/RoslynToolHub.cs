using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Hierarchy;
using SemanticRepo.Contracts.Roslyn.Hub;
using SemanticRepo.Contracts.Roslyn.Metadata;
using SemanticRepo.Contracts.Roslyn.Symbols;
using SemanticRepo.Contracts.Roslyn.Syntax;

namespace SemanticRepo.Agent.Roslyn.Hub;

public sealed class RoslynToolHub(
    IRoslynWorkspaceManager manager,
    ISyntaxTools syntax,
    IHierarchyTools hierarchy,
    ISymbolTools symbols,
    ICompilerMetadataTools metadata,
    ILogger<RoslynToolHub> logger) : IRoslynToolHub
{
    private static readonly string[] AvailableTools =
    [
        nameof(GetProjectSkeletonAsync), nameof(GetTypeDeclarationAsync), nameof(GetMethodBodyAsync),
        nameof(GetTypeHierarchyAsync), nameof(GetInterfaceImplementationsAsync), nameof(FindSymbolReferencesAsync),
        nameof(GetDIRegistrationsAsync), nameof(GetAttributedTypesAsync), nameof(GetEngineStatusAsync), nameof(WarmupScopeAsync)
    ];

    public Task<ProjectSkeletonDto> GetProjectSkeletonAsync(GetProjectSkeletonRequest request, CancellationToken ct = default)
        => syntax.GetProjectSkeletonAsync(request, ct);

    public Task<TypeDeclarationDto?> GetTypeDeclarationAsync(GetTypeDeclarationRequest request, CancellationToken ct = default)
        => syntax.GetTypeDeclarationAsync(request, ct);

    public Task<MethodBodyDto?> GetMethodBodyAsync(GetMethodBodyRequest request, CancellationToken ct = default)
        => syntax.GetMethodBodyAsync(request, ct);

    public Task<TypeHierarchyDto?> GetTypeHierarchyAsync(GetTypeHierarchyRequest request, CancellationToken ct = default)
        => hierarchy.GetTypeHierarchyAsync(request, ct);

    public Task<InterfaceImplementationsDto> GetInterfaceImplementationsAsync(GetInterfaceImplementationsRequest request, CancellationToken ct = default)
        => hierarchy.GetInterfaceImplementationsAsync(request, ct);

    public Task<FindSymbolReferencesResponse> FindSymbolReferencesAsync(FindSymbolReferencesRequest request, CancellationToken ct = default)
        => symbols.FindSymbolReferencesAsync(request, ct);

    public Task<DIRegistrationsDto> GetDIRegistrationsAsync(GetDIRegistrationsRequest request, CancellationToken ct = default)
        => metadata.GetDIRegistrationsAsync(request, ct);

    public Task<IReadOnlyList<AttributedTypeDto>> GetAttributedTypesAsync(GetAttributedTypesRequest request, CancellationToken ct = default)
        => metadata.GetAttributedTypesAsync(request, ct);

    public Task<RoslynEngineStatusDto> GetEngineStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new RoslynEngineStatusDto(manager.Status != "Disposed", manager.Status,
            manager.LoadedProjectsCount, AvailableTools.ToArray()));
    }

    public async Task<bool> WarmupScopeAsync(CodeScopeDto scope, CancellationToken ct = default)
    {
        try
        {
            using var workspace = await manager.AcquireAsync(scope, ct);
            foreach (var project in workspace.Solution.Projects.Where(project => project.SupportsCompilation))
                if (await workspace.GetCompilationAsync(project, ct) is null)
                    return false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not warm up the requested Roslyn scope.");
            return false;
        }
    }
}
