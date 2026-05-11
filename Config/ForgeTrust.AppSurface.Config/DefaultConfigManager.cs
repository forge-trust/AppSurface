using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Default implementation of <see cref="IConfigManager"/> that aggregates configuration from multiple sources.
/// </summary>
internal partial class DefaultConfigManager : IConfigManager
{
    private static readonly Type ConfigManagerType = typeof(IConfigManager);
    private static readonly Type EnvironmentConfigProviderType = typeof(IEnvironmentConfigProvider);

    // Lowest priority, we do not expect other config managers to be used over this one
    /// <inheritdoc />
    public int Priority { get; } = -1;

    /// <inheritdoc />
    public string Name { get; } = nameof(DefaultConfigManager);

    private static readonly Type[] ExcludedTypes = [ConfigManagerType, EnvironmentConfigProviderType];

    private readonly IEnvironmentConfigProvider _environmentProvider;
    private readonly IReadOnlyList<IConfigProvider> _otherProviders;
    private readonly ILogger<DefaultConfigManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultConfigManager"/> class.
    /// </summary>
    /// <param name="environmentProvider">The environment configuration provider.</param>
    /// <param name="otherProviders">The collection of other configuration providers.</param>
    /// <param name="logger">The logger for configuration events.</param>
    public DefaultConfigManager(
        IEnvironmentConfigProvider environmentProvider,
        IEnumerable<IConfigProvider>? otherProviders,
        ILogger<DefaultConfigManager> logger)
    {
        _environmentProvider = environmentProvider;
        // we don't want to include ourselves or the environment provider in the list of other providers
        _otherProviders = otherProviders?.Where(x => !ExcludedTypes.Any(t => t.IsInstanceOfType(x)))
                              .OrderByDescending(x => x.Priority)
                              .ToList()
                          ?? [];
        _logger = logger;
    }

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        var envValue = _environmentProvider.GetValue<T>(environment, key);
        if (envValue != null)
        {
            LogRetrievedFromEnvironment(key, environment, "Environment");

            return envValue;
        }

        T? providerValue = default;
        string? providerName = null;
        foreach (var provider in _otherProviders)
        {
            var value = provider.GetValue<T>(environment, key);
            if (value != null)
            {
                providerValue = value;
                providerName = provider.Name;

                break;
            }
        }

        if (_environmentProvider is IConfigValuePatcher patcher
            && patcher.TryPatch(environment, key, providerValue, out var patchedValue))
        {
            LogRetrievedFromEnvironment(key, environment, "Environment");

            return patchedValue;
        }

        if (providerValue != null)
        {
            LogRetrievedFromEnvironment(key, environment, providerName!);

            return providerValue;
        }

        LogKeyNotFound(key, environment);

        return default(T);
    }

    /// <summary>
    /// Logs that a configuration key was not found.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="environment">The environment name.</param>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Configuration key '{Key}' not found in environment '{Environment}'.")]
    public partial void LogKeyNotFound(string key, string environment);

    /// <summary>
    /// Logs that a configuration key was retrieved from a specific source.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="environment">The environment name.</param>
    /// <param name="source">The source of the configuration value.</param>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Configuration key '{Key}' retrieved from environment '{Environment}' using source '{Source}'.")]
    public partial void LogRetrievedFromEnvironment(string key, string environment, string source);
}
