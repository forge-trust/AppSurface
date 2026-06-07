using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes registry validation and sanitization for one product event.
/// </summary>
public sealed class AppSurfaceProductEventValidationResult
{
    /// <summary>
    /// Creates a registry validation result.
    /// </summary>
    /// <remarks>
    /// This internal constructor accepts a nullable <paramref name="contract"/> because unregistered events have no
    /// matched contract. Collection parameters must be non-null even when empty; they are defensively copied into
    /// read-only wrappers so later caller mutations cannot alter a published result. Empty rejected-property and
    /// diagnostic lists mean no rejection details were recorded. <paramref name="isValid"/> describes whether the event
    /// may be emitted after sanitization and can be <see langword="true"/> even when optional properties were rejected.
    /// </remarks>
    /// <param name="contract">Matched event contract, or <see langword="null"/> when the event is not registered.</param>
    /// <param name="isValid">Whether the event may be emitted after validation and sanitization.</param>
    /// <param name="sanitizedProperties">Sanitized properties safe to emit; pass an empty dictionary when none exist.</param>
    /// <param name="rejectedProperties">Property names rejected during validation; pass an empty list when none exist.</param>
    /// <param name="diagnostics">Safe diagnostics that do not echo rejected values; pass an empty list when none exist.</param>
    internal AppSurfaceProductEventValidationResult(
        AppSurfaceProductEventContract? contract,
        bool isValid,
        IReadOnlyDictionary<string, string> sanitizedProperties,
        IReadOnlyList<string> rejectedProperties,
        IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sanitizedProperties);
        ArgumentNullException.ThrowIfNull(rejectedProperties);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Contract = contract;
        IsValid = isValid;
        SanitizedProperties = new ReadOnlyDictionary<string, string>(
            sanitizedProperties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        RejectedProperties = Array.AsReadOnly(rejectedProperties.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
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
