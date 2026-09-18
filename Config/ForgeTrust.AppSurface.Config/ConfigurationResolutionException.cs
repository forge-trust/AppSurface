namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Represents a display-safe terminal configuration resolution failure.
/// </summary>
/// <remarks>
/// The exception message is built from <see cref="Diagnostic"/> and intentionally omits raw provider exception
/// messages and configuration values. Catch this exception at command or host boundaries when the app should render
/// provider posture guidance instead of falling through to lower-priority configuration sources.
/// </remarks>
public sealed class ConfigurationResolutionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationResolutionException"/> class.
    /// </summary>
    /// <param name="environment">The environment being resolved.</param>
    /// <param name="key">The configuration key being resolved.</param>
    /// <param name="providerName">The provider that stopped resolution.</param>
    /// <param name="diagnostic">The display-safe terminal diagnostic.</param>
    public ConfigurationResolutionException(
        string environment,
        AppSurfaceConfigKey key,
        string providerName,
        ConfigProviderTerminalDiagnostic diagnostic)
        : base(CreateMessage(
            providerName ?? throw new ArgumentNullException(nameof(providerName)),
            diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))))
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        EnvironmentName = environment;
        LogicalKey = key;
        ProviderName = providerName;
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the environment being resolved.
    /// </summary>
    public string EnvironmentName { get; }

    /// <summary>
    /// Gets the configuration key being resolved.
    /// </summary>
    public string Key => LogicalKey.Value;

    /// <summary>Gets the parsed identity that failed resolution.</summary>
    public AppSurfaceConfigKey LogicalKey { get; }

    /// <summary>
    /// Gets the provider that stopped lower-priority resolution.
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    /// Gets the display-safe terminal diagnostic.
    /// </summary>
    public ConfigProviderTerminalDiagnostic Diagnostic { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}Environment: {ConfigDiagnosticText.Identifier(EnvironmentName)}{Environment.NewLine}Key: {ConfigDiagnosticText.Identifier(Key)}";

    private static string CreateMessage(string providerName, ConfigProviderTerminalDiagnostic diagnostic) =>
        $"Configuration provider {ConfigDiagnosticText.Identifier(providerName)} stopped resolution. {diagnostic.ToDisplayString()}";
}
