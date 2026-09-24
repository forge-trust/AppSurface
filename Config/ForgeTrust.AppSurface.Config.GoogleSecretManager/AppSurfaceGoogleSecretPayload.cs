namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Carries one Google Secret Manager version payload.
/// </summary>
/// <remarks>
/// Payload bytes are copied when received and whenever returned, so callers cannot mutate provider-owned data.
/// The resolved name is the service response's exact version name. It may differ from the requested name when
/// Google resolves a version alias or a project ID to its numeric identifier. The provider requires this name
/// for source provenance and validates it before publishing or caching the payload.
/// </remarks>
public sealed class AppSurfaceGoogleSecretPayload
{
    private readonly byte[] _data;

    /// <summary>Creates a payload with defensive ownership of <paramref name="data"/>.</summary>
    /// <param name="data">The raw payload bytes.</param>
    /// <param name="resolvedResourceName">The exact resolved Secret Manager version resource name. A missing name causes provider resolution to fail.</param>
    public AppSurfaceGoogleSecretPayload(byte[] data, string? resolvedResourceName)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = (byte[])data.Clone();
        ResolvedResourceName = resolvedResourceName;
    }

    /// <summary>Gets a defensive copy of the raw payload bytes.</summary>
    public byte[] Data => (byte[])_data.Clone();

    /// <summary>Gets the exact resolved Secret Manager version resource name, if available.</summary>
    public string? ResolvedResourceName { get; }
}
