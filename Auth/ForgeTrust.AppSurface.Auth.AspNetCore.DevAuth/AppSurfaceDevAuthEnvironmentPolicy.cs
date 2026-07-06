using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Centralizes DevAuth environment activation checks.
/// </summary>
internal static class AppSurfaceDevAuthEnvironmentPolicy
{
    /// <summary>
    /// Determines whether DevAuth may activate in the current host environment.
    /// </summary>
    /// <param name="environment">Host environment to check.</param>
    /// <param name="options">Materialized DevAuth options containing allowed environment names.</param>
    /// <returns><see langword="true"/> when the environment name is in the DevAuth activation allow-list.</returns>
    internal static bool IsEnvironmentAllowed(IHostEnvironment environment, AppSurfaceDevAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var activeEnvironment = NormalizeEnvironmentName(environment.EnvironmentName);
        return options.AllowedEnvironmentNames
            .Select(NormalizeEnvironmentName)
            .Any(allowed => string.Equals(allowed, activeEnvironment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validates the configured DevAuth activation allow-list.
    /// </summary>
    /// <param name="options">Materialized DevAuth options to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the allow-list is empty or contains blank names.</exception>
    internal static void ValidateAllowedEnvironmentNames(AppSurfaceDevAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AllowedEnvironmentNames.Count == 0)
        {
            throw new ArgumentException(
                "AllowedEnvironmentNames must contain at least one host environment name.",
                nameof(AppSurfaceDevAuthOptions.AllowedEnvironmentNames));
        }

        if (options.AllowedEnvironmentNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "AllowedEnvironmentNames cannot contain blank host environment names.",
                nameof(AppSurfaceDevAuthOptions.AllowedEnvironmentNames));
        }
    }

    /// <summary>
    /// Formats configured allowed environment names for safe diagnostics.
    /// </summary>
    /// <param name="options">Materialized DevAuth options to inspect.</param>
    /// <returns>A comma-separated, display-safe list of configured environment names.</returns>
    internal static string FormatAllowedEnvironmentNames(AppSurfaceDevAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var names = options.AllowedEnvironmentNames
            .Select(NormalizeEnvironmentName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    private static string NormalizeEnvironmentName(string? environmentName)
    {
        return environmentName?.Trim() ?? string.Empty;
    }
}
