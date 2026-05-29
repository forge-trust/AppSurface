namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Options root for the surface-neutral AppSurface auth composition boundary.
/// </summary>
/// <remarks>
/// This boundary-preview type is intentionally empty. It exists so future AppSurface auth contracts can add settings
/// through a stable options root after those contracts are proven. It does not configure authentication schemes,
/// authorization policies, user or session access, tenant behavior, identity providers, cookies, bearer tokens,
/// challenges, forbids, middleware, endpoint filters, or UI. Host applications must keep those choices in their
/// host-specific security configuration until a later AppSurface package explicitly owns them.
/// </remarks>
public sealed class AppSurfaceAuthOptions;
