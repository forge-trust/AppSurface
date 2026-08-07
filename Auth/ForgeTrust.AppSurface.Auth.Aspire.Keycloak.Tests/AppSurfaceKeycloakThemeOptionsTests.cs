using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakThemeOptionsTests
{
    [Fact]
    public void ImageReference_ParseCanonicalImmutableReference_ReturnsStructuredValues()
    {
        var image = AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}");

        Assert.Equal("quay.io", image.Registry);
        Assert.Equal("keycloak/keycloak", image.Image);
        Assert.Equal("26.6", image.Tag);
        Assert.Equal(new string('a', 64), image.Sha256);
        Assert.Equal($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}", image.Value);
        Assert.Equal(image.Value, image.ToString());
    }

    [Theory]
    [InlineData("quay.io/keycloak/keycloak:26.6")]
    [InlineData("keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io/keycloak/keycloak@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io/keycloak/keycloak:26.6@sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    [InlineData(" quay.io/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ImageReference_ParseNonDeterministicReference_ThrowsSafeDiagnostic(string reference)
    {
        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => AppSurfaceKeycloakImageReference.Parse(reference));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration, exception.Code);
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageReference_ParseRegistryWithValidPort_ReturnsStructuredValues()
    {
        var image = AppSurfaceKeycloakImageReference.Parse($"localhost:5000/keycloak/keycloak:26.6@sha256:{new string('a', 64)}");

        Assert.Equal("localhost:5000", image.Registry);
        Assert.Equal("keycloak/keycloak", image.Image);
    }

    [Theory]
    [InlineData("quay..io/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io:0/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io:65536/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io:/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("quay.io:not-a-port/keycloak/keycloak:26.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ImageReference_ParseInvalidRegistryOrPort_ThrowsSafeDiagnostic(string reference)
    {
        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => AppSurfaceKeycloakImageReference.Parse(reference));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration, exception.Code);
    }

    [Theory]
    [InlineData("Application", "linux/amd64")]
    [InlineData("application", "linux/arm64")]
    public void CreateRegistration_WhenNameOrPlatformIsInvalid_ThrowsConfigurationDiagnostic(string name, string platform)
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        theme.Name = name;
        theme.Platform = platform;

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenSourceDirectoryIsBlank_ThrowsConfigurationDiagnostic()
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        theme.SourceDirectory = " ";

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenBaseImageIsNull_ThrowsArgumentNullException()
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        theme.BaseImage = null!;

        Assert.Throws<ArgumentNullException>(() => theme.CreateRegistration(directory.Path));
    }

    [Fact]
    public void CreateRegistration_ValidAssetsOnlyTheme_ProducesStableSafeManifest()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "resources", "site.css"), "body { color: black; }");
        File.WriteAllText(Path.Join(source, "login", "resources", "logo.svg"), "<svg></svg>");

        var theme = CreateOptions(source);
        theme.RequiredThemeProperties.Add("parent");
        theme.RequiredResourcePaths.Add("login/resources/site.css");
        theme.DevelopmentOnlyResourcePaths.Add("login/resources/logo.svg");

        var first = theme.CreateRegistration(directory.Path);
        var second = theme.CreateRegistration(directory.Path);

        Assert.Equal("application", first.Registration.Name);
        Assert.Equal("linux/amd64", first.Registration.Platform);
        Assert.Equal(first.Manifest.Digest, first.Registration.ManifestDigest);
        Assert.Equal(first.Manifest.Digest, second.Manifest.Digest);
        Assert.Equal(
            ["login/resources/logo.svg", "login/resources/site.css", "login/theme.properties"],
            first.Manifest.Files.Select(file => file.RelativePath).ToArray());
        Assert.DoesNotContain(directory.Path, first.Registration.ManifestDigest, StringComparison.Ordinal);
        Assert.DoesNotContain("parent=keycloak", first.Manifest.Digest, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRegistration_MissingThemePropertiesFile_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = Path.Join(directory.Path, "application");
        Directory.CreateDirectory(Path.Join(source, "login"));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_MissingSourceDirectory_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = Path.Join(directory.Path, "missing");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenRequiredPropertyHasNoExplicitValue_AcceptsJavaPropertiesSyntax()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "theme.properties"), "parent\n");
        var theme = CreateOptions(source);
        theme.RequiredThemeProperties.Add("parent");

        theme.CreateRegistration(directory.Path);
    }

    [Fact]
    public void CreateRegistration_WhenRequiredPropertyUsesWhitespaceSeparator_AcceptsJavaPropertiesSyntax()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "theme.properties"), "parent keycloak\n");
        var theme = CreateOptions(source);
        theme.RequiredThemeProperties.Add("parent");

        theme.CreateRegistration(directory.Path);
    }

    [Fact]
    public void CreateRegistration_WhenRequiredPropertyIsMissing_ThrowsRequirementsDiagnostic()
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        theme.RequiredThemeProperties.Add("missing");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemePropertiesInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenPropertiesIncludeCommentsBlankValuesAndEscapedSeparators_AcceptsRequiredName()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(
            Path.Join(source, "login", "theme.properties"),
            "# comment\n! another comment\n\n=empty-key\nparent\\:variant=keycloak\n");
        var theme = CreateOptions(source);
        theme.RequiredThemeProperties.Add(@"parent\:variant");

        theme.CreateRegistration(directory.Path);
    }

    [Theory]
    [InlineData("../outside.css")]
    [InlineData("/login/resources/site.css")]
    [InlineData("login//resources/site.css")]
    public void CreateRegistration_WhenDeclaredResourcePathIsUnsafe_ThrowsConfigurationDiagnostic(string resourcePath)
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        theme.RequiredResourcePaths.Add(resourcePath);

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration, exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateRegistration_WhenDeclaredResourceIsMissing_ThrowsRequirementsDiagnostic(bool developmentOnly)
    {
        using var directory = new TempDirectory();
        var theme = CreateOptions(CreateTheme(directory.Path, "application"));
        if (developmentOnly)
        {
            theme.DevelopmentOnlyResourcePaths.Add("login/resources/missing.css");
        }
        else
        {
            theme.RequiredResourcePaths.Add("login/resources/missing.css");
        }

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemePropertiesInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_UnsupportedSourceFile_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, ".DS_Store"), "not a theme resource");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
        Assert.Contains("unsupported file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAndRealmGenerator_WhenLoginThemeConfigured_EmitThemeSelectionOnly()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        var options = new AppSurfaceKeycloakOptions
        {
            LoginTheme = CreateOptions(source),
        };

        var json = AppSurfaceKeycloakRealmGenerator.Generate(options);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("application", document.RootElement.GetProperty("loginTheme").GetString());
        Assert.DoesNotContain(source, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildContract_WriteAndVerify_ProducesImmutableImageReadySnapshot()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "resources", "site.css"), "body { color: black; }");
        var contract = AppSurfaceKeycloakThemeBuildContract.Create(CreateOptions(source));
        var destination = Path.Join(directory.Path, "build-context");

        var result = contract.Write(destination);

        Assert.Equal(Path.GetFullPath(destination), result);
        var containerfile = File.ReadAllText(Path.Join(destination, "Containerfile"));
        Assert.Contains(contract.Registration.BaseImage, containerfile, StringComparison.Ordinal);
        Assert.Contains(contract.Manifest.Digest, containerfile, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(destination, "appsurface-keycloak-theme-manifest.json")));
        contract.VerifyPackagedTheme(Path.Join(destination, "themes", "application"));
    }

    [Fact]
    public void BuildContract_VerifyPackagedThemeWhenContentDrifts_ThrowsBuildDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "resources", "site.css"), "body { color: black; }");
        var contract = AppSurfaceKeycloakThemeBuildContract.Create(CreateOptions(source));
        var destination = contract.Write(Path.Join(directory.Path, "build-context"));
        File.WriteAllText(Path.Join(destination, "themes", "application", "login", "resources", "site.css"), "body { color: red; }");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => contract.VerifyPackagedTheme(Path.Join(destination, "themes", "application")));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid, exception.Code);
        Assert.Contains(contract.Manifest.Digest, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContract_VerifyPackagedThemeWhenUnexpectedFileExists_ThrowsBuildDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        var contract = AppSurfaceKeycloakThemeBuildContract.Create(CreateOptions(source));
        var destination = contract.Write(Path.Join(directory.Path, "build-context"));
        var resources = Path.Join(destination, "themes", "application", "login", "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Join(resources, "unexpected.txt"), "not allowed");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => contract.VerifyPackagedTheme(Path.Join(destination, "themes", "application")));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid, exception.Code);
    }

    [Fact]
    public void BuildContract_WhenOutputOrPackagedPathIsInvalid_ThrowsBuildDiagnostic()
    {
        using var directory = new TempDirectory();
        var contract = AppSurfaceKeycloakThemeBuildContract.Create(CreateOptions(CreateTheme(directory.Path, "application")));
        var existingDirectory = Path.Join(directory.Path, "existing-directory");
        var existingFile = Path.Join(directory.Path, "existing-file");
        Directory.CreateDirectory(existingDirectory);
        File.WriteAllText(existingFile, "occupied");

        var blankOutput = Assert.Throws<AppSurfaceKeycloakException>(() => contract.Write(" "));
        var directoryOutput = Assert.Throws<AppSurfaceKeycloakException>(() => contract.Write(existingDirectory));
        var fileOutput = Assert.Throws<AppSurfaceKeycloakException>(() => contract.Write(existingFile));
        var blankPackagedTheme = Assert.Throws<AppSurfaceKeycloakException>(() => contract.VerifyPackagedTheme(" "));

        Assert.All(
            [blankOutput, directoryOutput, fileOutput, blankPackagedTheme],
            exception => Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeBuildContractInvalid, exception.Code));
    }

    [Fact]
    public void CreateRegistration_WhenCopiedTemplateHasNoBaseline_ThrowsTemplateDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "login.ftl"), "<#import \"template.ftl\" as layout>");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeTemplateBaselineInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenCopiedTemplateHasReviewedBaseline_RecordsBaselineDigest()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "login.ftl"), "application override");
        var baseline = Path.Join(directory.Path, "baseline");
        Directory.CreateDirectory(Path.Join(baseline, "login"));
        File.WriteAllText(Path.Join(baseline, "login", "login.ftl"), "upstream source");
        var theme = CreateOptions(source);
        theme.TemplateBaselineDirectory = baseline;

        var registration = theme.CreateRegistration(directory.Path);

        Assert.NotNull(registration.Registration.TemplateBaselineDigest);
        Assert.Contains(registration.Registration.TemplateBaselineDigest, AppSurfaceKeycloakThemeBuildContract.Create(theme).CreateContainerfile(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRegistration_WhenCopiedTemplateIsMissingFromReviewedBaseline_ThrowsTemplateDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "login.ftl"), "application override");
        var baseline = Path.Join(directory.Path, "baseline");
        Directory.CreateDirectory(Path.Join(baseline, "login"));
        File.WriteAllText(Path.Join(baseline, "login", "other.ftl"), "upstream source");
        var theme = CreateOptions(source);
        theme.TemplateBaselineDirectory = baseline;

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeTemplateBaselineInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenTemplateBaselineCannotBeRead_ThrowsTemplateDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllText(Path.Join(source, "login", "login.ftl"), "application override");
        var theme = CreateOptions(source);
        theme.TemplateBaselineDirectory = Path.Join(directory.Path, "missing-baseline");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeTemplateBaselineInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenSourceFileExceedsLimit_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        File.WriteAllBytes(Path.Join(source, "login", "resources", "too-large.css"), new byte[1_048_577]);

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenSourceTotalExceedsLimit_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        for (var index = 0; index < 8; index++)
        {
            File.WriteAllBytes(Path.Join(source, "login", "resources", $"asset-{index}.css"), new byte[1_048_576]);
        }

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
    }

    [Fact]
    public void CreateRegistration_WhenSourceExceedsFileCount_ThrowsSourceDiagnostic()
    {
        using var directory = new TempDirectory();
        var source = CreateTheme(directory.Path, "application");
        for (var index = 0; index < 256; index++)
        {
            File.WriteAllText(Path.Join(source, "login", "resources", $"asset-{index}.css"), "body {}");
        }

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => CreateOptions(source).CreateRegistration(directory.Path));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid, exception.Code);
    }

    private static AppSurfaceKeycloakThemeOptions CreateOptions(string source) =>
        AppSurfaceKeycloakThemeOptions.Login(
            "application",
            source,
            AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}"));

    private static string CreateTheme(string root, string name)
    {
        var source = Path.Join(root, name);
        Directory.CreateDirectory(Path.Join(source, "login", "resources"));
        File.WriteAllText(Path.Join(source, "login", "theme.properties"), "parent=keycloak\nstyles=css/site.css\n");
        return source;
    }
}
