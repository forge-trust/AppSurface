namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides optional, privacy-safe server-known evidence about the configured AppSurface Web Push rail.
/// </summary>
/// <remarks>
/// Implementations must return only the fixed <see cref="PwaPushReadiness"/> evidence. They must not expose key
/// material, routes, callback values, subscription values, or provider exception details.
/// </remarks>
public interface IPwaPushReadinessProvider
{
    /// <summary>Gets the current privacy-safe Web Push readiness evidence, or <see langword="null"/> when unavailable.</summary>
    /// <returns>The fixed safe evidence record, or <see langword="null"/>.</returns>
    PwaPushReadiness? GetReadiness();
}

/// <summary>Contains the fixed privacy-safe facts that Web may publish in PWA diagnostics.</summary>
/// <param name="ActiveVapidKeyId">The safe identifier of the active VAPID key.</param>
/// <param name="PublicKeyFingerprint">The SHA-256 fingerprint of the decoded canonical public key.</param>
/// <param name="RouteMapped">Whether the package-owned Web Push route has been mapped.</param>
public sealed record PwaPushReadiness(
    string ActiveVapidKeyId,
    string PublicKeyFingerprint,
    bool RouteMapped);
