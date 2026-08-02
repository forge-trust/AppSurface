using System.Security.Cryptography;
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Builds privacy-safe PWA push readiness evidence from validated package configuration.</summary>
internal sealed class AppSurfaceWebPushReadinessProvider : IPwaPushReadinessProvider
{
    private readonly IOptions<AppSurfaceWebPushOptions> options;
    private readonly AppSurfaceWebPushRouteRegistry routeRegistry;

    public AppSurfaceWebPushReadinessProvider(
        IOptions<AppSurfaceWebPushOptions> options,
        AppSurfaceWebPushRouteRegistry routeRegistry)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.routeRegistry = routeRegistry ?? throw new ArgumentNullException(nameof(routeRegistry));
    }

    public PwaPushReadiness? GetReadiness()
    {
        try
        {
            var configured = options.Value;
            var activeKeyId = configured.ActiveVapidKeyId;
            if (activeKeyId is not { } safeActiveKeyId
                || !AppSurfaceWebPushValidation.IsSafeKeyId(safeActiveKeyId)
                || !configured.VapidKeys.TryGetValue(safeActiveKeyId, out var activeKey)
                || activeKey is null)
            {
                return null;
            }

            var publicKey = activeKey.PublicKey;
            if (!AppSurfaceWebPushValidation.IsValidP256PublicKey(publicKey)
                || !AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(publicKey, 65, out var publicKeyBytes))
            {
                return null;
            }

            var fingerprint = "sha256-" + AppSurfaceWebPushValidation.Base64UrlEncode(SHA256.HashData(publicKeyBytes));
            return new PwaPushReadiness(safeActiveKeyId, fingerprint, routeRegistry.IsMapped);
        }
        catch
        {
            return null;
        }
    }
}
