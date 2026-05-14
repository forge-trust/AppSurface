namespace ForgeTrust.AppSurface.Core.Defaults;

using Microsoft.Extensions.Hosting;

/// <summary>
///    Default implementation of <see cref="IEnvironmentProvider"/> that retrieves the environment from
/// system environment variables. It checks for "ASPNETCORE_ENVIRONMENT" first, then "DOTNET_ENVIRONMENT",
/// and defaults to "Production" if neither is set.
///
/// Can be overridden by passing a custom implementation to the <see cref="StartupContext"/>,
/// when building the host.
/// </summary>
public class DefaultEnvironmentProvider : IEnvironmentProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultEnvironmentProvider"/> class.
    /// </summary>
    public DefaultEnvironmentProvider()
    {
        // Prefer ASPNETCORE_ENVIRONMENT for Generic Host, fallback to DOTNET_ENVIRONMENT, default to Production
        var env = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environments.Production;

        Environment = env;
        IsDevelopment = string.Equals(env, Environments.Development, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The current environment name.
    ///
    /// Read from "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT" environment variables,
    /// defaults to "Production" if neither is set.
    /// </summary>
    public string Environment { get; }

    /// <summary>
    /// True if the current environment is "Development".
    /// </summary>
    public bool IsDevelopment { get; }

    /// <summary>
    /// Gets the value of an environment variable from the system.
    /// </summary>
    /// <remarks>
    /// Use <see cref="GetEnvironmentVariable"/> when callers need the provider's centralized lookup and unset-value
    /// defaulting behavior. Use <see cref="Environment"/> or <see cref="IsDevelopment"/> for the active application
    /// environment, and prefer direct <see cref="System.Environment"/> access only for low-level infrastructure code.
    /// Passing a null or empty <paramref name="name"/> follows <see cref="System.Environment.GetEnvironmentVariable(string)"/>
    /// semantics. Do not log returned values unless the variable is known to be non-sensitive, and do not trim or
    /// alter casing unless the caller intentionally wants to change the platform-specific value.
    /// </remarks>
    /// <param name="name">The exact environment variable name to query.</param>
    /// <param name="defaultValue">The default value to return if the environment variable is unset.</param>
    /// <returns>The environment variable value, an empty string when explicitly set empty, or the provided default if the variable is unset.</returns>
    public string? GetEnvironmentVariable(string name, string? defaultValue = null)
    {
        var value = System.Environment.GetEnvironmentVariable(name);

        return value ?? defaultValue;
    }
}
