using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

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
    {
        ArgumentNullException.ThrowIfNull(options);

        Theme = AppSurfaceDocsThemePolicy.Resolve(options.Theme);
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
internal sealed record AppSurfaceDocsResolvedTheme(
    AppSurfaceDocsThemePreset Preset,
    AppSurfaceDocsThemeDensity Density,
    AppSurfaceDocsThemeChrome Chrome,
    string PresetAttribute,
    string DensityAttribute,
    string ChromeAttribute,
    string RootCssClass,
    IReadOnlyDictionary<string, string> CssVariables,
    string CssVariableStyle);

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
        foreach (var (key, value) in variables)
        {
            builder.Append(key);
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }

        return builder.ToString();
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
