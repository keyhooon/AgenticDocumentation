using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Syntax;

namespace SemanticRepo.Agent.Roslyn.Services;

public sealed class SyntaxTools(IRoslynWorkspaceManager manager) : ISyntaxTools
{
    public async Task<ProjectSkeletonDto> GetProjectSkeletonAsync(GetProjectSkeletonRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var types = await SymbolUtilities.TypesAsync(workspace, ct);
        return new ProjectSkeletonDto(workspace.ScopeId, types
            .Where(type => request.IncludePrivateMembers || type.DeclaredAccessibility != Accessibility.Private)
            .Select(type => Describe(type, request.IncludePrivateMembers)).ToArray());
    }

    public async Task<TypeDeclarationDto?> GetTypeDeclarationAsync(GetTypeDeclarationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var type = await SymbolUtilities.FindTypeAsync(workspace, request.TypeFullName, ct);
        return type is null ? null : Describe(type, true);
    }

    public async Task<MethodBodyDto?> GetMethodBodyAsync(GetMethodBodyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MethodName);
        ArgumentOutOfRangeException.ThrowIfNegative(request.OverloadIndex);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var type = await SymbolUtilities.FindTypeAsync(workspace, request.ContainingTypeFullName, ct);
        var method = type?.GetMembers().OfType<IMethodSymbol>()
            .Where(method => method.Name == request.MethodName && !method.IsImplicitlyDeclared)
            .OrderBy(method => method.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(method => method.Locations.FirstOrDefault()?.SourceSpan.Start)
            .ElementAtOrDefault(request.OverloadIndex);
        if (method is null)
            return null;
        method = method.PartialImplementationPart ?? method;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(ct);
            SyntaxNode? body = declaration switch
            {
                BaseMethodDeclarationSyntax methodDeclaration => (SyntaxNode?)methodDeclaration.Body ?? methodDeclaration.ExpressionBody,
                AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
                _ => null
            };
            if (body is not null)
                return new MethodBodyDto(SymbolUtilities.Name(type!), request.MethodName, request.OverloadIndex,
                    LanguageNames.CSharp, body.ToString(), SymbolUtilities.Span(body.GetLocation()));
        }
        return null;
    }

    private static TypeDeclarationDto Describe(INamedTypeSymbol type, bool includePrivate)
    {
        var members = type.GetMembers().Where(member => !member.IsImplicitlyDeclared
            && (includePrivate || member.DeclaredAccessibility != Accessibility.Private)).ToArray();
        var bases = new List<string>();
        if (type.BaseType is { } baseType)
            bases.Add(SymbolUtilities.Name(baseType));
        bases.AddRange(type.Interfaces.Select(SymbolUtilities.Name));
        return new TypeDeclarationDto(
            type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            type.Name, SymbolUtilities.Name(type), type.IsRecord ? TypeKindDto.Record : type.TypeKind switch
            {
                TypeKind.Interface => TypeKindDto.Interface,
                TypeKind.Struct => TypeKindDto.Struct,
                TypeKind.Enum => TypeKindDto.Enum,
                TypeKind.Delegate => TypeKindDto.Delegate,
                _ => TypeKindDto.Class
            }, SymbolUtilities.Accessibility(type), type.IsStatic, type.IsAbstract, type.IsSealed,
            bases, SymbolUtilities.Attributes(type),
            members.OfType<IMethodSymbol>().Where(method => method.MethodKind is MethodKind.Ordinary
                or MethodKind.ExplicitInterfaceImplementation or MethodKind.Constructor or MethodKind.StaticConstructor
                or MethodKind.Destructor or MethodKind.UserDefinedOperator or MethodKind.Conversion)
                .Select(method => new MethodSignatureDto(method.Name, SymbolUtilities.Name(method.ReturnType),
                    SymbolUtilities.Accessibility(method), method.IsStatic, method.IsAbstract, method.IsVirtual,
                    method.IsAsync, method.Parameters.Select(Parameter).ToArray(),
                    method.TypeParameters.Select(parameter => parameter.Name).ToArray(), SymbolUtilities.Attributes(method))).ToArray(),
            members.OfType<IPropertySymbol>().Select(property => new PropertySignatureDto(property.Name,
                SymbolUtilities.Name(property.Type), SymbolUtilities.Accessibility(property), property.IsStatic,
                property.GetMethod is not null && (includePrivate || property.GetMethod.DeclaredAccessibility != Accessibility.Private),
                property.SetMethod is not null && (includePrivate || property.SetMethod.DeclaredAccessibility != Accessibility.Private),
                SymbolUtilities.Attributes(property))).ToArray(),
            members.OfType<IFieldSymbol>().Select(field => new FieldSignatureDto(field.Name, SymbolUtilities.Name(field.Type),
                SymbolUtilities.Accessibility(field), field.IsStatic, field.IsReadOnly, SymbolUtilities.Attributes(field))).ToArray(),
            SymbolUtilities.Span(type));
    }

    private static ParameterDto Parameter(IParameterSymbol parameter)
    {
        var defaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue switch
        {
            null => "null",
            string text => SymbolDisplay.FormatLiteral(text, true),
            char character => SymbolDisplay.FormatLiteral(character, true),
            bool boolean => boolean ? "true" : "false",
            var value => Convert.ToString(value, CultureInfo.InvariantCulture)
        } : null;
        return new ParameterDto(parameter.Name, SymbolUtilities.Name(parameter.Type), parameter.IsOptional,
            defaultValue, parameter.IsParams, parameter.RefKind == RefKind.None ? null : parameter.RefKind.ToString().ToLowerInvariant());
    }
}
