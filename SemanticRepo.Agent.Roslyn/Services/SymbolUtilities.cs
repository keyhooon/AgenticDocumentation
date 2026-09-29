using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Syntax;

namespace SemanticRepo.Agent.Roslyn.Services;

internal static class SymbolUtilities
{
    private static readonly SymbolDisplayFormat QualifiedFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    public static string Name(ISymbol symbol) => symbol.ToDisplayString(QualifiedFormat);
    public static string Normalize(string name) => name.StartsWith("global::", StringComparison.Ordinal) ? name[8..] : name;

    public static SourceSpanDto? Span(Location? location)
    {
        if (location is null || !location.IsInSource)
            return null;
        var span = location.GetLineSpan();
        return new SourceSpanDto(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }

    public static SourceSpanDto? Span(ISymbol symbol) => Span(symbol.Locations.FirstOrDefault(location => location.IsInSource));

    public static AccessibilityDto Accessibility(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => AccessibilityDto.Public,
        Microsoft.CodeAnalysis.Accessibility.Private => AccessibilityDto.Private,
        Microsoft.CodeAnalysis.Accessibility.Protected => AccessibilityDto.Protected,
        Microsoft.CodeAnalysis.Accessibility.Internal => AccessibilityDto.Internal,
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => AccessibilityDto.ProtectedInternal,
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => AccessibilityDto.PrivateProtected,
        _ => AccessibilityDto.NotApplicable
    };

    public static string[] Attributes(ISymbol symbol) => symbol.GetAttributes()
        .Where(attribute => attribute.AttributeClass is not null).Select(attribute => Name(attribute.AttributeClass!)).ToArray();

    public static async Task<IReadOnlyList<INamedTypeSymbol>> TypesAsync(RoslynWorkspaceLease workspace, CancellationToken ct)
    {
        var types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var document in workspace.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            var root = await document.GetSyntaxRootAsync(ct);
            var model = await workspace.GetSemanticModelAsync(document, ct);
            if (root is null || model is null)
                continue;
            foreach (var node in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                ct.ThrowIfCancellationRequested();
                if (model.GetDeclaredSymbol(node, ct) is INamedTypeSymbol type)
                    types.Add(type);
            }
        }
        return types.OrderBy(Name, StringComparer.Ordinal).ToArray();
    }

    public static async Task<INamedTypeSymbol?> FindTypeAsync(RoslynWorkspaceLease workspace, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = Normalize(name);
        var types = await TypesAsync(workspace, ct);
        var matches = types.Where(type => Name(type) == name || MetadataName(type) == name).ToArray();
        if (matches.Length > 1)
            throw new ArgumentException($"Type '{name}' is ambiguous. Narrow the scope to a project.");
        if (matches.Length == 1)
            return matches[0];
        foreach (var project in workspace.Documents.Select(document => document.Project).DistinctBy(project => project.Id))
        {
            var compilation = await workspace.GetCompilationAsync(project, ct);
            if (compilation?.GetTypeByMetadataName(name) is { } type)
                return type;
        }
        return null;
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
            return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        return type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace + "." + type.MetadataName;
    }

    public static bool InScope(ISymbol symbol, RoslynWorkspaceLease workspace)
        => symbol.Locations.Any(location => location.SourceTree is not null
            && workspace.Documents.Any(document => document.FilePath == location.SourceTree.FilePath));
}
