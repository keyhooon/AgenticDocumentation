using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Contracts.Roslyn.Syntax
{
    /// <summary>
    /// Categorization of declared types in C#.
    /// </summary>
    public enum TypeKindDto
    {
        Class = 0,
        Interface = 1,
        Struct = 2,
        Record = 3,
        Enum = 4,
        Delegate = 5
    }

    /// <summary>
    /// Member and type visibility levels.
    /// </summary>
    public enum AccessibilityDto
    {
        NotApplicable = 0,
        Private = 1,
        Protected = 2,
        Internal = 3,
        ProtectedInternal = 4,
        Public = 5,
        PrivateProtected = 6
    }

    /// <summary>
    /// Represents a method or local function parameter.
    /// </summary>
    public sealed record ParameterDto(
        string Name,
        string Type,
        bool IsOptional = false,
        string? DefaultValue = null,
        bool IsParams = false,
        string? RefKind = null // "ref" | "out" | "in" | null
    );

    /// <summary>
    /// Represents the declaration signature of a method.
    /// </summary>
    public sealed record MethodSignatureDto(
        string Name,
        string ReturnType,
        AccessibilityDto Accessibility,
        bool IsStatic,
        bool IsAbstract,
        bool IsVirtual,
        bool IsAsync,
        IReadOnlyList<ParameterDto> Parameters,
        IReadOnlyList<string> TypeParameters,
        IReadOnlyList<string> Attributes
    );

    /// <summary>
    /// Represents the signature and accessors of a property.
    /// </summary>
    public sealed record PropertySignatureDto(
        string Name,
        string Type,
        AccessibilityDto Accessibility,
        bool IsStatic,
        bool HasGetter,
        bool HasSetter,
        IReadOnlyList<string> Attributes
    );

    /// <summary>
    /// Represents the declaration of a field.
    /// </summary>
    public sealed record FieldSignatureDto(
        string Name,
        string Type,
        AccessibilityDto Accessibility,
        bool IsStatic,
        bool IsReadOnly,
        IReadOnlyList<string> Attributes
    );

    /// <summary>
    /// Comprehensive structural information of a declared type (class, interface, struct, etc.).
    /// </summary>
    public sealed record TypeDeclarationDto(
        string Namespace,
        string Name,                 // Simple name
        string FullName,             // Fully qualified name
        TypeKindDto Kind,
        AccessibilityDto Accessibility,
        bool IsStatic,
        bool IsAbstract,
        bool IsSealed,
        IReadOnlyList<string> BaseTypes,       // Base class + interfaces as qualified strings
        IReadOnlyList<string> Attributes,
        IReadOnlyList<MethodSignatureDto> Methods,
        IReadOnlyList<PropertySignatureDto> Properties,
        IReadOnlyList<FieldSignatureDto> Fields,
        SourceSpanDto? DeclaredAt
    );

    /// <summary>
    /// Request parameters to extract the high-level structural outline of a target scope.
    /// </summary>
    public sealed record GetProjectSkeletonRequest(
        CodeScopeDto Scope,
        bool IncludePrivateMembers = false
    );

    /// <summary>
    /// High-level outline containing all declared types and member signatures within a project/scope.
    /// </summary>
    public sealed record ProjectSkeletonDto(
        string ScopeId,
        IReadOnlyList<TypeDeclarationDto> Types
    );

    /// <summary>
    /// Request parameters to inspect a specific type declaration.
    /// </summary>
    public sealed record GetTypeDeclarationRequest(
        CodeScopeDto Scope,
        string TypeFullName,
        bool IncludeMethodBodies = false
    );

    /// <summary>
    /// Request parameters to extract the source implementation of a specific method overload.
    /// </summary>
    public sealed record GetMethodBodyRequest(
        CodeScopeDto Scope,
        string ContainingTypeFullName,
        string MethodName,
        int OverloadIndex = 0
    );

    /// <summary>
    /// Source code implementation details of a method.
    /// </summary>
    public sealed record MethodBodyDto(
        string ContainingTypeFullName,
        string MethodName,
        int OverloadIndex,
        string Language, // "C#"
        string BodyText,
        SourceSpanDto? Span
    );

    /// <summary>
    /// Syntax-level AST inspection tools for extracting skeletons, declarations, and method implementations.
    /// </summary>
    public interface ISyntaxTools
    {
        /// <summary>
        /// Retrieves the high-level structural skeleton of all types and members in the scope.
        /// </summary>
        Task<ProjectSkeletonDto> GetProjectSkeletonAsync(GetProjectSkeletonRequest request, CancellationToken ct = default);

        /// <summary>
        /// Retrieves detailed declaration metadata for a specific type.
        /// </summary>
        Task<TypeDeclarationDto?> GetTypeDeclarationAsync(GetTypeDeclarationRequest request, CancellationToken ct = default);

        /// <summary>
        /// Retrieves the raw source body of a specified method.
        /// </summary>
        Task<MethodBodyDto?> GetMethodBodyAsync(GetMethodBodyRequest request, CancellationToken ct = default);
    }
}
