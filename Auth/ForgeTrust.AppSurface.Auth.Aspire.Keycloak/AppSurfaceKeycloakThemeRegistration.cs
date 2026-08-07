namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Exposes secret-safe evidence for a validated Keycloak login theme registration.
/// </summary>
/// <param name="Name">The validated Keycloak theme name.</param>
/// <param name="BaseImage">The canonical immutable Keycloak base-image reference.</param>
/// <param name="Platform">The exact-image evidence platform.</param>
/// <param name="ManifestDigest">The deterministic source-manifest digest.</param>
/// <param name="TemplateBaselineDigest">The optional reviewed upstream template-baseline digest.</param>
public sealed record AppSurfaceKeycloakThemeRegistration(
    string Name,
    string BaseImage,
    string Platform,
    string ManifestDigest,
    string? TemplateBaselineDigest = null);

internal sealed record AppSurfaceKeycloakThemeRegistrationState(
    string SourceDirectory,
    AppSurfaceKeycloakThemeManifest Manifest,
    AppSurfaceKeycloakImageReference BaseImage,
    string? TemplateBaselineDigest,
    IReadOnlySet<string> DevelopmentOnlyResourcePaths,
    AppSurfaceKeycloakThemeRegistration Registration);
