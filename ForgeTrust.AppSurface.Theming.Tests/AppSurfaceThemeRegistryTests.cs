using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Theming.Tests;

public sealed class AppSurfaceThemeRegistryTests
{
    [Fact]
    public void AppSurfaceThemeId_ShouldRejectNonCanonicalValues()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AppSurfaceThemeId("AppSurface"));

        Assert.StartsWith("ASTHEME003:", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a0-9")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AppSurfaceThemeId_ShouldAcceptCanonicalBoundaryValues(string value)
    {
        Assert.Equal(value, new AppSurfaceThemeId(value).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0theme")]
    [InlineData("-theme")]
    [InlineData("theme-")]
    [InlineData("theme--alt")]
    [InlineData("Theme")]
    [InlineData("theme\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AppSurfaceThemeId_ShouldRejectMalformedOrOversizedValues(string? value)
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceThemeId(value!));
    }

    [Fact]
    public void AppSurfaceThemeId_ShouldRenderItsDefaultValueSafely()
    {
        Assert.Equal(string.Empty, default(AppSurfaceThemeId).ToString());
    }

    [Fact]
    public void Registry_ShouldResolveBuiltInPairAsImmutableSnapshot()
    {
        var options = CreateOptions();
        var registry = new AppSurfaceThemeRegistry(options);

        options.Pairs.Clear();
        options.DefaultMode = AppSurfaceThemeMode.Dark;

        var resolution = registry.ResolveDefault();

        Assert.Equal("appsurface", resolution.Id.Value);
        Assert.Equal(AppSurfaceThemeMode.System, resolution.Mode);
        Assert.Equal("#f8fafc", resolution.Light.Canvas);
        Assert.Equal("#0f172a", resolution.Dark.Canvas);
    }

    [Fact]
    public void Registry_ShouldResolveTheConfiguredCustomDefaultPair()
    {
        var options = CreateOptions();
        var alternate = CreateSecondPair();
        options.Pairs.Add(alternate);
        options.DefaultTheme = alternate.Id;
        options.DefaultMode = AppSurfaceThemeMode.Dark;

        var registry = new AppSurfaceThemeRegistry(options);
        var resolution = registry.ResolveDefault();

        Assert.Equal(alternate.Id, resolution.Id);
        Assert.Equal(AppSurfaceThemeMode.Dark, resolution.Mode);
        Assert.Equal(alternate.Light.Canvas, resolution.Light.Canvas);
        Assert.Equal(alternate.Dark.Canvas, resolution.Dark.Canvas);
        Assert.Equal(2, registry.ThemeIds.Count);
    }

    [Fact]
    public void IsSafeResolution_ShouldApplyTheNeutralRegistryContractWithoutWebSerialization()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var safe = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark);
        var unsupportedMode = new AppSurfaceThemeResolution(pair.Id, (AppSurfaceThemeMode)99, pair.Light, pair.Dark);
        var defaultId = new AppSurfaceThemeResolution(default, AppSurfaceThemeMode.System, pair.Light, pair.Dark);

        Assert.True(AppSurfaceThemeRegistry.IsSafeResolution(safe));
        Assert.False(AppSurfaceThemeRegistry.IsSafeResolution(null));
        Assert.False(AppSurfaceThemeRegistry.IsSafeResolution(unsupportedMode));
        Assert.False(AppSurfaceThemeRegistry.IsSafeResolution(defaultId));
    }

    [Fact]
    public void ServiceRegistration_ShouldSupportTheExplicitOptionsOverloadAndRejectNullArguments()
    {
        var options = CreateOptions();
        var services = new ServiceCollection();

        Assert.Same(services, services.AddAppSurfaceTheming(options));
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeServiceCollectionExtensions.AddAppSurfaceTheming(null!, options));
        Assert.Throws<ArgumentNullException>(() => services.AddAppSurfaceTheming((AppSurfaceThemeRegistryOptions)null!));
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeServiceCollectionExtensions.AddAppSurfaceTheming(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddAppSurfaceTheming((Action<AppSurfaceThemeRegistryOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeServiceCollectionExtensions.AddRequiredThemeExtension<string>(null!));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<AppSurfaceThemeRegistry>();

        Assert.Same(registry, provider.GetRequiredService<IAppSurfaceThemeRegistry>());
        Assert.Same(registry, provider.GetRequiredService<IAppSurfaceThemeResolver>());
        Assert.Equal("appsurface", registry.ResolveDefault().Id.Value);
    }

    [Fact]
    public void ServiceRegistration_ShouldKeepTheResolverAvailableWhenTheRegistryServiceIsReplaced()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(CreateOptions());
        services.AddSingleton<IAppSurfaceThemeRegistry>(new RegistryWithoutResolver());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RegistryWithoutResolver>(provider.GetRequiredService<IAppSurfaceThemeRegistry>());
        Assert.Equal("appsurface", provider.GetRequiredService<IAppSurfaceThemeResolver>().ResolveDefault().Id.Value);
    }

    [Fact]
    public void Validate_ShouldReportDuplicateIdsWithOrdinalSemantics()
    {
        var options = CreateOptions();
        options.Pairs.Add(AppSurfaceThemePair.AppSurface());

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ASTHEME004", diagnostic.Code);
    }

    [Fact]
    public void Validate_ShouldReportUnsupportedModeAndEmptyPairs()
    {
        var options = new AppSurfaceThemeRegistryOptions
        {
            DefaultMode = (AppSurfaceThemeMode)99
        };

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("ASTHEME001", diagnostic.Code),
            diagnostic => Assert.Equal("ASTHEME001", diagnostic.Code));
    }

    [Fact]
    public void Validate_ShouldReportNullPairAndMissingDefaultTheme()
    {
        var options = new AppSurfaceThemeRegistryOptions
        {
            DefaultTheme = new AppSurfaceThemeId("missing")
        };
        options.Pairs.Add(null!);

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("ASTHEME003", diagnostic.Code),
            diagnostic => Assert.Equal("ASTHEME002", diagnostic.Code));
    }

    [Fact]
    public void Validate_ShouldReportDefaultPairIdentifierAndMissingDefaultTheme()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var options = new AppSurfaceThemeRegistryOptions();
        options.Pairs.Add(new AppSurfaceThemePair(default, pair.Light, pair.Dark));

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("ASTHEME003", diagnostic.Code),
            diagnostic => Assert.Equal("ASTHEME002", diagnostic.Code));
    }

    [Fact]
    public void ThemeContracts_ShouldRejectNullConfigurationAndRoleSets()
    {
        var pair = AppSurfaceThemePair.AppSurface();

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceThemeRegistry(null!));
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeRegistry.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceThemePair(pair.Id, null!, pair.Dark));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceThemePair(pair.Id, pair.Light, null!));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, null!, pair.Dark));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, null!));
    }

    [Fact]
    public void Registry_ShouldExposeSafeDiagnosticsForInvalidConfiguration()
    {
        var exception = Assert.Throws<AppSurfaceThemeValidationException>(
            () => new AppSurfaceThemeRegistry(new AppSurfaceThemeRegistryOptions()));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("ASTHEME001", diagnostic.Code);
        Assert.Contains("Fix:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Docs:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldRejectNonOpaqueRoleColors()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var options = new AppSurfaceThemeRegistryOptions();
        options.Pairs.Add(
            new AppSurfaceThemePair(
                pair.Id,
                new AppSurfaceThemeRoles(
                    "rgba(1, 2, 3, .5)", pair.Light.Surface, pair.Light.RaisedSurface, pair.Light.Text, pair.Light.MutedText,
                    pair.Light.Border, pair.Light.Accent, pair.Light.AccentStrong, pair.Light.Link, pair.Light.VisitedLink, pair.Light.Danger, pair.Light.Focus),
                pair.Dark));

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ASTHEME005", diagnostic.Code);
        Assert.Contains("Light Canvas", diagnostic.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldReportUnsafeTextContrast()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var options = new AppSurfaceThemeRegistryOptions();
        options.Pairs.Add(
            new AppSurfaceThemePair(
                pair.Id,
                new AppSurfaceThemeRoles(
                    pair.Light.Canvas, pair.Light.Surface, pair.Light.RaisedSurface, "#f8fafc", pair.Light.MutedText,
                    pair.Light.Border, pair.Light.Accent, pair.Light.AccentStrong, pair.Light.Link, pair.Light.VisitedLink, pair.Light.Danger, pair.Light.Focus),
                pair.Dark));

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ASTHEME101" && diagnostic.Cause.Contains("Light.Text", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ContrastRoleCases))]
    public void Validate_ShouldRejectEveryContrastRoleAgainstEachBranchBackground(string branch, string role)
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var branchRoles = branch == "Light" ? pair.Light : pair.Dark;
        var unsafeRoles = ReplaceRole(branchRoles, role, branchRoles.Canvas);
        var options = new AppSurfaceThemeRegistryOptions();
        options.Pairs.Add(
            new AppSurfaceThemePair(
                pair.Id,
                branch == "Light" ? unsafeRoles : pair.Light,
                branch == "Dark" ? unsafeRoles : pair.Dark));

        var diagnostics = AppSurfaceThemeRegistry.Validate(options);

        Assert.Equal(
            3,
            diagnostics.Count(
                diagnostic => diagnostic.Code == "ASTHEME101"
                              && diagnostic.Cause.Contains($"{branch}.{role}", StringComparison.Ordinal)));
    }

    public static IEnumerable<object[]> ContrastRoleCases()
    {
        foreach (var branch in new[] { "Light", "Dark" })
        {
            foreach (var role in new[] { "Text", "MutedText", "Link", "VisitedLink", "Danger", "Border", "Accent", "AccentStrong", "Focus" })
            {
                yield return [branch, role];
            }
        }
    }

    [Fact]
    public void Registry_ShouldRejectUnknownPair()
    {
        var registry = new AppSurfaceThemeRegistry(CreateOptions());

        var exception = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired(new AppSurfaceThemeId("missing")));

        Assert.StartsWith("ASTHEME002:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_ShouldRejectTheDefaultThemeIdWithTheDocumentedException()
    {
        var registry = new AppSurfaceThemeRegistry(CreateOptions());

        var exception = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired(default));

        Assert.StartsWith("ASTHEME002:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_ShouldReturnRegisteredPairAsDefensiveSnapshot()
    {
        var options = CreateOptions();
        var configuredPair = Assert.Single(options.Pairs);
        var registry = new AppSurfaceThemeRegistry(options);

        var snapshot = registry.GetRequired(configuredPair.Id);

        Assert.Equal(configuredPair, snapshot);
        Assert.NotSame(configuredPair, snapshot);
        Assert.NotSame(configuredPair.Light, snapshot.Light);
        Assert.NotSame(configuredPair.Dark, snapshot.Dark);
    }

    [Fact]
    public void ValidationException_ShouldSnapshotDiagnostics()
    {
        var diagnostics = new List<AppSurfaceThemeDiagnostic>
        {
            AppSurfaceThemeDiagnostic.Create("ASTHEME001", "problem", "cause", "fix")
        };
        var exception = new AppSurfaceThemeValidationException(diagnostics);

        diagnostics.Clear();

        Assert.Single(exception.Diagnostics);
    }

    [Fact]
    public void RequiredExtension_ShouldFailWhenTheProviderIsMissing()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        services.AddRequiredThemeExtension<ExtensionSettings>();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<AppSurfaceThemeValidationException>(
            () => provider.GetRequiredService<IAppSurfaceThemeRegistry>());

        Assert.Equal("ASTHEME201", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void RequiredExtension_ShouldFailWhenARegisteredPairHasNoSettings()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        services.AddRequiredThemeExtension<ExtensionSettings>();
        services.AddSingleton<IAppSurfaceThemeExtensionProvider<ExtensionSettings>>(new MissingExtensionProvider());
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<AppSurfaceThemeValidationException>(
            () => provider.GetRequiredService<IAppSurfaceThemeRegistry>());

        Assert.Equal("ASTHEME202", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void RequiredExtension_ShouldFailWhenAProviderReturnsNullSettings()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        services.AddRequiredThemeExtension<ExtensionSettings>();
        services.AddSingleton<IAppSurfaceThemeExtensionProvider<ExtensionSettings>>(new NullSettingsExtensionProvider());
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<AppSurfaceThemeValidationException>(
            () => provider.GetRequiredService<IAppSurfaceThemeRegistry>());

        Assert.Equal("ASTHEME202", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void RequiredExtension_ShouldValidateEveryPairExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(
            options =>
            {
                options.Pairs.Add(AppSurfaceThemePair.AppSurface());
                options.Pairs.Add(CreateSecondPair());
            });
        services.AddRequiredThemeExtension<ExtensionSettings>();
        var extensionProvider = new CompleteExtensionProvider();
        services.AddSingleton<IAppSurfaceThemeExtensionProvider<ExtensionSettings>>(extensionProvider);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IAppSurfaceThemeRegistry>();

        Assert.Equal(2, registry.ThemeIds.Count);
        Assert.Equal(2, extensionProvider.RequestedThemeIds.Count);
        Assert.Equal(registry.ThemeIds, extensionProvider.RequestedThemeIds);
    }

    private static AppSurfaceThemeRegistryOptions CreateOptions()
    {
        var options = new AppSurfaceThemeRegistryOptions();
        options.Pairs.Add(AppSurfaceThemePair.AppSurface());
        return options;
    }

    private static AppSurfaceThemePair CreateSecondPair()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        return new AppSurfaceThemePair(new AppSurfaceThemeId("appsurface-alt"), pair.Light, pair.Dark);
    }

    private static AppSurfaceThemeRoles ReplaceRole(AppSurfaceThemeRoles roles, string role, string value) =>
        new(
            roles.Canvas,
            roles.Surface,
            roles.RaisedSurface,
            role == "Text" ? value : roles.Text,
            role == "MutedText" ? value : roles.MutedText,
            role == "Border" ? value : roles.Border,
            role == "Accent" ? value : roles.Accent,
            role == "AccentStrong" ? value : roles.AccentStrong,
            role == "Link" ? value : roles.Link,
            role == "VisitedLink" ? value : roles.VisitedLink,
            role == "Danger" ? value : roles.Danger,
            role == "Focus" ? value : roles.Focus);

    private sealed record ExtensionSettings(string Name);

    private sealed class RegistryWithoutResolver : IAppSurfaceThemeRegistry
    {
        public IReadOnlyCollection<AppSurfaceThemeId> ThemeIds => [];

        public AppSurfaceThemePair GetRequired(AppSurfaceThemeId id) => throw new KeyNotFoundException();
    }

    private sealed class MissingExtensionProvider : IAppSurfaceThemeExtensionProvider<ExtensionSettings>
    {
        public bool TryGet(AppSurfaceThemeId themeId, out ExtensionSettings settings)
        {
            settings = null!;
            return false;
        }
    }

    private sealed class CompleteExtensionProvider : IAppSurfaceThemeExtensionProvider<ExtensionSettings>
    {
        public List<AppSurfaceThemeId> RequestedThemeIds { get; } = [];

        public bool TryGet(AppSurfaceThemeId themeId, out ExtensionSettings settings)
        {
            RequestedThemeIds.Add(themeId);
            settings = new ExtensionSettings(themeId.Value);
            return true;
        }
    }

    private sealed class NullSettingsExtensionProvider : IAppSurfaceThemeExtensionProvider<ExtensionSettings>
    {
        public bool TryGet(AppSurfaceThemeId themeId, out ExtensionSettings settings)
        {
            settings = null!;
            return true;
        }
    }
}
