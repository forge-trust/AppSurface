using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Represents the secret-safe tuple that binds a packaged Keycloak login theme to the images and manifests it was
/// verified against.
/// </summary>
public sealed class AppSurfaceKeycloakThemeReleaseEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly AsyncLocal<Action<string>?> BeforeMoveForTestingSlot = new();

    internal static Action<string>? BeforeMoveForTesting
    {
        get => BeforeMoveForTestingSlot.Value;
        set => BeforeMoveForTestingSlot.Value = value;
    }

    private AppSurfaceKeycloakThemeReleaseEvidence(
        string themeName,
        string sourceManifestDigest,
        string packagedManifestDigest,
        string buildContractDigest,
        string keycloakBaseImage,
        string finalImage,
        string platform,
        string? templateBaselineDigest)
    {
        ThemeName = themeName;
        SourceManifestDigest = sourceManifestDigest;
        PackagedManifestDigest = packagedManifestDigest;
        BuildContractDigest = buildContractDigest;
        KeycloakBaseImage = keycloakBaseImage;
        FinalImage = finalImage;
        Platform = platform;
        TemplateBaselineDigest = templateBaselineDigest;
    }

    /// <summary>
    /// Gets the versioned evidence schema name.
    /// </summary>
    public string Schema => "appsurface-keycloak-theme-release-evidence-v1";

    /// <summary>
    /// Gets the registered Keycloak login theme name.
    /// </summary>
    public string ThemeName { get; }

    /// <summary>
    /// Gets the complete validated source-manifest digest.
    /// </summary>
    public string SourceManifestDigest { get; }

    /// <summary>
    /// Gets the immutable image-context manifest digest.
    /// </summary>
    public string PackagedManifestDigest { get; }

    /// <summary>
    /// Gets the digest of the generated build contract.
    /// </summary>
    public string BuildContractDigest { get; }

    /// <summary>
    /// Gets the digest-pinned Keycloak base-image reference.
    /// </summary>
    public string KeycloakBaseImage { get; }

    /// <summary>
    /// Gets the digest-pinned image that contains the packaged theme.
    /// </summary>
    public string FinalImage { get; }

    /// <summary>
    /// Gets the verified container platform.
    /// </summary>
    public string Platform { get; }

    /// <summary>
    /// Gets the optional reviewed FreeMarker baseline digest.
    /// </summary>
    public string? TemplateBaselineDigest { get; }

    /// <summary>
    /// Creates release evidence from a verified build contract and the digest-pinned image that packages its theme.
    /// </summary>
    /// <param name="buildContract">The validated build contract that produced the image context.</param>
    /// <param name="finalImage">The immutable image reference that contains the packaged theme.</param>
    /// <returns>A portable evidence tuple without source paths, realm imports, credentials, or property values.</returns>
    public static AppSurfaceKeycloakThemeReleaseEvidence Create(
        AppSurfaceKeycloakThemeBuildContract buildContract,
        string finalImage)
    {
        ArgumentNullException.ThrowIfNull(buildContract);
        var image = AppSurfaceKeycloakImageReference.Parse(finalImage);
        return new AppSurfaceKeycloakThemeReleaseEvidence(
            buildContract.Registration.Name,
            buildContract.Manifest.Digest,
            buildContract.PackagedManifest.Digest,
            buildContract.Digest,
            buildContract.Registration.BaseImage,
            image.Value,
            buildContract.Registration.Platform,
            buildContract.Registration.TemplateBaselineDigest);
    }

    /// <summary>
    /// Verifies that this evidence still represents the supplied build contract and final image reference.
    /// </summary>
    /// <param name="buildContract">The current validated build contract.</param>
    /// <param name="finalImage">The expected digest-pinned final image reference.</param>
    /// <exception cref="AppSurfaceKeycloakException">The evidence and supplied immutable inputs do not match.</exception>
    public void Verify(AppSurfaceKeycloakThemeBuildContract buildContract, string finalImage)
    {
        var expected = Create(buildContract, finalImage);
        if (!string.Equals(ThemeName, expected.ThemeName, StringComparison.Ordinal)
            || !string.Equals(SourceManifestDigest, expected.SourceManifestDigest, StringComparison.Ordinal)
            || !string.Equals(PackagedManifestDigest, expected.PackagedManifestDigest, StringComparison.Ordinal)
            || !string.Equals(BuildContractDigest, expected.BuildContractDigest, StringComparison.Ordinal)
            || !string.Equals(KeycloakBaseImage, expected.KeycloakBaseImage, StringComparison.Ordinal)
            || !string.Equals(FinalImage, expected.FinalImage, StringComparison.Ordinal)
            || !string.Equals(Platform, expected.Platform, StringComparison.Ordinal)
            || !string.Equals(TemplateBaselineDigest, expected.TemplateBaselineDigest, StringComparison.Ordinal))
        {
            throw PackagedImageInvalid("the supplied image or build contract does not match this release tuple.");
        }
    }

    /// <summary>
    /// Atomically writes this release tuple to an application-owned evidence file.
    /// </summary>
    /// <param name="outputFile">An absent, application-owned JSON evidence file path.</param>
    /// <returns>The absolute evidence file path.</returns>
    /// <exception cref="AppSurfaceKeycloakException">The output path is invalid or cannot be safely materialized.</exception>
    public string Write(string outputFile)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
        {
            throw BuildContractInvalid("release evidence output file cannot be blank.");
        }

        var destination = Path.GetFullPath(outputFile);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw BuildContractInvalid("release evidence output file must not already exist; choose a fresh path.");
        }

        var parent = Path.GetDirectoryName(destination)!;

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw BuildContractInvalid($"release evidence parent directory could not be created safely ({exception.GetType().Name}).");
        }

        var temporary = Path.Join(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            BeforeMoveForTesting?.Invoke(destination);
            File.Move(temporary, destination);
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw BuildContractInvalid($"release evidence output could not be materialized safely ({exception.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the primary output failure; the next fresh evidence run can remove its owned temporary file.
            }
        }
    }

    private static AppSurfaceKeycloakException BuildContractInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid,
            $"Problem: AppSurface Keycloak theme build evidence is invalid. Cause: {detail} Fix: recreate the immutable build context and evidence tuple from a fresh validated source. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#release-evidence. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid}.");

    private static AppSurfaceKeycloakException PackagedImageInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemePackagedImageInvalid,
            $"Problem: AppSurface Keycloak packaged-image evidence is invalid. Cause: {detail} Fix: rebuild the image from the matching build contract and regenerate the release tuple. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#ci-evidence. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemePackagedImageInvalid}.");
}
