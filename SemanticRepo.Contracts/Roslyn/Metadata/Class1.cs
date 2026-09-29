using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SemanticRepo.Contracts.Roslyn.Common;

namespace SemanticRepo.Contracts.Roslyn.Metadata
{
    /// <summary>
    /// Dependency injection lifetime descriptor.
    /// </summary>
    public enum ServiceLifetimeDto
    {
        Singleton = 0,
        Scoped = 1,
        Transient = 2
    }

    /// <summary>
    /// Discovered Dependency Injection registration (e.g., services.AddSingleton&lt;TInterface, TImpl&gt;()).
    /// </summary>
    public sealed record DIRegistrationDto(
        ServiceLifetimeDto Lifetime,
        string ServiceType,
        string ImplementationType,
        SourceSpanDto? DeclaredAt
    );

    /// <summary>
    /// Request parameters to extract DI registrations.
    /// </summary>
    public sealed record GetDIRegistrationsRequest(
        CodeScopeDto Scope,
        bool IncludeThirdParty = false
    );

    /// <summary>
    /// Collection of extracted DI registrations.
    /// </summary>
    public sealed record DIRegistrationsDto(
        IReadOnlyList<DIRegistrationDto> Registrations
    );

    /// <summary>
    /// Type marked with a specific attribute.
    /// </summary>
    public sealed record AttributedTypeDto(
        string TypeFullName,
        string AttributeFullName,
        SourceSpanDto? DeclaredAt
    );

    /// <summary>
    /// Request parameters to find all types bearing a designated attribute.
    /// </summary>
    public sealed record GetAttributedTypesRequest(
        CodeScopeDto Scope,
        string AttributeFullName,
        bool Inherit = true
    );

    /// <summary>
    /// Compiler metadata inspection tools for DI containers and custom attributes.
    /// </summary>
    public interface ICompilerMetadataTools
    {
        /// <summary>
        /// Scans configuration and startup code to detect Dependency Injection registrations.
        /// </summary>
        Task<DIRegistrationsDto> GetDIRegistrationsAsync(GetDIRegistrationsRequest request, CancellationToken ct = default);

        /// <summary>
        /// Finds all types annotated with a specific attribute.
        /// </summary>
        Task<IReadOnlyList<AttributedTypeDto>> GetAttributedTypesAsync(GetAttributedTypesRequest request, CancellationToken ct = default);
    }
}
