using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Contracts.Roslyn.Metadata;

namespace SemanticRepo.Agent.Roslyn.Services;

public sealed class CompilerMetadataTools(IRoslynWorkspaceManager manager) : ICompilerMetadataTools
{
    public async Task<DIRegistrationsDto> GetDIRegistrationsAsync(GetDIRegistrationsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var registrations = new List<DIRegistrationDto>();
        foreach (var document in workspace.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            var root = await document.GetSyntaxRootAsync(ct);
            var model = await workspace.GetSemanticModelAsync(document, ct);
            if (root is null || model is null)
                continue;
            foreach (var syntax in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (model.GetOperation(syntax, ct) is not IInvocationOperation invocation)
                    continue;
                var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                var lifetime = Lifetime(method.Name);
                if (lifetime is null || !IsRegistration(method, request.IncludeThirdParty))
                    continue;
                var typeArguments = invocation.TargetMethod.TypeArguments;
                ITypeSymbol? service = typeArguments.Length > 0 ? typeArguments[0] : null;
                ITypeSymbol? implementation = typeArguments.Length > 1 ? typeArguments[1] : null;
                var hasFactoryOrInstance = false;
                foreach (var argument in invocation.Arguments)
                {
                    var value = Unwrap(argument.Value);
                    var parameterName = argument.Parameter?.Name ?? "";
                    if (value is ITypeOfOperation typeOf)
                    {
                        if (parameterName is "serviceType" or "service")
                            service = typeOf.TypeOperand;
                        else if (parameterName is "implementationType" or "implementation")
                            implementation = typeOf.TypeOperand;
                    }
                    else if (parameterName.Contains("factory", StringComparison.OrdinalIgnoreCase))
                    {
                        hasFactoryOrInstance = true;
                        implementation ??= FactoryType(value);
                    }
                    else if (parameterName.Contains("instance", StringComparison.OrdinalIgnoreCase))
                    {
                        hasFactoryOrInstance = true;
                        implementation ??= value.Type;
                    }
                }
                if (service is null || service.TypeKind == TypeKind.Error)
                    continue;
                if (!hasFactoryOrInstance)
                    implementation ??= service;
                registrations.Add(new DIRegistrationDto(lifetime.Value, SymbolUtilities.Name(service),
                    implementation is null ? "<unknown>" : SymbolUtilities.Name(implementation), SymbolUtilities.Span(syntax.GetLocation())));
            }
        }
        return new DIRegistrationsDto(registrations);
    }

    public async Task<IReadOnlyList<AttributedTypeDto>> GetAttributedTypesAsync(GetAttributedTypesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AttributeFullName);
        using var workspace = await manager.AcquireAsync(request.Scope, ct);
        var name = SymbolUtilities.Normalize(request.AttributeFullName);
        var results = new List<AttributedTypeDto>();
        foreach (var type in await SymbolUtilities.TypesAsync(workspace, ct))
        {
            ct.ThrowIfCancellationRequested();
            for (var current = type; current is not null; current = request.Inherit ? current.BaseType : null)
            {
                var attribute = current.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass is { } attributeType
                    && (SymbolUtilities.Name(attributeType) == name || SymbolUtilities.Name(attributeType) == name + "Attribute")
                    && (SymbolEqualityComparer.Default.Equals(current, type) || IsInherited(attributeType)));
                if (attribute?.AttributeClass is not { } matched)
                    continue;
                results.Add(new AttributedTypeDto(SymbolUtilities.Name(type), SymbolUtilities.Name(matched), SymbolUtilities.Span(type)));
                break;
            }
        }
        return results;
    }

    private static bool IsInherited(INamedTypeSymbol attribute)
    {
        for (var current = attribute; current is not null; current = current.BaseType)
        {
            var usage = current.GetAttributes().FirstOrDefault(candidate => candidate.AttributeClass is { } type
                && SymbolUtilities.Name(type) == "System.AttributeUsageAttribute");
            if (usage is not null)
                return !usage.NamedArguments.Any(argument => argument.Key == "Inherited" && argument.Value.Value is false);
        }
        return true;
    }

    private static ServiceLifetimeDto? Lifetime(string name) => name switch
    {
        "AddSingleton" or "TryAddSingleton" or "AddKeyedSingleton" => ServiceLifetimeDto.Singleton,
        "AddScoped" or "TryAddScoped" or "AddKeyedScoped" => ServiceLifetimeDto.Scoped,
        "AddTransient" or "TryAddTransient" or "AddKeyedTransient" => ServiceLifetimeDto.Transient,
        _ => null
    };

    private static bool IsRegistration(IMethodSymbol method, bool includeThirdParty)
    {
        if (!method.IsExtensionMethod || method.Parameters.Length == 0
            || SymbolUtilities.Name(method.Parameters[0].Type) != "Microsoft.Extensions.DependencyInjection.IServiceCollection")
            return false;
        var assembly = method.ContainingAssembly.Name;
        return includeThirdParty || assembly is "Microsoft.Extensions.DependencyInjection.Abstractions" or "Microsoft.Extensions.DependencyInjection";
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;
        return operation;
    }

    private static ITypeSymbol? FactoryType(IOperation operation)
    {
        if (operation is IDelegateCreationOperation creation)
            operation = creation.Target;
        if (operation is IMethodReferenceOperation reference)
            return reference.Method.ReturnType;
        if (operation is not IAnonymousFunctionOperation lambda)
            return null;
        var returnedTypes = Returns(lambda.Body).Where(result => result.ReturnedValue is not null)
            .Select(result => Unwrap(result.ReturnedValue!).Type).OfType<ITypeSymbol>()
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        return returnedTypes.Length == 1 ? returnedTypes[0] : null;
    }

    private static IEnumerable<IReturnOperation> Returns(IOperation operation)
    {
        if (operation is IReturnOperation returned)
            yield return returned;
        foreach (var child in operation.ChildOperations)
        {
            if (child is IAnonymousFunctionOperation or ILocalFunctionOperation)
                continue;
            foreach (var result in Returns(child))
                yield return result;
        }
    }
}
