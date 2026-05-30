namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Represents surface-neutral session information associated with an AppSurface auth context.
/// </summary>
/// <remarks>
/// Session timestamps use <see cref="DateTimeOffset"/> so host adapters can preserve their original offset. AppSurface
/// does not convert, refresh, revoke, store, or validate the backing host session.
/// </remarks>
public sealed class AppSurfaceSession
{
    /// <summary>
    /// Creates a session description for AppSurface auth-aware modules.
    /// </summary>
    /// <param name="id">Stable host-owned session identifier. The value must be non-empty and is preserved exactly.</param>
    /// <param name="startedAt">Optional timestamp when the host session began.</param>
    /// <param name="expiresAt">Optional timestamp when the host session expires.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceSession(
        string id,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? expiresAt = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (startedAt is not null && expiresAt is not null && expiresAt < startedAt)
        {
            throw new ArgumentException("Session expiration cannot be earlier than session start.", nameof(expiresAt));
        }

        Id = AppSurfaceAuthMetadata.RequireIdentifier(id, nameof(id));
        StartedAt = startedAt;
        ExpiresAt = expiresAt;
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the stable host-owned session identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the optional timestamp when the host session began.
    /// </summary>
    public DateTimeOffset? StartedAt { get; }

    /// <summary>
    /// Gets the optional timestamp when the host session expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
