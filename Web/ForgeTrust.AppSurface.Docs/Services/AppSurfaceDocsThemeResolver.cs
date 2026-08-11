using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.Theming;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves normalized AppSurface Docs theme options into render-ready CSS variables and shell attributes.
/// </summary>
/// <remarks>
/// The resolved theme is safe to cache as a singleton because it contains only preset names, density/chrome flags, and
/// sanitized CSS custom property declarations. Razor views emit these values into the exported HTML so live docs, static
/// export, and published archives share the same frozen visual contract.
/// </remarks>
internal sealed class AppSurfaceDocsThemeResolver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsThemeResolver"/> class.
    /// </summary>
    /// <param name="options">The normalized AppSurface Docs options.</param>
    public AppSurfaceDocsThemeResolver(AppSurfaceDocsOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsThemeResolver"/> with optional shared theme support.
    /// </summary>
    /// <param name="options">The normalized AppSurface Docs options.</param>
    /// <param name="themeResolver">The shared resolver selected by the container, or <see langword="null"/> for the legacy Docs contract.</param>
    /// <param name="themePreferenceOptions">
    /// The optional browser-preference registration. Its presence makes every shared Docs override satisfy contrast
    /// requirements in both branches, even when the neutral default is fixed Light or Dark. The resolver intentionally
    /// does not consume the request-scoped Web document provider, so host-owned pair selection remains request-bound.
    /// </param>
    public AppSurfaceDocsThemeResolver(
        AppSurfaceDocsOptions options,
        IAppSurfaceThemeResolver? themeResolver,
        AppSurfaceThemePreferenceOptions? themePreferenceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var legacyTheme = AppSurfaceDocsThemePolicy.Resolve(options.Theme);
        var sharedResolution = themeResolver?.ResolveDefault();
        if (sharedResolution is not null && themePreferenceOptions is not null)
        {
            sharedResolution = new AppSurfaceThemeResolution(
                sharedResolution.Id,
                AppSurfaceThemeMode.System,
                sharedResolution.Light,
                sharedResolution.Dark);
        }

        Theme = sharedResolution is not null
            && legacyTheme.Preset == AppSurfaceDocsThemePreset.AppSurfaceDark
            && AppSurfaceThemeRegistry.IsSafeResolution(sharedResolution)
            ? AppSurfaceDocsThemePolicy.ResolveShared(legacyTheme, options.Theme, sharedResolution)
            : legacyTheme;
    }

    /// <summary>
    /// Gets the render-ready theme used by AppSurface Docs layouts.
    /// </summary>
    public AppSurfaceDocsResolvedTheme Theme { get; }
}

/// <summary>
/// Render-ready AppSurface Docs theme values.
/// </summary>
/// <param name="Preset">The selected public preset.</param>
/// <param name="Density">The selected public density.</param>
/// <param name="Chrome">The selected public chrome compactness.</param>
/// <param name="PresetAttribute">Kebab-case value emitted in <c>data-docs-theme-preset</c>.</param>
/// <param name="DensityAttribute">Kebab-case value emitted in <c>data-docs-density</c>.</param>
/// <param name="ChromeAttribute">Kebab-case value emitted in <c>data-docs-chrome</c>.</param>
/// <param name="RootCssClass">CSS classes emitted on the document root.</param>
/// <param name="CssVariables">Resolved CSS custom properties consumed by the package stylesheets.</param>
/// <param name="CssVariableStyle">Serialized CSS custom property declarations suitable for a style attribute.</param>
/// <param name="UsesSharedTheme">Whether the root consumes the shared AppSurface semantic pair.</param>
/// <param name="CriticalCss">Docs-owned critical CSS emitted before the package stylesheet when <paramref name="UsesSharedTheme"/> is <see langword="true"/>.</param>
internal sealed record AppSurfaceDocsResolvedTheme(
    AppSurfaceDocsThemePreset Preset,
    AppSurfaceDocsThemeDensity Density,
    AppSurfaceDocsThemeChrome Chrome,
    string PresetAttribute,
    string DensityAttribute,
    string ChromeAttribute,
    string RootCssClass,
    IReadOnlyDictionary<string, string> CssVariables,
    string CssVariableStyle,
    bool UsesSharedTheme = false,
    string? CriticalCss = null);

/// <summary>
/// Centralizes normalization, validation, and render-ready resolution for the AppSurface Docs theme contract.
/// </summary>
/// <remarks>
/// Hosts configure <see cref="AppSurfaceDocsThemeOptions"/>, while this policy keeps every consumer on one resolved
/// theme boundary. Run <see cref="Normalize"/> during post-configuration before calling <see cref="Validate"/> or
/// <see cref="Resolve"/>.
/// </remarks>
internal static class AppSurfaceDocsThemePolicy
{
    private const double TextContrastRatio = 4.5d;
    private const double UserInterfaceContrastRatio = 3d;

    /// <summary>
    /// Normalizes mutable theme options in place.
    /// </summary>
    /// <param name="theme">The configured theme options to normalize.</param>
    /// <remarks>
    /// This method creates omitted nested sections and canonicalizes configured CSS hex colors. Call it during
    /// post-configuration before validation or resolution so those later operations observe a stable options shape.
    /// </remarks>
    public static void Normalize(AppSurfaceDocsThemeOptions theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        theme.Colors ??= new AppSurfaceDocsThemeColorOptions();
        theme.Layout ??= new AppSurfaceDocsThemeLayoutOptions();
        theme.Colors.AccentColor = NormalizeCssHexColorOrNull(theme.Colors.AccentColor);
        theme.Colors.AccentStrongColor = NormalizeCssHexColorOrNull(theme.Colors.AccentStrongColor);
        theme.Colors.LinkColor = NormalizeCssHexColorOrNull(theme.Colors.LinkColor);
        theme.Colors.VisitedLinkColor = NormalizeCssHexColorOrNull(theme.Colors.VisitedLinkColor);
    }

    /// <summary>
    /// Adds configuration failures for an AppSurface Docs theme.
    /// </summary>
    /// <param name="theme">The normalized theme options to validate.</param>
    /// <param name="failures">The destination for actionable validation messages.</param>
    /// <remarks>
    /// Contrast checks use the selected preset's canvas and raised backgrounds because v1 intentionally does not
    /// expose raw surface overrides. This makes the reported contrast guarantee match the surfaces the package renders.
    /// </remarks>
    public static void Validate(AppSurfaceDocsThemeOptions? theme, List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (theme is null)
        {
            failures.Add("AppSurfaceDocs:Theme must not be null.");
            return;
        }

        if (!Enum.IsDefined(theme.Preset))
        {
            failures.Add(
                $"AppSurfaceDocs:Theme:Preset has unsupported value '{theme.Preset}'. Allowed values are AppSurfaceDark and GraphiteDark.");
        }

        if (theme.Colors is null)
        {
            failures.Add("AppSurfaceDocs:Theme:Colors must not be null.");
        }
        else
        {
            ValidateThemeColor(failures, "AppSurfaceDocs:Theme:Colors:AccentColor", theme.Colors.AccentColor);
            ValidateThemeColor(failures, "AppSurfaceDocs:Theme:Colors:AccentStrongColor", theme.Colors.AccentStrongColor);
            ValidateThemeColor(failures, "AppSurfaceDocs:Theme:Colors:LinkColor", theme.Colors.LinkColor);
            ValidateThemeColor(failures, "AppSurfaceDocs:Theme:Colors:VisitedLinkColor", theme.Colors.VisitedLinkColor);

            if (Enum.IsDefined(theme.Preset))
            {
                ValidateConfiguredContrast(theme, failures);
            }
        }

        if (theme.Layout is null)
        {
            failures.Add("AppSurfaceDocs:Theme:Layout must not be null.");
        }
        else
        {
            if (!Enum.IsDefined(theme.Layout.Density))
            {
                failures.Add(
                    $"AppSurfaceDocs:Theme:Layout:Density has unsupported value '{theme.Layout.Density}'. Allowed values are Comfortable and Compact.");
            }

            if (!Enum.IsDefined(theme.Layout.Chrome))
            {
                failures.Add(
                    $"AppSurfaceDocs:Theme:Layout:Chrome has unsupported value '{theme.Layout.Chrome}'. Allowed values are Standard and Compact.");
            }
        }
    }

    /// <summary>
    /// Resolves theme options into the attributes and CSS variables consumed by rendered and exported docs.
    /// </summary>
    /// <param name="options">The normalized theme options to resolve.</param>
    /// <returns>The immutable theme contract for layouts, search, and static output.</returns>
    /// <remarks>
    /// Call <see cref="Normalize"/> before resolution for configured options. The null-tolerant fallback exists only
    /// to keep rendering defensive when no theme section is supplied.
    /// </remarks>
    public static AppSurfaceDocsResolvedTheme Resolve(AppSurfaceDocsThemeOptions? options)
    {
        var theme = options ?? new AppSurfaceDocsThemeOptions();
        var colors = theme.Colors ?? new AppSurfaceDocsThemeColorOptions();
        var layout = theme.Layout ?? new AppSurfaceDocsThemeLayoutOptions();
        var variables = BuildPreset(theme.Preset);
        ApplyOverrides(variables, colors);
        var cssVariables = new ReadOnlyDictionary<string, string>(variables);
        var presetAttribute = ToPresetAttribute(theme.Preset);
        var densityAttribute = ToDensityAttribute(layout.Density);
        var chromeAttribute = ToChromeAttribute(layout.Chrome);
        var rootCssClass = string.Create(
            CultureInfo.InvariantCulture,
            $"docs-theme-preset-{presetAttribute} docs-density-{densityAttribute} docs-chrome-{chromeAttribute}");

        return new AppSurfaceDocsResolvedTheme(
            theme.Preset,
            layout.Density,
            layout.Chrome,
            presetAttribute,
            densityAttribute,
            chromeAttribute,
            rootCssClass,
            cssVariables,
            SerializeCssVariables(cssVariables));
    }

    /// <summary>
    /// Maps a shared semantic pair into the AppSurface Docs internal CSS-variable graph.
    /// </summary>
    /// <param name="legacyTheme">Resolved Docs compatibility values.</param>
    /// <param name="options">Normalized Docs theme options, including supported legacy color overrides.</param>
    /// <param name="resolution">The safe shared semantic pair selected by the host.</param>
    /// <returns>A Docs theme that consumes shared semantic variables without exposing Docs-local variables publicly.</returns>
    public static AppSurfaceDocsResolvedTheme ResolveShared(
        AppSurfaceDocsResolvedTheme legacyTheme,
        AppSurfaceDocsThemeOptions? options,
        AppSurfaceThemeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(legacyTheme);
        ArgumentNullException.ThrowIfNull(resolution);

        return legacyTheme with
        {
            UsesSharedTheme = true,
            CriticalCss = BuildSharedCriticalCss(options?.Colors, resolution)
        };
    }

    private static string? NormalizeCssHexColorOrNull(string? value)
    {
        return AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(value, out var normalizedColor, out _)
            ? normalizedColor
            : AppSurfaceDocsIdentityPath.NormalizeTextOrNull(value);
    }

    private static void ValidateThemeColor(List<string> failures, string configurationPath, string? value)
    {
        var normalized = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(value);
        if (normalized is not null
            && !AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(normalized, out _, out var colorError))
        {
            failures.Add($"{configurationPath} value '{normalized}' {colorError}");
        }
    }

    private static void ValidateConfiguredContrast(AppSurfaceDocsThemeOptions theme, List<string> failures)
    {
        var colors = theme.Colors;
        var preset = BuildPreset(theme.Preset);
        var canvas = preset["--docs-color-surface-canvas"];
        var raised = preset["--docs-color-surface-raised"];

        AddContrastFailure(
            failures,
            "AppSurfaceDocs:Theme:Colors:AccentColor",
            colors.AccentColor,
            TextContrastRatio,
            "text accent",
            [canvas, raised]);
        AddContrastFailure(
            failures,
            "AppSurfaceDocs:Theme:Colors:AccentStrongColor",
            colors.AccentStrongColor,
            UserInterfaceContrastRatio,
            "focus and selected-state accent",
            [canvas, raised]);
        AddContrastFailure(
            failures,
            "AppSurfaceDocs:Theme:Colors:LinkColor",
            colors.LinkColor,
            TextContrastRatio,
            "link text",
            [canvas, raised]);
        AddContrastFailure(
            failures,
            "AppSurfaceDocs:Theme:Colors:VisitedLinkColor",
            colors.VisitedLinkColor,
            TextContrastRatio,
            "visited link text",
            [canvas, raised]);
    }

    private static void AddContrastFailure(
        List<string> failures,
        string configurationPath,
        string? value,
        double threshold,
        string role,
        IReadOnlyList<string> backgroundColors)
    {
        if (!AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(value, out var normalizedColor, out _)
            || normalizedColor is null)
        {
            return;
        }

        foreach (var backgroundColor in backgroundColors)
        {
            var ratio = ContrastRatio(normalizedColor, backgroundColor);
            if (ratio >= threshold)
            {
                continue;
            }

            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{configurationPath} value '{normalizedColor}' does not meet {threshold:0.#}:1 contrast for {role} against preset background '{backgroundColor}' (actual {ratio:0.##}:1). Choose a lighter or higher-contrast CSS hex color."));
            return;
        }
    }

    private static Dictionary<string, string> BuildPreset(AppSurfaceDocsThemePreset preset)
    {
        return preset == AppSurfaceDocsThemePreset.GraphiteDark
            ? BuildGraphiteDarkPreset()
            : BuildAppSurfaceDarkPreset();
    }

    private static Dictionary<string, string> BuildAppSurfaceDarkPreset()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--docs-color-surface-canvas"] = "#050b17",
            ["--docs-color-surface-canvas-mid"] = "#08101e",
            ["--docs-color-surface-canvas-deep"] = "#040812",
            ["--docs-color-surface-raised"] = "#0d182a",
            ["--docs-color-surface-muted"] = "rgba(24, 38, 64, 0.8)",
            ["--docs-color-surface-panel"] = "rgba(13, 24, 42, 0.72)",
            ["--docs-color-surface-panel-elevated"] = "rgba(13, 24, 42, 0.88)",
            ["--docs-color-surface-panel-heavy"] = "rgba(13, 24, 42, 0.92)",
            ["--docs-color-surface-panel-hover"] = "rgba(20, 35, 61, 0.56)",
            ["--docs-color-surface-panel-hover-strong"] = "rgba(20, 35, 61, 0.68)",
            ["--docs-color-surface-panel-raised"] = "rgba(13, 24, 42, 0.64)",
            ["--docs-color-surface-panel-active"] = "rgba(20, 35, 61, 0.78)",
            ["--docs-color-surface-panel-soft"] = "rgba(13, 24, 42, 0.48)",
            ["--docs-color-surface-panel-faint"] = "rgba(13, 24, 42, 0.34)",
            ["--docs-color-surface-overlay"] = "rgba(5, 11, 23, 0.78)",
            ["--docs-color-surface-overlay-strong"] = "rgba(5, 11, 23, 0.92)",
            ["--docs-color-surface-overlay-soft"] = "rgba(5, 11, 23, 0.68)",
            ["--docs-color-surface-code"] = "#0a1322",
            ["--docs-color-border-muted"] = "#1b2a43",
            ["--docs-color-border-default"] = "#314461",
            ["--docs-color-border-strong"] = "#526987",
            ["--docs-color-text-strong"] = "#f8fafc",
            ["--docs-color-text-default"] = "#e5e7eb",
            ["--docs-color-text-muted"] = "#c8d0dc",
            ["--docs-color-text-subtle"] = "#9aa8bc",
            ["--docs-color-text-faint"] = "#728098",
            ["--docs-color-text-prose"] = "#dbe2ec",
            ["--docs-color-text-info"] = "#dbeafe",
            ["--docs-color-text-info-muted"] = "#c7d2fe",
            ["--docs-color-text-mark"] = "#f5f7fb",
            ["--docs-color-accent"] = "#14b8a6",
            ["--docs-color-accent-strong"] = "#2563eb",
            ["--docs-color-accent-blue"] = "#2563eb",
            ["--docs-color-accent-violet"] = "#8b5cf6",
            ["--docs-color-accent-soft"] = "#ccfbf1",
            ["--docs-color-accent-muted"] = "#99f6e4",
            ["--docs-color-link"] = "#93c5fd",
            ["--docs-color-link-visited"] = "#c4b5fd",
            ["--docs-color-page-wash"] = "rgba(37, 99, 235, 0.08)",
            ["--docs-color-skeleton-edge"] = "rgba(27, 42, 67, 0.92)",
            ["--docs-color-skeleton-mid"] = "rgba(65, 87, 121, 0.55)",
            ["--docs-color-wordmark-edge-shadow"] = "rgba(0, 0, 0, 0.45)",
            ["--docs-shadow-copy-feedback"] = "0 14px 34px rgba(2, 6, 23, 0.32)",
            ["--docs-shadow-copy-fallback"] = "0 18px 46px rgba(2, 6, 23, 0.46)"
        };
        AddDerivedAccentVariables(variables);
        return variables;
    }

    private static Dictionary<string, string> BuildGraphiteDarkPreset()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--docs-color-surface-canvas"] = "#080a0d",
            ["--docs-color-surface-canvas-mid"] = "#101216",
            ["--docs-color-surface-canvas-deep"] = "#050608",
            ["--docs-color-surface-raised"] = "#151820",
            ["--docs-color-surface-muted"] = "rgba(34, 38, 46, 0.82)",
            ["--docs-color-surface-panel"] = "rgba(21, 24, 32, 0.74)",
            ["--docs-color-surface-panel-elevated"] = "rgba(27, 30, 39, 0.9)",
            ["--docs-color-surface-panel-heavy"] = "rgba(24, 27, 36, 0.94)",
            ["--docs-color-surface-panel-hover"] = "rgba(41, 46, 58, 0.58)",
            ["--docs-color-surface-panel-hover-strong"] = "rgba(48, 54, 68, 0.7)",
            ["--docs-color-surface-panel-raised"] = "rgba(24, 27, 36, 0.66)",
            ["--docs-color-surface-panel-active"] = "rgba(45, 51, 64, 0.78)",
            ["--docs-color-surface-panel-soft"] = "rgba(24, 27, 36, 0.5)",
            ["--docs-color-surface-panel-faint"] = "rgba(24, 27, 36, 0.36)",
            ["--docs-color-surface-overlay"] = "rgba(8, 10, 13, 0.78)",
            ["--docs-color-surface-overlay-strong"] = "rgba(8, 10, 13, 0.92)",
            ["--docs-color-surface-overlay-soft"] = "rgba(8, 10, 13, 0.68)",
            ["--docs-color-surface-code"] = "#0f131a",
            ["--docs-color-border-muted"] = "#252b36",
            ["--docs-color-border-default"] = "#3a4351",
            ["--docs-color-border-strong"] = "#647085",
            ["--docs-color-text-strong"] = "#f8fafc",
            ["--docs-color-text-default"] = "#e7e9ee",
            ["--docs-color-text-muted"] = "#c9ced8",
            ["--docs-color-text-subtle"] = "#a1a9b7",
            ["--docs-color-text-faint"] = "#788292",
            ["--docs-color-text-prose"] = "#dce1e8",
            ["--docs-color-text-info"] = "#dbeafe",
            ["--docs-color-text-info-muted"] = "#d8dff2",
            ["--docs-color-text-mark"] = "#f8fafc",
            ["--docs-color-accent"] = "#38bdf8",
            ["--docs-color-accent-strong"] = "#818cf8",
            ["--docs-color-accent-blue"] = "#818cf8",
            ["--docs-color-accent-violet"] = "#a5b4fc",
            ["--docs-color-accent-soft"] = "#e0f2fe",
            ["--docs-color-accent-muted"] = "#bae6fd",
            ["--docs-color-link"] = "#93c5fd",
            ["--docs-color-link-visited"] = "#c4b5fd",
            ["--docs-color-page-wash"] = "rgba(129, 140, 248, 0.07)",
            ["--docs-color-skeleton-edge"] = "rgba(37, 43, 54, 0.92)",
            ["--docs-color-skeleton-mid"] = "rgba(81, 91, 110, 0.55)",
            ["--docs-color-wordmark-edge-shadow"] = "rgba(0, 0, 0, 0.5)",
            ["--docs-shadow-copy-feedback"] = "0 14px 34px rgba(0, 0, 0, 0.3)",
            ["--docs-shadow-copy-fallback"] = "0 18px 46px rgba(0, 0, 0, 0.46)"
        };
        AddDerivedAccentVariables(variables);
        return variables;
    }

    private static void AddDerivedAccentVariables(Dictionary<string, string> variables)
    {
        var accent = variables["--docs-color-accent"];
        var accentStrong = variables["--docs-color-accent-strong"];
        var link = variables["--docs-color-link"];
        var raised = variables["--docs-color-surface-raised"];

        variables["--docs-color-border-accent"] = ToRgba(accentStrong, 0.42);
        variables["--docs-color-border-accent-hover"] = ToRgba(accent, 0.56);
        variables["--docs-color-border-accent-muted"] = ToRgba(accentStrong, 0.34);
        variables["--docs-color-border-accent-active"] = ToRgba(accent, 0.48);
        variables["--docs-color-border-accent-subtle"] = ToRgba(accentStrong, 0.22);
        variables["--docs-color-border-accent-faint"] = ToRgba(accentStrong, 0.12);
        variables["--docs-color-border-accent-strong"] = ToRgba(accent, 0.7);
        variables["--docs-color-border-accent-readable"] = ToRgba(accent, 0.62);
        variables["--docs-color-link-underline"] = ToRgba(link, 0.5);
        variables["--docs-color-accent-fill-soft"] = ToRgba(accentStrong, 0.14);
        variables["--docs-color-accent-mark-fill"] = ToRgba(accent, 0.28);
        variables["--docs-color-accent-underline"] = ToRgba(accent, 0.5);
        variables["--docs-color-accent-soft-underline"] = ToRgba(variables["--docs-color-accent-soft"], 0.78);
        variables["--docs-color-accent-soft-underline-muted"] = ToRgba(variables["--docs-color-accent-soft"], 0.7);
        variables["--docs-color-state-active-fill"] = ToRgba(accentStrong, 0.24);
        variables["--docs-color-state-active-fill-strong"] = ToRgba(accentStrong, 0.34);
        variables["--docs-color-state-link-fill"] = ToRgba(accentStrong, 0.28);
        variables["--docs-color-state-trust-fill-start"] = ToRgba(accentStrong, 0.18);
        variables["--docs-color-state-outline-fill"] = ToRgba(accentStrong, 0.46);
        variables["--docs-color-state-outline-fill-end"] = ToRgba(raised, 0.3);
        variables["--docs-color-state-outline-rail-start"] = ToRgba(accentStrong, 0.5);
        variables["--docs-color-state-outline-rail-mid"] = ToRgba(accent, 0.22);
        variables["--docs-color-state-outline-rail-end"] = ToRgba(raised, 0.18);
        variables["--docs-color-state-outline-rail-hover-start"] = ToRgba(accentStrong, 0.6);
        variables["--docs-color-state-outline-rail-hover-mid"] = ToRgba(accent, 0.3);
        variables["--docs-color-state-outline-rail-hover-end"] = ToRgba(raised, 0.24);
        variables["--docs-color-accent-glow"] = ToRgba(accent, 0.12);
        variables["--docs-focus-ring-inset"] = $"0 0 0 1px {accentStrong} inset";
        variables["--docs-focus-outline"] = $"2px solid {accentStrong}";
    }

    private static void ApplyOverrides(Dictionary<string, string> variables, AppSurfaceDocsThemeColorOptions colors)
    {
        if (AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(colors.AccentColor, out var accentColor, out _)
            && accentColor is not null)
        {
            variables["--docs-color-accent"] = accentColor;
            variables["--docs-color-accent-soft"] = accentColor;
            variables["--docs-color-accent-muted"] = accentColor;
        }

        if (AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(colors.AccentStrongColor, out var accentStrongColor, out _)
            && accentStrongColor is not null)
        {
            variables["--docs-color-accent-strong"] = accentStrongColor;
            variables["--docs-color-accent-blue"] = accentStrongColor;
        }

        if (AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(colors.LinkColor, out var linkColor, out _)
            && linkColor is not null)
        {
            variables["--docs-color-link"] = linkColor;
        }

        if (AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(colors.VisitedLinkColor, out var visitedLinkColor, out _)
            && visitedLinkColor is not null)
        {
            variables["--docs-color-link-visited"] = visitedLinkColor;
        }

        AddDerivedAccentVariables(variables);
    }

    private static string SerializeCssVariables(IReadOnlyDictionary<string, string> variables)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(key);
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }

        return builder.ToString();
    }

    private static string BuildSharedCriticalCss(
        AppSurfaceDocsThemeColorOptions? colors,
        AppSurfaceThemeResolution resolution)
    {
        var accent = ResolveSharedOverride(colors?.AccentColor, "var(--as-accent)", resolution, TextContrastRatio);
        var accentStrong = ResolveSharedOverride(colors?.AccentStrongColor, "var(--as-accent-strong)", resolution, UserInterfaceContrastRatio);
        var link = ResolveSharedOverride(colors?.LinkColor, "var(--as-link)", resolution, TextContrastRatio);
        var visitedLink = ResolveSharedOverride(colors?.VisitedLinkColor, "var(--as-visited-link)", resolution, TextContrastRatio);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--docs-brand-blue"] = accentStrong,
            ["--docs-brand-teal"] = accent,
            ["--docs-brand-violet"] = visitedLink,
            ["--docs-color-surface-canvas"] = "var(--as-canvas)",
            ["--docs-color-surface-canvas-mid"] = "color-mix(in srgb, var(--as-canvas) 82%, var(--as-surface))",
            ["--docs-color-surface-canvas-deep"] = "color-mix(in srgb, var(--as-canvas) 92%, var(--as-text))",
            ["--docs-color-surface-raised"] = "var(--as-raised-surface)",
            ["--docs-color-surface-muted"] = "color-mix(in srgb, var(--as-surface) 82%, transparent)",
            ["--docs-color-surface-panel"] = "color-mix(in srgb, var(--as-surface) 84%, transparent)",
            ["--docs-color-surface-panel-elevated"] = "color-mix(in srgb, var(--as-surface) 94%, transparent)",
            ["--docs-color-surface-panel-heavy"] = "color-mix(in srgb, var(--as-raised-surface) 96%, transparent)",
            ["--docs-color-surface-panel-hover"] = "color-mix(in srgb, var(--as-raised-surface) 72%, transparent)",
            ["--docs-color-surface-panel-hover-strong"] = "color-mix(in srgb, var(--as-raised-surface) 84%, transparent)",
            ["--docs-color-surface-panel-raised"] = "color-mix(in srgb, var(--as-raised-surface) 82%, transparent)",
            ["--docs-color-surface-panel-active"] = "color-mix(in srgb, var(--as-accent) 18%, var(--as-raised-surface))",
            ["--docs-color-surface-panel-soft"] = "color-mix(in srgb, var(--as-surface) 64%, transparent)",
            ["--docs-color-surface-panel-faint"] = "color-mix(in srgb, var(--as-surface) 44%, transparent)",
            ["--docs-color-surface-panel-table"] = "color-mix(in srgb, var(--as-surface) 76%, transparent)",
            ["--docs-color-surface-panel-table-head"] = "color-mix(in srgb, var(--as-raised-surface) 92%, transparent)",
            ["--docs-color-surface-overlay"] = "color-mix(in srgb, var(--as-canvas) 78%, transparent)",
            ["--docs-color-surface-overlay-strong"] = "color-mix(in srgb, var(--as-canvas) 92%, transparent)",
            ["--docs-color-surface-overlay-soft"] = "color-mix(in srgb, var(--as-canvas) 68%, transparent)",
            ["--docs-color-surface-overlay-faint"] = "color-mix(in srgb, var(--as-canvas) 32%, transparent)",
            ["--docs-color-surface-code"] = "color-mix(in srgb, var(--as-canvas) 92%, var(--as-raised-surface))",
            ["--docs-color-surface-code-plain"] = "color-mix(in srgb, var(--as-surface) 82%, var(--as-raised-surface))",
            ["--docs-color-border-muted"] = "color-mix(in srgb, var(--as-border) 56%, transparent)",
            ["--docs-color-border-muted-heavy"] = "color-mix(in srgb, var(--as-border) 86%, transparent)",
            ["--docs-color-border-code-plain"] = "var(--as-border)",
            ["--docs-color-border-default"] = "var(--as-border)",
            ["--docs-color-border-default-strong"] = "color-mix(in srgb, var(--as-border) 88%, transparent)",
            ["--docs-color-border-default-heavy"] = "color-mix(in srgb, var(--as-border) 90%, transparent)",
            ["--docs-color-border-default-heavier"] = "color-mix(in srgb, var(--as-border) 92%, transparent)",
            ["--docs-color-border-strong"] = "color-mix(in srgb, var(--as-border) 84%, var(--as-text))",
            ["--docs-color-border-accent"] = "color-mix(in srgb, " + accentStrong + " 42%, transparent)",
            ["--docs-color-border-accent-hover"] = "color-mix(in srgb, " + accent + " 56%, transparent)",
            ["--docs-color-border-accent-muted"] = "color-mix(in srgb, " + accentStrong + " 34%, transparent)",
            ["--docs-color-border-accent-active"] = "color-mix(in srgb, " + accent + " 48%, transparent)",
            ["--docs-color-border-accent-subtle"] = "color-mix(in srgb, " + accentStrong + " 22%, transparent)",
            ["--docs-color-border-accent-faint"] = "color-mix(in srgb, " + accentStrong + " 12%, transparent)",
            ["--docs-color-border-accent-strong"] = "color-mix(in srgb, " + accent + " 70%, transparent)",
            ["--docs-color-border-accent-readable"] = "color-mix(in srgb, " + accent + " 62%, transparent)",
            ["--docs-color-text-strong"] = "var(--as-text)",
            ["--docs-color-text-default"] = "var(--as-text)",
            ["--docs-color-text-default-underline"] = "color-mix(in srgb, var(--as-text) 78%, transparent)",
            ["--docs-color-text-muted"] = "var(--as-muted-text)",
            ["--docs-color-text-subtle"] = "color-mix(in srgb, var(--as-muted-text) 84%, transparent)",
            ["--docs-color-text-subtle-underline"] = "color-mix(in srgb, var(--as-muted-text) 62%, transparent)",
            ["--docs-color-text-faint"] = "color-mix(in srgb, var(--as-muted-text) 64%, transparent)",
            ["--docs-color-text-prose"] = "var(--as-text)",
            ["--docs-color-text-table"] = "var(--as-text)",
            ["--docs-color-text-info"] = "var(--as-text)",
            ["--docs-color-text-info-muted"] = "var(--as-muted-text)",
            ["--docs-color-text-mark"] = "var(--as-text)",
            ["--docs-color-accent"] = accent,
            ["--docs-color-accent-strong"] = accentStrong,
            ["--docs-color-accent-blue"] = accentStrong,
            ["--docs-color-accent-violet"] = visitedLink,
            ["--docs-color-accent-soft"] = "color-mix(in srgb, " + accent + " 24%, var(--as-surface))",
            ["--docs-color-accent-muted"] = "color-mix(in srgb, " + accent + " 56%, var(--as-surface))",
            ["--docs-color-accent-glow"] = "color-mix(in srgb, " + accent + " 12%, transparent)",
            ["--docs-color-link"] = link,
            ["--docs-color-link-visited"] = visitedLink,
            ["--docs-color-link-underline"] = "color-mix(in srgb, " + link + " 50%, transparent)",
            ["--docs-color-accent-fill-soft"] = "color-mix(in srgb, " + accentStrong + " 14%, transparent)",
            ["--docs-color-accent-mark-fill"] = "color-mix(in srgb, " + accent + " 28%, transparent)",
            ["--docs-color-accent-underline"] = "color-mix(in srgb, " + accent + " 50%, transparent)",
            ["--docs-color-accent-soft-underline"] = "color-mix(in srgb, " + accent + " 42%, transparent)",
            ["--docs-color-accent-soft-underline-muted"] = "color-mix(in srgb, " + accent + " 32%, transparent)",
            ["--docs-color-state-active-fill"] = "color-mix(in srgb, " + accentStrong + " 24%, transparent)",
            ["--docs-color-state-active-fill-strong"] = "color-mix(in srgb, " + accentStrong + " 34%, transparent)",
            ["--docs-color-state-link-fill"] = "color-mix(in srgb, " + accentStrong + " 28%, transparent)",
            ["--docs-color-state-trust-fill-start"] = "color-mix(in srgb, " + accentStrong + " 18%, transparent)",
            ["--docs-color-state-outline-fill"] = "color-mix(in srgb, " + accentStrong + " 46%, transparent)",
            ["--docs-color-state-outline-fill-end"] = "color-mix(in srgb, var(--as-raised-surface) 30%, transparent)",
            ["--docs-color-state-outline-rail-start"] = "color-mix(in srgb, " + accentStrong + " 50%, transparent)",
            ["--docs-color-state-outline-rail-mid"] = "color-mix(in srgb, " + accent + " 22%, transparent)",
            ["--docs-color-state-outline-rail-end"] = "color-mix(in srgb, var(--as-raised-surface) 18%, transparent)",
            ["--docs-color-state-outline-rail-hover-start"] = "color-mix(in srgb, " + accentStrong + " 60%, transparent)",
            ["--docs-color-state-outline-rail-hover-mid"] = "color-mix(in srgb, " + accent + " 30%, transparent)",
            ["--docs-color-state-outline-rail-hover-end"] = "color-mix(in srgb, var(--as-raised-surface) 24%, transparent)",
            ["--docs-color-page-wash"] = "color-mix(in srgb, " + accentStrong + " 8%, transparent)",
            ["--docs-color-skeleton-edge"] = "color-mix(in srgb, var(--as-border) 70%, transparent)",
            ["--docs-color-skeleton-mid"] = "color-mix(in srgb, var(--as-border) 42%, transparent)",
            ["--docs-color-syntax-keyword"] = visitedLink,
            ["--docs-color-syntax-string"] = accent,
            ["--docs-color-syntax-comment"] = "var(--as-muted-text)",
            ["--docs-color-syntax-number"] = "var(--as-danger)",
            ["--docs-color-syntax-type"] = link,
            ["--docs-color-syntax-member"] = "var(--as-text)",
            ["--docs-color-syntax-parameter"] = accentStrong,
            ["--docs-color-syntax-operator"] = "var(--as-muted-text)",
            ["--docs-color-syntax-inserted"] = accent,
            ["--docs-color-syntax-deleted"] = "var(--as-danger)",
            ["--docs-focus-ring-inset"] = "0 0 0 1px var(--as-focus) inset",
            ["--docs-focus-outline"] = "2px solid var(--as-focus)"
        };

        return "html[data-as-theme]{"
            + SerializeCssVariables(variables)
            + "}\n"
            + BuildForcedColorsCss();
    }

    private static string BuildForcedColorsCss()
    {
        const string selector = "  html[data-as-theme]{";
        var builder = new StringBuilder("@media (forced-colors: active){\n");
        builder.Append(selector);
        AppendForcedColor(builder, "--docs-brand-blue", "Highlight");
        AppendForcedColor(builder, "--docs-brand-teal", "Highlight");
        AppendForcedColor(builder, "--docs-brand-violet", "VisitedText");
        foreach (var surface in new[]
                 {
                     "--docs-color-surface-canvas", "--docs-color-surface-canvas-mid", "--docs-color-surface-canvas-deep",
                     "--docs-color-surface-raised", "--docs-color-surface-muted", "--docs-color-surface-panel",
                     "--docs-color-surface-panel-elevated", "--docs-color-surface-panel-heavy", "--docs-color-surface-panel-hover",
                     "--docs-color-surface-panel-hover-strong", "--docs-color-surface-panel-raised", "--docs-color-surface-panel-active",
                     "--docs-color-surface-panel-soft", "--docs-color-surface-panel-faint", "--docs-color-surface-panel-table",
                     "--docs-color-surface-panel-table-head", "--docs-color-surface-overlay", "--docs-color-surface-overlay-strong",
                     "--docs-color-surface-overlay-soft", "--docs-color-surface-overlay-faint", "--docs-color-surface-code",
                     "--docs-color-surface-code-plain"
                 })
        {
            AppendForcedColor(builder, surface, "Canvas");
        }

        foreach (var border in new[]
                 {
                     "--docs-color-border-muted", "--docs-color-border-muted-heavy", "--docs-color-border-code-plain",
                     "--docs-color-border-default", "--docs-color-border-default-strong", "--docs-color-border-default-heavy",
                     "--docs-color-border-default-heavier", "--docs-color-border-strong", "--docs-color-skeleton-edge",
                     "--docs-color-skeleton-mid"
                 })
        {
            AppendForcedColor(builder, border, "GrayText");
        }

        foreach (var text in new[]
                 {
                     "--docs-color-text-strong", "--docs-color-text-default", "--docs-color-text-default-underline",
                     "--docs-color-text-prose", "--docs-color-text-table", "--docs-color-text-info", "--docs-color-text-mark",
                     "--docs-color-syntax-member", "--docs-color-syntax-number", "--docs-color-syntax-deleted"
                 })
        {
            AppendForcedColor(builder, text, "CanvasText");
        }

        foreach (var muted in new[]
                 {
                     "--docs-color-text-muted", "--docs-color-text-subtle", "--docs-color-text-subtle-underline",
                     "--docs-color-text-faint", "--docs-color-text-info-muted", "--docs-color-syntax-comment",
                     "--docs-color-syntax-operator"
                 })
        {
            AppendForcedColor(builder, muted, "GrayText");
        }

        foreach (var accent in new[]
                 {
                     "--docs-color-border-accent", "--docs-color-border-accent-hover",
                     "--docs-color-border-accent-muted", "--docs-color-border-accent-active",
                     "--docs-color-border-accent-subtle", "--docs-color-border-accent-faint",
                     "--docs-color-border-accent-strong", "--docs-color-border-accent-readable",
                     "--docs-color-accent", "--docs-color-accent-strong", "--docs-color-accent-blue",
                     "--docs-color-accent-soft", "--docs-color-accent-muted", "--docs-color-accent-fill-soft",
                     "--docs-color-accent-mark-fill", "--docs-color-accent-underline", "--docs-color-accent-soft-underline",
                     "--docs-color-accent-soft-underline-muted", "--docs-color-state-active-fill",
                     "--docs-color-state-active-fill-strong", "--docs-color-state-link-fill", "--docs-color-state-trust-fill-start",
                     "--docs-color-state-outline-fill", "--docs-color-state-outline-rail-start",
                     "--docs-color-state-outline-rail-mid", "--docs-color-state-outline-rail-hover-start",
                     "--docs-color-state-outline-rail-hover-mid", "--docs-color-page-wash", "--docs-color-syntax-keyword",
                     "--docs-color-syntax-string", "--docs-color-syntax-parameter", "--docs-color-syntax-inserted",
                     "--docs-color-accent-glow"
                 })
        {
            AppendForcedColor(builder, accent, "Highlight");
        }

        foreach (var link in new[] { "--docs-color-link", "--docs-color-link-underline", "--docs-color-syntax-type" })
        {
            AppendForcedColor(builder, link, "LinkText");
        }

        AppendForcedColor(builder, "--docs-color-link-visited", "VisitedText");
        AppendForcedColor(builder, "--docs-color-accent-violet", "VisitedText");
        AppendForcedColor(builder, "--docs-color-state-outline-fill-end", "Canvas");
        AppendForcedColor(builder, "--docs-color-state-outline-rail-end", "Canvas");
        AppendForcedColor(builder, "--docs-color-state-outline-rail-hover-end", "Canvas");
        AppendForcedColor(builder, "--docs-focus-ring-inset", "0 0 0 1px Highlight inset");
        AppendForcedColor(builder, "--docs-focus-outline", "2px solid Highlight");
        builder.Append("  }\n}");
        return builder.ToString();
    }

    private static void AppendForcedColor(StringBuilder builder, string name, string value)
    {
        builder.Append("\n    ");
        builder.Append(name);
        builder.Append(":");
        builder.Append(value);
        builder.Append(';');
    }

    private static string ResolveSharedOverride(
        string? value,
        string fallback,
        AppSurfaceThemeResolution resolution,
        double requiredContrast)
    {
        if (!AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(value, out var normalized, out _)
            || normalized is null)
        {
            return fallback;
        }

        return IsSharedOverrideSafe(normalized, resolution, requiredContrast)
            ? normalized
            : fallback;
    }

    private static bool IsSharedOverrideSafe(
        string overrideColor,
        AppSurfaceThemeResolution resolution,
        double requiredContrast)
    {
        return resolution.Mode switch
        {
            AppSurfaceThemeMode.Light => HasRequiredContrast(overrideColor, resolution.Light, requiredContrast),
            AppSurfaceThemeMode.Dark => HasRequiredContrast(overrideColor, resolution.Dark, requiredContrast),
            AppSurfaceThemeMode.System => HasRequiredContrast(overrideColor, resolution.Light, requiredContrast)
                                           && HasRequiredContrast(overrideColor, resolution.Dark, requiredContrast),
            _ => false
        };
    }

    private static bool HasRequiredContrast(
        string foreground,
        AppSurfaceThemeRoles roles,
        double requiredContrast)
    {
        return ContrastRatio(foreground, roles.Canvas) >= requiredContrast
               && ContrastRatio(foreground, roles.Surface) >= requiredContrast
               && ContrastRatio(foreground, roles.RaisedSurface) >= requiredContrast;
    }

    private static string ToPresetAttribute(AppSurfaceDocsThemePreset preset)
    {
        return preset switch
        {
            AppSurfaceDocsThemePreset.GraphiteDark => "graphite-dark",
            _ => "appsurface-dark"
        };
    }

    private static string ToDensityAttribute(AppSurfaceDocsThemeDensity density)
    {
        return density == AppSurfaceDocsThemeDensity.Compact ? "compact" : "comfortable";
    }

    private static string ToChromeAttribute(AppSurfaceDocsThemeChrome chrome)
    {
        return chrome == AppSurfaceDocsThemeChrome.Compact ? "compact" : "standard";
    }

    private static string ToRgba(string hexColor, double alpha)
    {
        var color = ParseHexColor(hexColor);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"rgba({color.Red}, {color.Green}, {color.Blue}, {alpha:0.##})");
    }

    private static double ContrastRatio(string foregroundHexColor, string backgroundHexColor)
    {
        var foreground = RelativeLuminance(ParseHexColor(foregroundHexColor));
        var background = RelativeLuminance(ParseHexColor(backgroundHexColor));
        var light = Math.Max(foreground, background);
        var dark = Math.Min(foreground, background);
        return (light + 0.05d) / (dark + 0.05d);
    }

    private static double RelativeLuminance(RgbColor color)
    {
        return (0.2126d * Linearize(color.Red))
               + (0.7152d * Linearize(color.Green))
               + (0.0722d * Linearize(color.Blue));
    }

    private static double Linearize(int channel)
    {
        var value = channel / 255d;
        return value <= 0.04045d
            ? value / 12.92d
            : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
    }

    private static RgbColor ParseHexColor(string hexColor)
    {
        var value = hexColor[0] == '#' ? hexColor[1..] : hexColor;
        if (value.Length == 3)
        {
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        }

        return new RgbColor(
            int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private readonly record struct RgbColor(int Red, int Green, int Blue);
}
