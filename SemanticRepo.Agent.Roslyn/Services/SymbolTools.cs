using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Symbols;

namespace SemanticRepo.Agent.Roslyn.Services;

public sealed class SymbolTools(IRoslynWorkspaceManager manager) : ISymbolTools
{
    public async Task<FindSymbolReferencesResponse> FindSymbolReferencesAsync(FindSymbolReferencesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SymbolFullName);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MaxResults);
        if (request.MaxResults == 0)
            return new FindSymbolReferencesResponse(request.SymbolFullName, []);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var symbols = await ResolveAsync(workspace, request.SymbolFullName, ct);
        var documents = workspace.Documents.ToImmutableHashSet();
        var results = new List<SymbolReferenceDto>();
        var seen = new HashSet<(string Path, int Start, int Length)>();
        foreach (var symbol in symbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, workspace.Solution, documents, ct);
            foreach (var reference in references.SelectMany(reference => reference.Locations)
                .OrderBy(reference => reference.Document.FilePath, StringComparer.Ordinal)
                .ThenBy(reference => reference.Location.SourceSpan.Start))
            {
                ct.ThrowIfCancellationRequested();
                var span = SymbolUtilities.Span(reference.Location);
                if (span is null || !seen.Add((span.FilePath, reference.Location.SourceSpan.Start, reference.Location.SourceSpan.Length)))
                    continue;
                var model = await workspace.GetSemanticModelAsync(reference.Document, ct);
                var root = await reference.Document.GetSyntaxRootAsync(ct);
                if (model is null || root is null)
                    continue;
                var node = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
                var enclosing = model.GetEnclosingSymbol(reference.Location.SourceSpan.Start, ct);
                var containingType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
                results.Add(new SymbolReferenceDto(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    containingType is null ? "" : SymbolUtilities.Name(containingType),
                    enclosing is INamedTypeSymbol ? "" : enclosing?.Name ?? "", span, ReferenceKind(symbol, node)));
                if (results.Count == request.MaxResults)
                    return new FindSymbolReferencesResponse(request.SymbolFullName, results);
            }
        }
        return new FindSymbolReferencesResponse(request.SymbolFullName, results);
    }

    private static async Task<IReadOnlyList<ISymbol>> ResolveAsync(RoslynWorkspaceLease workspace, string name, CancellationToken ct)
    {
        name = SymbolUtilities.Normalize(name);
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var project in workspace.Documents.Select(document => document.Project).DistinctBy(project => project.Id))
        {
            var compilation = await workspace.GetCompilationAsync(project, ct);
            if (compilation is null)
                continue;
            if (name.Length > 2 && name[1] == ':')
                symbols.UnionWith(DocumentationCommentId.GetSymbolsForDeclarationId(name, compilation));
        }
        if (symbols.Count > 0)
            return symbols.ToArray();
        var type = await SymbolUtilities.FindTypeAsync(workspace, name, ct);
        if (type is not null)
            return [type];
        foreach (var declaredType in await SymbolUtilities.TypesAsync(workspace, ct))
        {
            foreach (var member in declaredType.GetMembers())
            {
                ct.ThrowIfCancellationRequested();
                if (member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == name
                    || SymbolUtilities.Name(declaredType) + "." + member.Name == name)
                    symbols.Add(member);
            }
        }
        return symbols.ToArray();
    }

    private static string ReferenceKind(ISymbol symbol, SyntaxNode node)
    {
        if (symbol is ITypeSymbol or INamespaceSymbol)
            return "TypeUse";
        if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
            node = access;
        if (node.Parent is MemberBindingExpressionSyntax binding)
            node = binding;
        if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
            return "Invoke";
        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
            return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "Write" : "ReadWrite";
        if (node.Parent is PrefixUnaryExpressionSyntax prefix && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
            || prefix.IsKind(SyntaxKind.PreDecrementExpression))
            || node.Parent is PostfixUnaryExpressionSyntax postfix && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
            || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
            return "ReadWrite";
        if (node.Parent is ArgumentSyntax argument)
        {
            if (argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                return "Write";
            if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                return "ReadWrite";
        }
        return "Read";
    }
}
