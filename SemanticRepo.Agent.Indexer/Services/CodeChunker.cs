using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Infrastructure;

namespace SemanticRepo.Agent.Indexer.Services;

public sealed class CodeChunker(IOptions<IndexerOptions> options)
{
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        var source = SourceText.From(content);
        var isCSharp = Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var root = isCSharp ? CSharpSyntaxTree.ParseText(source, cancellationToken: ct).GetRoot(ct) : null;
        var boundaries = root?.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Select(member => member.SpanStart).Distinct().Order().ToArray() ?? [];
        var chunks = new List<CodeChunk>();
        var start = 0;
        while (start < content.Length)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + options.Value.ChunkCharacters, content.Length);
            if (end < content.Length)
            {
                var minimum = start + options.Value.ChunkCharacters / 2;
                var boundary = Array.BinarySearch(boundaries, end);
                if (boundary < 0)
                    boundary = ~boundary - 1;
                if (boundary >= 0 && boundaries[boundary] >= minimum)
                    end = boundaries[boundary];
                else
                {
                    var newline = content.LastIndexOf('\n', end - 1, end - start);
                    if (newline >= minimum)
                        end = newline + 1;
                }
                if (char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end]))
                    end--;
            }
            var text = content[start..end];
            if (!string.IsNullOrWhiteSpace(text))
            {
                chunks.Add(new CodeChunk(filePath, source.Lines.GetLineFromPosition(start).LineNumber + 1,
                    source.Lines.GetLineFromPosition(end - 1).LineNumber + 1,
                    isCSharp ? "C#" : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                    root is null ? null : SymbolAt(root, start), text));
            }
            start = end;
        }
        return chunks;
    }

    private static string? SymbolAt(SyntaxNode root, int position)
    {
        var ancestors = root.FindToken(position).Parent?.AncestorsAndSelf().Reverse().ToArray() ?? [];
        var parts = ancestors.Select(node => node switch
        {
            BaseNamespaceDeclarationSyntax declaration => declaration.Name.ToString(),
            BaseTypeDeclarationSyntax declaration => declaration.Identifier.Text,
            DelegateDeclarationSyntax declaration => declaration.Identifier.Text,
            MethodDeclarationSyntax declaration => declaration.Identifier.Text + declaration.ParameterList,
            ConstructorDeclarationSyntax declaration => declaration.Identifier.Text + declaration.ParameterList,
            PropertyDeclarationSyntax declaration => declaration.Identifier.Text,
            _ => null
        }).OfType<string>().ToArray();
        return parts.Length == 0 ? null : string.Join('.', parts);
    }
}
