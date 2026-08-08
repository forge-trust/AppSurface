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

/// <summary>
/// Holds validated theme-registration state, including the resolved source directory and development-only paths that
/// are excluded from immutable image evidence.
/// </summary>
/// <param name="SourceDirectory">The resolved application-owned theme source directory.</param>
/// <param name="Manifest">The deterministic manifest of the complete validated source tree.</param>
/// <param name="BaseImage">The immutable Keycloak base image used for the local proof.</param>
/// <param name="TemplateBaselineDigest">The optional reviewed upstream template-baseline digest.</param>
/// <param name="DevelopmentOnlyResourcePaths">Source-relative paths excluded from packaged image evidence.</param>
/// <param name="Registration">The secret-safe public registration evidence.</param>
internal sealed record AppSurfaceKeycloakThemeRegistrationState(
    string SourceDirectory,
    AppSurfaceKeycloakThemeManifest Manifest,
    AppSurfaceKeycloakImageReference BaseImage,
    string? TemplateBaselineDigest,
    IReadOnlySet<string> DevelopmentOnlyResourcePaths,
    AppSurfaceKeycloakThemeRegistration Registration);
