namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes registry validation and sanitization for one product event.
/// </summary>
public sealed class AppSurfaceProductEventValidationResult
{
    internal AppSurfaceProductEventValidationResult(
        AppSurfaceProductEventContract? contract,
        bool isValid,
        IReadOnlyDictionary<string, string> sanitizedProperties,
        IReadOnlyList<string> rejectedProperties,
        IReadOnlyList<string> diagnostics)
    {
        Contract = contract;
        IsValid = isValid;
        SanitizedProperties = sanitizedProperties;
        RejectedProperties = rejectedProperties;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the matched event contract, or <see langword="null"/> when the event is not registered.
    /// </summary>
    public AppSurfaceProductEventContract? Contract { get; }

    /// <summary>
    /// Gets a value indicating whether the event may be emitted after sanitization.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Gets properties that may be emitted to a sink.
    /// </summary>
    public IReadOnlyDictionary<string, string> SanitizedProperties { get; }

    /// <summary>
    /// Gets property names rejected during validation.
    /// </summary>
    public IReadOnlyList<string> RejectedProperties { get; }

    /// <summary>
    /// Gets safe diagnostics that describe rejected schema decisions without echoing rejected values.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
