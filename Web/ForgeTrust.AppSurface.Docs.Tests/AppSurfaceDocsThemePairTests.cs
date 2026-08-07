using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsThemePairTests
{
    [Fact]
    public void Resolver_ShouldMapSharedSystemPairIntoDocsInternalCriticalCss()
    {
        var options = new AppSurfaceDocsOptions();
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.True(theme.UsesSharedTheme);
        Assert.NotNull(theme.CriticalCss);
        Assert.StartsWith("html[data-as-theme]{", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-surface-canvas:var(--as-canvas);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent:color-mix(in srgb, var(--as-accent-strong) 42%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-hover:color-mix(in srgb, var(--as-accent) 56%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-muted:color-mix(in srgb, var(--as-accent-strong) 34%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-active:color-mix(in srgb, var(--as-accent) 48%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-subtle:color-mix(in srgb, var(--as-accent-strong) 22%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-faint:color-mix(in srgb, var(--as-accent-strong) 12%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-strong:color-mix(in srgb, var(--as-accent) 70%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-readable:color-mix(in srgb, var(--as-accent) 62%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-accent-glow:color-mix(in srgb, var(--as-accent) 12%, transparent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-syntax-keyword:var(--as-visited-link);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-focus-outline:2px solid var(--as-focus);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-surface-canvas:Canvas;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-default:GrayText;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-text-default:CanvasText;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-accent:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-hover:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-muted:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-active:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-subtle:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-faint:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-strong:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-border-accent-readable:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-accent-glow:Highlight;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-link:LinkText;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-link-visited:VisitedText;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-syntax-comment:GrayText;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-focus-outline:2px solid Highlight;", theme.CriticalCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_ShouldKeepGraphiteOnTheLegacyLocalPath()
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Preset = AppSurfaceDocsThemePreset.GraphiteDark
            }
        };
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.Light, pair.Light, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.False(theme.UsesSharedTheme);
        Assert.Null(theme.CriticalCss);
        Assert.Equal("#080a0d", theme.CssVariables["--docs-color-surface-canvas"]);
    }

    [Fact]
    public void Resolver_ShouldPreserveShortHexOverridesWhenMappingSharedDarkPair()
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Colors = new AppSurfaceDocsThemeColorOptions
                {
                    AccentColor = "#0af",
                    AccentStrongColor = "#88f",
                    LinkColor = "#9cf",
                    VisitedLinkColor = "#fbf"
                }
            }
        };
        AppSurfaceDocsThemePolicy.Normalize(options.Theme);
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.Dark, pair.Light, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.Contains("--docs-color-accent:#0af;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-link:#9cf;", theme.CriticalCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_ShouldRejectDarkSafeOverridesWhenTheWebPreferenceDocumentAlsoExposesLight()
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Colors = new AppSurfaceDocsThemeColorOptions { LinkColor = "#9cf" }
            }
        };
        AppSurfaceDocsThemePolicy.Normalize(options.Theme);
        var pair = AppSurfaceThemePair.AppSurface();
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(themeOptions =>
        {
            themeOptions.Pairs.Add(pair);
            themeOptions.DefaultMode = AppSurfaceThemeMode.Dark;
        });
        services.AddAppSurfaceWebThemePreferences();
        using var provider = services.BuildServiceProvider();
        var darkResolution = provider.GetRequiredService<IAppSurfaceThemeResolver>().ResolveDefault();

        var theme = new AppSurfaceDocsThemeResolver(
            options,
            new StubThemeResolver(darkResolution),
            provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>()).Theme;

        Assert.Contains("--docs-color-link:var(--as-link);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.DoesNotContain("--docs-color-link:#9cf;", theme.CriticalCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_ShouldPreserveContrastSafeOverridesWhenMappingSharedLightPair()
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Colors = new AppSurfaceDocsThemeColorOptions
                {
                    AccentColor = "#1d4ed8",
                    AccentStrongColor = "#1d4ed8",
                    LinkColor = "#1d4ed8",
                    VisitedLinkColor = "#6d28d9"
                }
            }
        };
        AppSurfaceDocsThemePolicy.Normalize(options.Theme);
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.Light, pair.Light, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.Contains("--docs-color-accent:#1d4ed8;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-accent-strong:#1d4ed8;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-link:#1d4ed8;", theme.CriticalCss, StringComparison.Ordinal);
        Assert.Contains("--docs-color-link-visited:#6d28d9;", theme.CriticalCss, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppSurfaceThemeMode.Light)]
    [InlineData(AppSurfaceThemeMode.System)]
    public void Resolver_ShouldFallBackToSemanticTokensForUnsafeSharedLightOverrides(AppSurfaceThemeMode mode)
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Colors = new AppSurfaceDocsThemeColorOptions
                {
                    LinkColor = "#9cf"
                }
            }
        };
        AppSurfaceDocsThemePolicy.Normalize(options.Theme);
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, mode, pair.Light, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.Contains("--docs-color-link:var(--as-link);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.DoesNotContain("--docs-color-link:#9cf;", theme.CriticalCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_ShouldKeepLegacyDocsVariablesWhenTheCustomResolutionIsUnsafe()
    {
        var options = new AppSurfaceDocsOptions();
        var pair = AppSurfaceThemePair.AppSurface();
        var unsafeLight = new AppSurfaceThemeRoles(
            pair.Light.Canvas, pair.Light.Surface, pair.Light.RaisedSurface, pair.Light.Canvas, pair.Light.MutedText,
            pair.Light.Border, pair.Light.Accent, pair.Light.AccentStrong, pair.Light.Link, pair.Light.VisitedLink,
            pair.Light.Danger, pair.Light.Focus);
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, unsafeLight, pair.Dark);

        var theme = new AppSurfaceDocsThemeResolver(options, new StubThemeResolver(resolution)).Theme;

        Assert.False(theme.UsesSharedTheme);
        Assert.Null(theme.CriticalCss);
        Assert.NotEmpty(theme.CssVariableStyle);
    }

    [Fact]
    public void SharedPolicy_ShouldFallBackToSemanticTokensForAnUnsupportedMode()
    {
        var options = new AppSurfaceDocsOptions
        {
            Theme = new AppSurfaceDocsThemeOptions
            {
                Colors = new AppSurfaceDocsThemeColorOptions
                {
                    AccentColor = "#1d4ed8"
                }
            }
        };
        AppSurfaceDocsThemePolicy.Normalize(options.Theme);
        var pair = AppSurfaceThemePair.AppSurface();
        var unsupportedResolution = new AppSurfaceThemeResolution(pair.Id, (AppSurfaceThemeMode)99, pair.Light, pair.Dark);

        var theme = AppSurfaceDocsThemePolicy.ResolveShared(
            AppSurfaceDocsThemePolicy.Resolve(options.Theme),
            options.Theme,
            unsupportedResolution);

        Assert.Contains("--docs-color-accent:var(--as-accent);", theme.CriticalCss, StringComparison.Ordinal);
        Assert.DoesNotContain("--docs-color-accent:#1d4ed8;", theme.CriticalCss, StringComparison.Ordinal);
    }

    private sealed class StubThemeResolver(AppSurfaceThemeResolution resolution) : IAppSurfaceThemeResolver
    {
        public AppSurfaceThemeResolution ResolveDefault() => resolution;
    }

}
