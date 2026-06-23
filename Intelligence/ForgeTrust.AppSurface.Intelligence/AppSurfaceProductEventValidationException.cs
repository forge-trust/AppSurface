namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Exception thrown by development diagnostics when a product event would be dropped.
/// </summary>
/// <remarks>
/// The exception is safe to surface in development and tests. It includes event names only after constructor
/// normalization, property names only from registry diagnostics, stable reason codes, a documentation path, and a fix
/// hint. It never includes raw property values or serialized event payloads.
/// </remarks>
public sealed class AppSurfaceProductEventValidationException : InvalidOperationException
{
    /// <summary>
    /// Documentation path for product-intelligence contract registration and validation diagnostics.
    /// </summary>
    public const string DocumentationPath = "Intelligence/ForgeTrust.AppSurface.Intelligence/README.md#custom-event-contracts-in-5-minutes";

    /// <summary>
    /// Creates a safe validation exception from a validation result.
    /// </summary>
    /// <param name="eventName">Registered-format event name supplied by the caller.</param>
    /// <param name="validation">Validation result for the dropped event.</param>
    /// <param name="fixHint">Safe remediation hint for the caller.</param>
    public AppSurfaceProductEventValidationException(
        string eventName,
        AppSurfaceProductEventValidationResult validation,
        string fixHint)
        : base(CreateMessage(eventName, validation, fixHint))
    {
        ArgumentNullException.ThrowIfNull(validation);
        EventName = AppSurfaceProductEventMetadata.SanitizeDiagnosticEventName(eventName);
        ReasonCodes = validation.ReasonCodes;
        RejectedProperties = validation.RejectedProperties;
        DocsLink = DocumentationPath;
        FixHint = AppSurfaceProductEventMetadata.RequireText(fixHint, nameof(fixHint));
    }

    /// <summary>
    /// Gets the safe event name that failed validation.
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets stable safe reason codes explaining why the event was dropped.
    /// </summary>
    public IReadOnlyList<AppSurfaceProductEventValidationFailureReason> ReasonCodes { get; }

    /// <summary>
    /// Gets safe property names rejected during validation.
    /// </summary>
    public IReadOnlyList<string> RejectedProperties { get; }

    /// <summary>
    /// Gets a repository documentation path with registration and validation guidance.
    /// </summary>
    public string DocsLink { get; }

    /// <summary>
    /// Gets a safe remediation hint that does not echo event payload values.
    /// </summary>
    public string FixHint { get; }

    private static string CreateMessage(
        string eventName,
        AppSurfaceProductEventValidationResult validation,
        string fixHint)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var safeEventName = AppSurfaceProductEventMetadata.SanitizeDiagnosticEventName(eventName);
        var safeFixHint = AppSurfaceProductEventMetadata.RequireText(fixHint, nameof(fixHint));
        var reasons = validation.ReasonCodes.Count == 0
            ? "Unknown"
            : string.Join(", ", validation.ReasonCodes);
        var rejected = validation.RejectedProperties.Count == 0
            ? "none"
            : string.Join(", ", validation.RejectedProperties);

        return $"Product event '{safeEventName}' was dropped. Reasons: {reasons}. Rejected properties: {rejected}. Fix: {safeFixHint}. See {DocumentationPath}.";
    }
}
