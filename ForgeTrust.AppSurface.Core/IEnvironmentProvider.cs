namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides information about the application's environment and configuration variables.
/// </summary>
public interface IEnvironmentProvider
{
    /// <summary>
    /// Gets the current environment name (e.g., "Development", "Staging", "Production").
    /// </summary>
    string Environment { get; }

    /// <summary>
    /// Gets a value indicating whether the current environment is "Development".
    /// </summary>
    bool IsDevelopment { get; }

    /// <summary>
    /// Gets the value of an environment variable.
    /// </summary>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="defaultValue">The value to return if the environment variable is not set.</param>
    /// <returns>The value of the environment variable, or the default value if not found.</returns>
    string? GetEnvironmentVariable(string name, string? defaultValue = null);
}
