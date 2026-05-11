using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines a configuration provider that also provides environment information.
/// </summary>
/// <remarks>
/// <see cref="IEnvironmentConfigProvider"/> composes <see cref="IConfigProvider"/> with
/// <see cref="IEnvironmentProvider"/> for implementations that need to resolve configuration and the active
/// environment from the same source. Prefer the composite when environment-aware config lookup should be atomic or
/// co-located in one implementation; use the separate interfaces when configuration and environment ownership differ.
/// Implementations should be safe for repeated reads, define any caching or refresh behavior, and avoid assuming that
/// all configuration is environment-exclusive. Disposal and lifetime follow the concrete provider registration.
/// </remarks>
public interface IEnvironmentConfigProvider : IConfigProvider, IEnvironmentProvider
{
}
