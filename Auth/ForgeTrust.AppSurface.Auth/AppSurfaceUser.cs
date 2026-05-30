namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Represents the surface-neutral identity information AppSurface modules can share about a user.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceUser"/> is not a claims principal, identity-provider user, or authorization policy result. Host
/// adapters should map their security system into this passive value only after authenticating the subject. Metadata is
/// copied with ordinal keys and should be treated as context, not as authority for authorization decisions.
/// </remarks>
public sealed class AppSurfaceUser
{
    /// <summary>
    /// Creates a user description for AppSurface auth-aware modules.
    /// </summary>
    /// <param name="id">Stable host-owned user identifier. The value must be non-empty and is preserved exactly.</param>
    /// <param name="displayName">Optional display name. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="email">Optional email address. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceUser(
        string id,
        string? displayName = null,
        string? email = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Id = AppSurfaceAuthMetadata.RequireIdentifier(id, nameof(id));
        DisplayName = AppSurfaceAuthMetadata.NormalizeOptionalText(displayName);
        Email = AppSurfaceAuthMetadata.NormalizeOptionalText(email);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the stable host-owned user identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the optional display name for UI or diagnostics.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the optional email address for UI or diagnostics.
    /// </summary>
    public string? Email { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
