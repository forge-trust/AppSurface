namespace ForgeTrust.AppSurface.Config;

/// <summary>The application boundary for environment-first, ordered configuration resolution.</summary>
/// <remarks>Terminal failures suppress fallback but allow one transactional environment-child rescue.</remarks>
public interface IConfigManager
{
    /// <summary>Resolves a strict typed key without applying legacy aliases.</summary>
    /// <typeparam name="T">The requested value type.</typeparam>
    /// <param name="environment">The environment name, independent of key identity.</param>
    /// <param name="key">The immutable logical key.</param>
    /// <returns>The value, or default only when all providers and patches are missing.</returns>
    /// <exception cref="ConfigurationResolutionException">A terminal failure could not be rescued.</exception>
    T? GetValue<T>(string environment, AppSurfaceConfigKey key);

    /// <summary>Parses application input once using the configured compatibility mode, then resolves.</summary>
    /// <typeparam name="T">The requested value type.</typeparam>
    /// <param name="environment">The environment name.</param>
    /// <param name="key">A colon path, or train-1 dot-only migration input.</param>
    /// <returns>The value, or default when absent.</returns>
    /// <exception cref="ArgumentException">The key violates the logical grammar.</exception>
    /// <exception cref="ConfigurationResolutionException">A terminal failure could not be rescued.</exception>
    T? GetValue<T>(string environment, string key);
}
