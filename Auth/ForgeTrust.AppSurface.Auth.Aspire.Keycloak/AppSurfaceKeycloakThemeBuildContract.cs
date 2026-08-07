using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Creates and verifies a deterministic, immutable-image-ready Keycloak login-theme build context.
/// </summary>
/// <remarks>
/// This contract materializes a validated source snapshot, a Containerfile, and secret-safe manifest metadata. The
/// application or its CI system owns the actual image build, registry push, deployment, and production realm update.
/// </remarks>
public sealed class AppSurfaceKeycloakThemeBuildContract
{
    private const string ContainerfileName = "Containerfile";
    private const string ManifestName = "appsurface-keycloak-theme-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _sourceDirectory;

    private AppSurfaceKeycloakThemeBuildContract(AppSurfaceKeycloakThemeRegistrationState registration)
    {
        _sourceDirectory = registration.SourceDirectory;
        Registration = registration.Registration;
        Manifest = registration.Manifest;
        PackagedManifest = AppSurfaceKeycloakThemeManifest.CreatePackagedManifest(
            registration.Manifest,
            registration.DevelopmentOnlyResourcePaths);
    }

    /// <summary>
    /// Gets secret-safe evidence for the registered login theme.
    /// </summary>
    public AppSurfaceKeycloakThemeRegistration Registration { get; }

    /// <summary>
    /// Gets the deterministic source manifest used by this build contract.
    /// </summary>
    public AppSurfaceKeycloakThemeManifest Manifest { get; }

    /// <summary>
    /// Gets the deterministic manifest for the immutable image context after development-only resources are excluded.
    /// </summary>
    /// <remarks>
    /// <see cref="Manifest"/> retains the complete validated local source manifest. Use this property to prove the
    /// packaged image content that <see cref="Write(string)"/> materializes and <see cref="VerifyPackagedTheme(string)"/>
    /// validates.
    /// </remarks>
    public AppSurfaceKeycloakThemeManifest PackagedManifest { get; }

    /// <summary>
    /// Creates a deterministic build contract from a configured login theme.
    /// </summary>
    /// <param name="theme">The application-owned login theme configuration.</param>
    /// <returns>A build contract that owns only the materialized local snapshot.</returns>
    public static AppSurfaceKeycloakThemeBuildContract Create(AppSurfaceKeycloakThemeOptions theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new AppSurfaceKeycloakThemeBuildContract(theme.CreateRegistration(AppContext.BaseDirectory));
    }

    /// <summary>
    /// Writes an immutable-image-ready snapshot to a new directory.
    /// </summary>
    /// <param name="buildContextDirectory">A currently absent output directory owned by the caller.</param>
    /// <returns>The absolute build context directory.</returns>
    /// <exception cref="AppSurfaceKeycloakException">The output would overwrite an existing context or fails verification.</exception>
    public string Write(string buildContextDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildContextDirectory))
        {
            throw BuildContractInvalid("build context directory cannot be blank.");
        }

        var destination = Path.GetFullPath(buildContextDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw BuildContractInvalid("build context directory must not already exist; choose a fresh output location.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw BuildContractInvalid("build context directory must have a parent directory.");
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Join(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var themeDestination = Path.Join(temporary, "themes", Registration.Name);
            Directory.CreateDirectory(themeDestination);
            foreach (var file in PackagedManifest.Files)
            {
                var target = Path.Join(themeDestination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyVerifiedSourceFile(file, target);
            }

            File.WriteAllText(Path.Join(temporary, ContainerfileName), CreateContainerfile());
            File.WriteAllText(
                Path.Join(temporary, ManifestName),
                JsonSerializer.Serialize(new ThemeBuildEvidence(Registration, Manifest, PackagedManifest), JsonOptions));

            VerifyPackagedTheme(themeDestination);
            Directory.Move(temporary, destination);
            return destination;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the primary materialization failure; a later fresh-output run can clean the owned temporary directory.
            }
        }
    }

    /// <summary>
    /// Verifies that a materialized or extracted image theme directory exactly matches this contract's manifest.
    /// </summary>
    /// <param name="packagedThemeDirectory">The directory corresponding to <c>/opt/keycloak/themes/{name}</c>.</param>
    /// <exception cref="AppSurfaceKeycloakException">The packaged content does not match <see cref="PackagedManifest"/>.</exception>
    public void VerifyPackagedTheme(string packagedThemeDirectory)
    {
        if (string.IsNullOrWhiteSpace(packagedThemeDirectory))
        {
            throw BuildContractInvalid("packaged theme directory cannot be blank.");
        }

        AppSurfaceKeycloakThemeManifest packagedManifest;
        try
        {
            packagedManifest = AppSurfaceKeycloakThemeManifest.Create(Registration.Name, Path.GetFullPath(packagedThemeDirectory));
        }
        catch (AppSurfaceKeycloakException exception)
            when (exception.Code is AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid or AppSurfaceKeycloakDiagnosticCodes.ThemeSourceCollision)
        {
            throw BuildContractInvalid("packaged theme content is missing, unsafe, or contains an unexpected file.");
        }

        if (!string.Equals(packagedManifest.Digest, PackagedManifest.Digest, StringComparison.Ordinal))
        {
            throw BuildContractInvalid(
                $"packaged theme manifest digest '{packagedManifest.Digest}' does not match expected digest '{PackagedManifest.Digest}'.");
        }
    }

    /// <summary>
    /// Creates the deterministic Containerfile content for the materialized snapshot.
    /// </summary>
    /// <returns>The image build instructions without machine-local source paths.</returns>
    public string CreateContainerfile() =>
        $"""
        FROM {Registration.BaseImage}
        COPY --chown=keycloak:keycloak themes/{Registration.Name}/ /opt/keycloak/themes/{Registration.Name}/
        LABEL org.appsurface.keycloak.theme.name="{Registration.Name}"
        LABEL org.appsurface.keycloak.theme.manifest-digest="{PackagedManifest.Digest}"
        LABEL org.appsurface.keycloak.theme.platform="{Registration.Platform}"
        {CreateTemplateBaselineLabel()}
        """;

    private string CreateTemplateBaselineLabel() =>
        Registration.TemplateBaselineDigest is null
            ? string.Empty
            : $"LABEL org.appsurface.keycloak.theme.template-baseline-digest=\"{Registration.TemplateBaselineDigest}\"";

    private void CopyVerifiedSourceFile(AppSurfaceKeycloakThemeManifestEntry file, string target)
    {
        try
        {
            File.WriteAllBytes(target, AppSurfaceKeycloakThemeManifest.ReadVerifiedFile(_sourceDirectory, file));
        }
        catch (AppSurfaceKeycloakException exception)
            when (exception.Code == AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid)
        {
            throw BuildContractInvalid($"source file '{file.RelativePath}' changed after this build contract was created.");
        }
    }

    private static AppSurfaceKeycloakException BuildContractInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid,
            $"Problem: AppSurface Keycloak theme build contract is invalid. Cause: {detail} Fix: rebuild from a fresh validated source snapshot and preserve its image/manifest evidence tuple. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#build-contract. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid}.");

    private sealed record ThemeBuildEvidence(
        AppSurfaceKeycloakThemeRegistration Registration,
        AppSurfaceKeycloakThemeManifest Manifest,
        AppSurfaceKeycloakThemeManifest PackagedManifest);
}
