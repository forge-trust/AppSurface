namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Default implementation of <see cref="IConfigFileLocationProvider"/> that uses the application's base directory.
/// </summary>
public class DefaultConfigFileLocationProvider : IConfigFileLocationProvider
{
    /// <inheritdoc />
    public string Directory { get; } = AppContext.BaseDirectory;
}
