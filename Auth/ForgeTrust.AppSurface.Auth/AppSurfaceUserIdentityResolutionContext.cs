namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Carries display-safe context for resolving an external subject to an app-owned user id.
/// </summary>
/// <remarks>
/// The context is an input to an app-owned resolver. It may carry safe correlation, issuer, tenant, or provisioning
/// policy hints, but those hints are not authority unless the application validates them against its own security and
/// persistence rules. Metadata is copied with ordinal keys.
/// </remarks>
public sealed class AppSurfaceUserIdentityResolutionContext
{
    /// <summary>
    /// Creates a resolution context.
    /// </summary>
    /// <param name="correlationId">
    /// Optional display-safe correlation id for diagnostics. Null or whitespace values are normalized to
    /// <see langword="null"/>.
    /// </param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceUserIdentityResolutionContext(
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        CorrelationId = AppSurfaceAuthMetadata.NormalizeOptionalText(correlationId);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets an empty resolution context.
    /// </summary>
    public static AppSurfaceUserIdentityResolutionContext Empty { get; } = new();

    /// <summary>
    /// Gets the optional display-safe correlation id.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Gets copied metadata that can help an app resolver preserve display-safe context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
