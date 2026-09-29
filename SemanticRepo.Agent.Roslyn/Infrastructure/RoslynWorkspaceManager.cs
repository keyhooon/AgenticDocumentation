using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Agent.Roslyn.Infrastructure;

public sealed class RoslynWorkspaceManager(ILogger<RoslynWorkspaceManager> logger) : IRoslynWorkspaceManager
{
    private static readonly object RegistrationLock = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, WorkspaceSnapshot> snapshots = new();
    private volatile string status = "Idle";
    private int loadedProjectsCount;
    private bool disposed;

    public string Status => status;
    public int LoadedProjectsCount => Volatile.Read(ref loadedProjectsCount);

    public async Task<RoslynWorkspaceLease> AcquireAsync(CodeScopeDto scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsValid(out var error))
            throw new ArgumentException(error, nameof(scope));
        var root = Path.GetFullPath(scope.WorkingDirectory ?? Directory.GetCurrentDirectory());
        string? Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, root);
        var solutionPath = Resolve(scope.SolutionPath);
        var projectPath = Resolve(scope.ProjectPath);
        var files = (scope.FilePaths ?? []).Select(path => Resolve(path)
            ?? throw new ArgumentException("File paths cannot be empty.", nameof(scope)))
            .Distinct(PathComparer).Order(PathComparer).ToArray();
        var key = JsonSerializer.Serialize(new { solutionPath, projectPath, files });

        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            snapshots.TryGetValue(key, out var previous);
            var fingerprint = Fingerprint(solutionPath, projectPath, files, previous?.Solution, ct);
            if (previous is not null && previous.Fingerprint == fingerprint)
                return previous.Acquire();

            status = "Loading";
            var snapshot = await LoadAsync(key, solutionPath, projectPath, files, ct);
            try
            {
                snapshot.Fingerprint = Fingerprint(solutionPath, projectPath, files, snapshot.Solution, ct);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
            snapshots[key] = snapshot;
            previous?.Dispose();
            if (snapshots.Count > 4)
            {
                var oldest = snapshots.First(entry => entry.Key != key);
                snapshots.Remove(oldest.Key);
                oldest.Value.Dispose();
            }
            loadedProjectsCount = snapshots.Values.Sum(value => value.Solution.ProjectIds.Count);
            return snapshot.Acquire();
        }
        finally
        {
            status = disposed ? "Disposed" : snapshots.Count > 0 ? "Loaded" : "Idle";
            gate.Release();
        }
    }

    private async Task<WorkspaceSnapshot> LoadAsync(string key, string? solutionPath, string? projectPath,
        string[] files, CancellationToken ct)
    {
        Workspace workspace;
        if (solutionPath is not null || projectPath is not null)
        {
            lock (RegistrationLock)
            {
                if (!MSBuildLocator.IsRegistered)
                    MSBuildLocator.RegisterDefaults();
            }
            workspace = CreateMSBuildWorkspace();
        }
        else
        {
            workspace = new AdhocWorkspace();
        }
        try
        {
            Solution solution;
            if (workspace is MSBuildWorkspace msbuild)
            {
                msbuild.WorkspaceFailed += (_, args) => logger.LogWarning("Workspace: {Message}", args.Diagnostic.Message);
                solution = solutionPath is not null
                    ? await msbuild.OpenSolutionAsync(solutionPath, cancellationToken: ct)
                    : (await msbuild.OpenProjectAsync(projectPath!, cancellationToken: ct)).Solution;
            }
            else
            {
                var projectId = ProjectId.CreateNewId();
                var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => MetadataReference.CreateFromFile(path));
                solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(),
                    "Adhoc", "Adhoc", LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    parseOptions: new CSharpParseOptions(LanguageVersion.Latest), metadataReferences: references));
                foreach (var file in files)
                {
                    var text = SourceText.From(await File.ReadAllTextAsync(file, ct), Encoding.UTF8);
                    solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(file), text, filePath: file);
                }
                workspace.TryApplyChanges(solution);
                solution = workspace.CurrentSolution;
            }
            var projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp);
            if (projectPath is not null)
                projects = projects.Where(project => PathComparer.Equals(project.FilePath, projectPath));
            var selectedProjects = projects.ToArray();
            if (selectedProjects.Length == 0)
                throw new ArgumentException("The scope contains no matching C# project.");
            var documents = selectedProjects.SelectMany(project => project.Documents).ToArray();
            if (files.Length > 0)
            {
                var fileSet = files.ToHashSet(PathComparer);
                documents = documents.Where(document => document.FilePath is not null && fileSet.Contains(document.FilePath)).ToArray();
                if (files.Any(file => !documents.Any(document => PathComparer.Equals(document.FilePath, file))))
                    throw new ArgumentException("Some requested files are not documents in the selected project or solution.");
            }
            return new WorkspaceSnapshot
            {
                ScopeId = solutionPath ?? projectPath ?? key,
                Workspace = workspace,
                Solution = solution,
                Documents = documents,
                Fingerprint = ""
            };
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static MSBuildWorkspace CreateMSBuildWorkspace() => MSBuildWorkspace.Create();

    private static string Fingerprint(string? solutionPath, string? projectPath, string[] files,
        Solution? solution, CancellationToken ct)
    {
        var paths = new HashSet<string>(files, PathComparer);
        var projectFiles = (solution?.Projects.Select(project => project.FilePath).OfType<string>() ?? [])
            .Concat(new[] { solutionPath, projectPath }.OfType<string>()).ToArray();
        foreach (var file in projectFiles)
        {
            paths.Add(file);
            var directory = Path.GetDirectoryName(file)!;
            foreach (var candidate in EnumerateInputs(directory, ct))
                paths.Add(candidate);
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config" })
                    paths.Add(Path.Combine(parent.FullName, name));
            }
        }
        if (solution is not null)
        {
            foreach (var document in solution.Projects.SelectMany(project => project.Documents))
                if (document.FilePath is not null)
                    paths.Add(document.FilePath);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.Order(PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{info.Exists}\0{(info.Exists ? info.Length : 0)}\0{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> EnumerateInputs(string directory, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetExtension(file) is ".cs" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".json")
                yield return file;
        }
        var assets = Path.Combine(directory, "obj", "project.assets.json");
        if (File.Exists(assets))
            yield return assets;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".git" or ".vs" or "node_modules"
                || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in EnumerateInputs(child, ct))
                yield return file;
        }
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var snapshot in snapshots.Values)
                snapshot.Dispose();
            snapshots.Clear();
            loadedProjectsCount = 0;
            status = "Disposed";
        }
        finally
        {
            gate.Release();
        }
    }
}
