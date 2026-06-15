namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines the central manager for configuration, which aggregates multiple <see cref="IConfigProvider"/> instances.
/// </summary>
/// <remarks>
/// <see cref="IConfigProvider.GetValue{T}"/> preserves ordinary provider fallback for missing values, but it also honors
/// fail-closed provider diagnostics. When a provider reports that resolution must stop, the manager throws
/// <see cref="ConfigurationResolutionException"/> instead of querying lower-priority providers.
/// </remarks>
public interface IConfigManager : IConfigProvider
{
}
