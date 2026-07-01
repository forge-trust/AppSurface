namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Carries one Google Secret Manager version payload.
/// </summary>
/// <param name="Data">The raw payload bytes.</param>
/// <param name="ResolvedResourceName">The resolved Secret Manager version resource name, if available.</param>
/// <remarks>
/// Record equality compares <paramref name="Data"/> by array reference, not by content, because <see cref="byte"/>[]
/// does not override equality. Compare payload contents explicitly when byte identity matters.
/// </remarks>
public sealed record AppSurfaceGoogleSecretPayload(
    byte[] Data,
    string? ResolvedResourceName);
