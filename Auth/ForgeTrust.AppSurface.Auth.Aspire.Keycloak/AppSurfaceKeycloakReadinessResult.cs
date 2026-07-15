namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Captures safe readiness evidence for the AppSurface Keycloak local proof.
/// </summary>
/// <param name="Authority">The verified Keycloak realm authority.</param>
/// <param name="ClientId">The verified public client id.</param>
/// <param name="Realm">The verified realm id.</param>
public sealed record AppSurfaceKeycloakReadinessResult(string Authority, string ClientId, string Realm);
