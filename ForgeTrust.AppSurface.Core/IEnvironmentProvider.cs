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
    /// <remarks>
    /// Implementations should be side-effect-free and preserve the platform's environment-name behavior: Windows is
    /// generally case-insensitive, while Unix-like systems are case-sensitive. Return <paramref name="defaultValue"/>
    /// only when the variable is unset and the underlying lookup returns <see langword="null"/>; an explicitly empty
    /// variable is a real empty string and should not be replaced by the default.
    /// </remarks>
    /// <param name="name">The exact environment variable name to query.</param>
    /// <param name="defaultValue">The value to return when the variable is unset.</param>
    /// <returns>The variable value, an empty string when explicitly set empty, or <paramref name="defaultValue"/> when unset.</returns>
    string? GetEnvironmentVariable(string name, string? defaultValue = null);
}
