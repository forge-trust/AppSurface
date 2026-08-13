namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes the environment and explicit mode for one configuration audit report.
/// </summary>
/// <remarks>
/// The default mode preserves the canonical report returned by <see cref="IConfigAuditReporter.GetReport(string)"/>.
/// <see cref="ConfigAuditReportMode.ExpandKnownEntryCollections"/> is an opt-in diagnostic mode: built-in reporting
/// expands only already-known entry collections and retains the normal redaction and traversal limits. Custom
/// reporters can continue to support only the default mode until they explicitly implement the request overload.
/// </remarks>
public sealed class ConfigAuditReportRequest
{
    /// <summary>
    /// Initializes a report request.
    /// </summary>
    /// <param name="environment">The non-blank environment to audit.</param>
    /// <param name="mode">The explicit report mode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="environment"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is undefined.</exception>
    public ConfigAuditReportRequest(
        string environment,
        ConfigAuditReportMode mode = ConfigAuditReportMode.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The configuration audit report mode is not supported.");
        }

        Environment = environment;
        Mode = mode;
    }

    /// <summary>
    /// Gets the environment to audit.
    /// </summary>
    public string Environment { get; }

    /// <summary>
    /// Gets the explicit report mode.
    /// </summary>
    public ConfigAuditReportMode Mode { get; }
}

/// <summary>
/// Selects the shape of a configuration audit report.
/// </summary>
public enum ConfigAuditReportMode
{
    /// <summary>
    /// Produces the existing canonical report without global collection expansion.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Expands supported collection children beneath already-known audit entries while preserving redaction and bounds.
    /// </summary>
    ExpandKnownEntryCollections = 1
}
