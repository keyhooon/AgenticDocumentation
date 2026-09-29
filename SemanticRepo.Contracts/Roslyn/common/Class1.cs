using System.Collections.Generic;

namespace SemanticRepo.Contracts.Roslyn.Common
{
    /// <summary>
    /// Represents the diagnostic severity level emitted during Roslyn analysis.
    /// </summary>
    public enum RoslynSeverity
    {
        /// <summary>Informational message with no negative impact.</summary>
        Info = 0,
        /// <summary>Potential issue or bad practice that does not break compilation.</summary>
        Warning = 1,
        /// <summary>Critical error preventing successful compilation or accurate analysis.</summary>
        Error = 2
    }

    /// <summary>
    /// Identifies an exact source code span within a physical file.
    /// </summary>
    public sealed record SourceSpanDto(
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn
    );

    /// <summary>
    /// Represents a compiler diagnostic, error, or warning.
    /// </summary>
    public sealed record DiagnosticDto(
        string Id,
        RoslynSeverity Severity,
        string Message,
        SourceSpanDto? Span
    );

    /// <summary>
    /// Encapsulates the outcome of a Roslyn tool operation, including potential diagnostics.
    /// </summary>
    public sealed record OperationResultDto(
        bool Success,
        string? Message = null,
        IReadOnlyList<DiagnosticDto>? Diagnostics = null
    )
    {
        public static OperationResultDto Ok(string? message = null)
            => new(true, message);

        public static OperationResultDto Fail(string message, IReadOnlyList<DiagnosticDto>? diagnostics = null)
            => new(false, message, diagnostics);
    }

    /// <summary>
    /// Defines the context boundaries for Roslyn analysis (Solution, Project, or isolated source files).
    /// </summary>
    public sealed record CodeScopeDto
    {
        /// <summary>Absolute path to the .sln file.</summary>
        public string? SolutionPath { get; init; }

        /// <summary>Absolute path to the .csproj file.</summary>
        public string? ProjectPath { get; init; }

        /// <summary>List of specific C# source files for ad-hoc analysis.</summary>
        public IReadOnlyList<string>? FilePaths { get; init; }

        /// <summary>Root directory for relative path resolution.</summary>
        public string? WorkingDirectory { get; init; }

        /// <summary>
        /// Validates that at least one target scope is specified.
        /// </summary>
        public bool IsValid(out string? error)
        {
            var hasAny = !string.IsNullOrWhiteSpace(SolutionPath)
                      || !string.IsNullOrWhiteSpace(ProjectPath)
                      || (FilePaths is { Count: > 0 });

            if (!hasAny)
            {
                error = "CodeScopeDto must specify at least SolutionPath, ProjectPath, or FilePaths.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
