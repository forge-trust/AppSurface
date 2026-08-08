using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Configures an application-owned Keycloak login theme for a local AppHost proof.
/// </summary>
/// <remarks>
/// This configuration affects only the AppHost resource. It does not build, publish, deploy, or administer a
/// production Keycloak image or realm. The source directory must contain a Keycloak theme root with
/// <c>login/theme.properties</c>.
/// </remarks>
public sealed class AppSurfaceKeycloakThemeOptions
{
    private static readonly Regex ThemeNamePattern = new("^[a-z][a-z0-9-]{2,62}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Creates a login-theme configuration.
    /// </summary>
    /// <param name="name">The lower-case Keycloak login theme name.</param>
    /// <param name="sourceDirectory">The directory containing the <c>login</c> theme subtree.</param>
    /// <param name="baseImage">The immutable Keycloak image whose theme behavior is being verified.</param>
    public AppSurfaceKeycloakThemeOptions(string name, string sourceDirectory, AppSurfaceKeycloakImageReference baseImage)
    {
        Name = name;
        SourceDirectory = sourceDirectory;
        BaseImage = baseImage;
    }

    /// <summary>
    /// Gets or sets the lower-case Keycloak login theme name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the directory containing the theme's <c>login</c> subtree.
    /// </summary>
    /// <remarks>
    /// Relative paths are resolved once against the AppHost process base directory before validation and mounting.
    /// The resolved path is never exposed through the safe resource registration.
    /// </remarks>
    public string SourceDirectory { get; set; }

    /// <summary>
    /// Gets or sets the immutable Keycloak base image used by local theme proof.
    /// </summary>
    public AppSurfaceKeycloakImageReference BaseImage { get; set; }

    /// <summary>
    /// Gets or sets the platform for exact-image evidence.
    /// </summary>
    /// <remarks>
    /// Version one supports <c>linux/amd64</c> exact runtime proof. Other platforms can use deterministic source
    /// validation, but must not claim the exact-image release proof.
    /// </remarks>
    public string Platform { get; set; } = "linux/amd64";

    /// <summary>
    /// Gets or sets the directory holding the reviewed upstream FreeMarker baseline for copied template overrides.
    /// </summary>
    /// <remarks>
    /// A source tree containing <c>.ftl</c> files must declare this directory. It contains only the expected
    /// slash-relative upstream template files and its digest is emitted as evidence beside the pinned image identity.
    /// </remarks>
    public string? TemplateBaselineDirectory { get; set; }

    /// <summary>
    /// Gets property names that must occur in <c>login/theme.properties</c> without retaining their values in evidence.
    /// </summary>
    public IList<string> RequiredThemeProperties { get; } = [];

    /// <summary>
    /// Gets source-relative resources that must exist in the deterministic manifest.
    /// </summary>
    public IList<string> RequiredResourcePaths { get; } = [];

    /// <summary>
    /// Gets source-relative resources used only by the development bind mount.
    /// </summary>
    /// <remarks>
    /// This declaration is validated but does not make an asset eligible for a future packaged-image proof.
    /// </remarks>
    public IList<string> DevelopmentOnlyResourcePaths { get; } = [];

    /// <summary>
    /// Creates an assets-only or inherited-template login-theme configuration.
    /// </summary>
    /// <param name="name">The lower-case Keycloak login theme name.</param>
    /// <param name="sourceDirectory">The directory containing the <c>login</c> theme subtree.</param>
    /// <param name="baseImage">The immutable Keycloak image whose theme behavior is being verified.</param>
    /// <returns>A configurable login-theme instance.</returns>
    public static AppSurfaceKeycloakThemeOptions Login(
        string name,
        string sourceDirectory,
        AppSurfaceKeycloakImageReference baseImage) =>
        new(name, sourceDirectory, baseImage);

    /// <summary>
    /// Validates the theme source and declared resource/property requirements using the AppHost process base directory.
    /// </summary>
    public void Validate() => _ = CreateRegistration(AppContext.BaseDirectory);

    internal AppSurfaceKeycloakThemeRegistrationState CreateRegistration(string sourceBaseDirectory)
    {
        if (!ThemeNamePattern.IsMatch(Name))
        {
            throw Invalid(nameof(Name), "name must match ^[a-z][a-z0-9-]{2,62}$ and is never normalized silently.");
        }

        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            throw Invalid(nameof(SourceDirectory), "source directory cannot be blank.");
        }

        ArgumentNullException.ThrowIfNull(BaseImage);
        if (!string.Equals(Platform, "linux/amd64", StringComparison.Ordinal))
        {
            throw Invalid(nameof(Platform), "only linux/amd64 is eligible for the exact-image proof in version one.");
        }

        var sourceDirectory = ResolveDirectory(SourceDirectory, sourceBaseDirectory, nameof(SourceDirectory));
        var manifest = AppSurfaceKeycloakThemeManifest.Create(Name, sourceDirectory);
        var developmentOnlyResourcePaths = ValidateRequirements(manifest, sourceDirectory);
        var templateBaselineDigest = ValidateTemplateBaseline(manifest, sourceBaseDirectory);
        return new AppSurfaceKeycloakThemeRegistrationState(
            sourceDirectory,
            manifest,
            BaseImage,
            templateBaselineDigest,
            developmentOnlyResourcePaths,
            new AppSurfaceKeycloakThemeRegistration(Name, BaseImage.Value, Platform, manifest.Digest, templateBaselineDigest));
    }

    internal AppSurfaceKeycloakThemeOptions CreateSnapshot()
    {
        var snapshot = new AppSurfaceKeycloakThemeOptions(Name, SourceDirectory, BaseImage)
        {
            Platform = Platform,
            TemplateBaselineDirectory = TemplateBaselineDirectory,
        };

        foreach (var requiredProperty in RequiredThemeProperties)
        {
            snapshot.RequiredThemeProperties.Add(requiredProperty);
        }

        foreach (var requiredResourcePath in RequiredResourcePaths)
        {
            snapshot.RequiredResourcePaths.Add(requiredResourcePath);
        }

        foreach (var developmentOnlyResourcePath in DevelopmentOnlyResourcePaths)
        {
            snapshot.DevelopmentOnlyResourcePaths.Add(developmentOnlyResourcePath);
        }

        return snapshot;
    }

    private string? ValidateTemplateBaseline(AppSurfaceKeycloakThemeManifest manifest, string sourceBaseDirectory)
    {
        var templatePaths = manifest.Files
            .Where(file => string.Equals(Path.GetExtension(file.RelativePath), ".ftl", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.RelativePath)
            .ToArray();
        if (templatePaths.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(TemplateBaselineDirectory))
        {
            throw TemplateBaselineInvalid("copied .ftl files require TemplateBaselineDirectory.");
        }

        var baselineDirectory = ResolveDirectory(TemplateBaselineDirectory, sourceBaseDirectory, nameof(TemplateBaselineDirectory));
        AppSurfaceKeycloakThemeManifest baseline;
        try
        {
            baseline = AppSurfaceKeycloakThemeManifest.CreateTemplateBaseline(Name, baselineDirectory);
        }
        catch (AppSurfaceKeycloakException exception)
            when (exception.Code is AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid or AppSurfaceKeycloakDiagnosticCodes.ThemeSourceCollision)
        {
            throw TemplateBaselineInvalid("the declared upstream template baseline is missing or unsafe.");
        }

        var baselinePaths = baseline.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var unexpectedTemplate = templatePaths.FirstOrDefault(path => !baselinePaths.Contains(path));
        if (unexpectedTemplate is not null)
        {
            throw TemplateBaselineInvalid($"copied template '{unexpectedTemplate}' has no reviewed upstream baseline entry.");
        }

        return baseline.Digest;
    }

    private IReadOnlySet<string> ValidateRequirements(AppSurfaceKeycloakThemeManifest manifest, string sourceDirectory)
    {
        var paths = manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var requiredResourcePath in RequiredResourcePaths)
        {
            var normalizedPath = NormalizeDeclaredPath(requiredResourcePath, nameof(RequiredResourcePaths));
            if (!paths.Contains(normalizedPath))
            {
                throw RequirementsInvalid($"required resource '{normalizedPath}' is missing from the source manifest.");
            }
        }

        var developmentOnlyPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var developmentOnlyResourcePath in DevelopmentOnlyResourcePaths)
        {
            var normalizedPath = NormalizeDeclaredPath(developmentOnlyResourcePath, nameof(DevelopmentOnlyResourcePaths));
            if (!paths.Contains(normalizedPath))
            {
                throw RequirementsInvalid($"development-only resource '{normalizedPath}' is missing from the source manifest.");
            }

            if (string.Equals(normalizedPath, "login/theme.properties", StringComparison.Ordinal))
            {
                throw RequirementsInvalid("login/theme.properties cannot be development-only because packaged themes require it.");
            }

            developmentOnlyPaths.Add(normalizedPath);
        }

        if (RequiredThemeProperties.Count == 0)
        {
            return developmentOnlyPaths;
        }

        var propertiesEntry = manifest.Files.Single(file => string.Equals(file.RelativePath, "login/theme.properties", StringComparison.Ordinal));
        var properties = System.Text.Encoding.UTF8.GetString(AppSurfaceKeycloakThemeManifest.ReadVerifiedFile(sourceDirectory, propertiesEntry));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var continued = false;
        foreach (var line in properties.Split('\n'))
        {
            var physicalLine = line.TrimEnd('\r');
            var continues = HasTrailingContinuation(physicalLine);
            if (continued)
            {
                continued = continues;
                continue;
            }

            continued = continues;
            var trimmed = physicalLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            var key = GetPropertyKey(trimmed);
            if (!string.IsNullOrWhiteSpace(key))
            {
                names.Add(key.Trim());
            }
        }

        foreach (var requiredProperty in RequiredThemeProperties)
        {
            if (string.IsNullOrWhiteSpace(requiredProperty) || !names.Contains(requiredProperty))
            {
                throw RequirementsInvalid($"required theme property '{requiredProperty}' is missing from login/theme.properties.");
            }
        }

        return developmentOnlyPaths;
    }

    private static string ResolveDirectory(string value, string baseDirectory, string optionName)
    {
        try
        {
            return Path.GetFullPath(value, baseDirectory);
        }
        catch (ArgumentException)
        {
            throw Invalid(optionName, "the path contains unsupported characters.");
        }
    }

    private static string NormalizeDeclaredPath(string value, string optionName)
    {
        if (value is null)
        {
            throw Invalid(optionName, "paths cannot contain null entries.");
        }

        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw Invalid(optionName, "paths must be non-empty, slash-separated theme-relative paths without traversal.");
        }

        return normalized;
    }

    private static string GetPropertyKey(string line)
    {
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character is '=' or ':' || char.IsWhiteSpace(character))
            {
                return line[..index];
            }
        }

        return line;
    }

    private static bool HasTrailingContinuation(string line)
    {
        var backslashes = 0;
        for (var index = line.Length - 1; index >= 0 && line[index] == '\\'; index--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static AppSurfaceKeycloakException Invalid(string optionName, string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration,
            $"Problem: AppSurface Keycloak login theme option {optionName} is invalid. Cause: {detail} Fix: use an explicit lower-case name and immutable local-proof configuration. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#theme-quickstart. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration}.");

    private static AppSurfaceKeycloakException RequirementsInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemePropertiesInvalid,
            $"Problem: AppSurface Keycloak login theme requirements are invalid. Cause: {detail} Fix: update declared property or resource names without adding their values to evidence. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#source-policy. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemePropertiesInvalid}.");

    private static AppSurfaceKeycloakException TemplateBaselineInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemeTemplateBaselineInvalid,
            $"Problem: AppSurface Keycloak copied template baseline is invalid. Cause: {detail} Fix: review the upstream templates for the pinned image and declare their bounded baseline directory. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#template-overrides. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemeTemplateBaselineInvalid}.");
}
