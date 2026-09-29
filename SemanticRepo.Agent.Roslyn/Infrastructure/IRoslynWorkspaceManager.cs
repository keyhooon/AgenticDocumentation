using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Agent.Roslyn.Infrastructure;

public interface IRoslynWorkspaceManager : IDisposable
{
    string Status { get; }
    int LoadedProjectsCount { get; }
    Task<RoslynWorkspaceLease> AcquireAsync(CodeScopeDto scope, CancellationToken ct = default);
}

public sealed class RoslynWorkspaceLease : IDisposable
{
    private Action? release;
    private readonly WorkspaceSnapshot snapshot;

    internal RoslynWorkspaceLease(WorkspaceSnapshot snapshot, Action release)
    {
        this.snapshot = snapshot;
        this.release = release;
    }

    public string ScopeId => snapshot.ScopeId;
    public Solution Solution => snapshot.Solution;
    public IReadOnlyList<Document> Documents => snapshot.Documents;

    public Task<Compilation?> GetCompilationAsync(Project project, CancellationToken ct = default)
        => snapshot.Compilations.GetOrAdd(project.Id, _ =>
            new Lazy<Task<Compilation?>>(() => project.GetCompilationAsync())).Value.WaitAsync(ct);

    public Task<SemanticModel?> GetSemanticModelAsync(Document document, CancellationToken ct = default)
        => snapshot.Models.GetOrAdd(document.Id, _ =>
            new Lazy<Task<SemanticModel?>>(() => document.GetSemanticModelAsync())).Value.WaitAsync(ct);

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

internal sealed class WorkspaceSnapshot : IDisposable
{
    private int references = 1;

    public required string ScopeId { get; init; }
    public required Workspace Workspace { get; init; }
    public required Solution Solution { get; init; }
    public required IReadOnlyList<Document> Documents { get; init; }
    public required string Fingerprint { get; set; }
    public ConcurrentDictionary<ProjectId, Lazy<Task<Compilation?>>> Compilations { get; } = new();
    public ConcurrentDictionary<DocumentId, Lazy<Task<SemanticModel?>>> Models { get; } = new();

    public RoslynWorkspaceLease Acquire()
    {
        Interlocked.Increment(ref references);
        return new RoslynWorkspaceLease(this, Dispose);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) == 0)
            Workspace.Dispose();
    }
}
