namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides the base directory used to resolve AppSurface configuration files.
/// </summary>
/// <remarks>
/// Implementations should return a stable absolute path that exists and is readable by the process. The default
/// implementation returns <see cref="AppContext.BaseDirectory"/>. Callers should validate <see cref="Directory"/> before
/// opening files and report a configuration error when it is null, empty, missing, unreadable, or unsuitable for the
/// current platform. Paths should not depend on the current working directory; symlinks and platform separators are
/// implementation details callers should normalize before comparison.
/// </remarks>
public interface IConfigFileLocationProvider
{
    /// <summary>
    /// Gets the absolute directory path containing configuration files.
    /// </summary>
    /// <remarks>
    /// The value should be an absolute path without a required trailing slash. The provider does not own the directory
    /// lifetime and should not create, delete, or lock it. A typical consumer checks
    /// <c>System.IO.Directory.Exists(provider.Directory)</c> before resolving known config file names below that path
    /// and returns a clear validation failure when the directory is unavailable.
    /// </remarks>
    string Directory { get; }
}
