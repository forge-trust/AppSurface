namespace ForgeTrust.AppSurface.Config;

/// <summary>Resolves a parsed logical key through one provider-native source projection.</summary>
/// <remarks>
/// Providers must be safe for concurrent requests. Missing permits fallback; Terminal suppresses lower providers.
/// Do not normalize logical identity or store a follow-up diagnostic cache. See the
/// <see href="https://appsurface.dev/guides/config-provider-authors">provider-author contract</see>.
/// </remarks>
public interface IConfigProvider
{
    /// <summary>Gets priority; larger values are queried first after the environment provider.</summary>
    int Priority { get; }
    /// <summary>Gets the value-safe provider name used in provenance.</summary>
    string Name { get; }
    /// <summary>Returns exactly one missing, found, or terminal outcome for the request.</summary>
    /// <typeparam name="T">The requested type.</typeparam>
    /// <param name="request">The immutable operation request supplied by the manager.</param>
    /// <returns>A non-null structured result; expected provider failures are terminal diagnostics.</returns>
    ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request);
}
