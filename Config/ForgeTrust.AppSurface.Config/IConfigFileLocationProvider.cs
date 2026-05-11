namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides the directory path where configuration files are located.
/// </summary>
public interface IConfigFileLocationProvider
{
    /// <summary>
    /// Gets the directory path containing configuration files.
    /// </summary>
    string Directory { get; }
}
