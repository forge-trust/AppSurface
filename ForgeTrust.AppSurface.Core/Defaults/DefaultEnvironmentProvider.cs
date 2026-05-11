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
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="defaultValue">The default value to return if the environment variable is unset.</param>
    /// <returns>The environment variable value, an empty string when explicitly set empty, or the provided default if the variable is unset.</returns>
    public string? GetEnvironmentVariable(string name, string? defaultValue = null)
    {
        var value = System.Environment.GetEnvironmentVariable(name);

        return value ?? defaultValue;
    }
}
