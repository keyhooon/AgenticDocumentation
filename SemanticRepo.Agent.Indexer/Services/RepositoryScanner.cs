using System.Text;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Infrastructure;
using SemanticRepo.Contracts.Indexing;

namespace SemanticRepo.Agent.Indexer.Services;

public sealed record RepositoryScan(IReadOnlyList<string> Files, IReadOnlyList<IndexingErrorDto> Errors);

public sealed class RepositoryScanner(IOptions<IndexerOptions> options)
{
    public RepositoryScan Scan(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository directory does not exist: {root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Use the physical repository path instead of a symbolic link.", nameof(root));
        var extensions = options.Value.IncludedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = options.Value.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var errors = new List<IndexingErrorDto>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Name.StartsWith('.') || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (!excluded.Contains(entry.Name))
                            directories.Push(entry.FullName);
                    }
                    else if (extensions.Contains(entry.Extension))
                    {
                        if (((FileInfo)entry).Length <= options.Value.MaxFileBytes)
                            files.Add(entry.FullName);
                        else
                            errors.Add(new IndexingErrorDto(RelativePath(root, entry.FullName), "File exceeds Indexer:MaxFileBytes."));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(new IndexingErrorDto(RelativePath(root, directory), exception.Message));
            }
        }
        return new RepositoryScan(files.Order(StringComparer.Ordinal).ToArray(), errors);
    }

    public async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > options.Value.MaxFileBytes)
            throw new IOException("File exceeds Indexer:MaxFileBytes.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Contains('\0'))
            throw new IOException("Binary content cannot be indexed as source text.");
        return text;
    }

    public static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
