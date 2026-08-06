using System.Text;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>
/// Serializes validated neutral theme resolutions into deterministic Web document fragments.
/// </summary>
public static partial class AppSurfaceThemeDocumentSerializer
{
    private const string StyleOpenTag = "<style data-as-theme-critical>";
    private const string StyleCloseTag = "</style>";

    /// <summary>
    /// Creates a deterministic, nonce-free document for a neutral theme resolution.
    /// </summary>
    /// <param name="resolution">The sealed neutral theme resolution.</param>
    /// <returns>A renderable document, or <see cref="AppSurfaceThemeDocument.Empty"/> when the snapshot is unsafe.</returns>
    public static AppSurfaceThemeDocument Serialize(AppSurfaceThemeResolution resolution)
    {
        if (!TrySerialize(resolution, out var document))
        {
            return AppSurfaceThemeDocument.Empty;
        }

        return document;
    }

    /// <summary>
    /// Creates the System-first document used by the browser-local preference enhancement.
    /// </summary>
    /// <param name="resolution">The configured theme-pair resolution whose Light and Dark branches are emitted.</param>
    /// <returns>
    /// A document with System, Light, and Dark selectors, or <see cref="AppSurfaceThemeDocument.Empty"/> when the
    /// resolution is unsafe to render.
    /// </returns>
    /// <remarks>
    /// This method deliberately replaces the configured startup mode with System while retaining the same Light and
    /// Dark pair. The browser bootstrap can then select an explicit branch without changing the URL or duplicating
    /// the HTML document. Safety validation is repeated before serialization so an invalid resolution fails closed
    /// instead of emitting partially trusted markup.
    /// </remarks>
    internal static AppSurfaceThemeDocument SerializePreference(AppSurfaceThemeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var systemResolution = new AppSurfaceThemeResolution(
            resolution.Id,
            AppSurfaceThemeMode.System,
            resolution.Light,
            resolution.Dark);
        if (!AppSurfaceThemeRegistry.IsSafeResolution(systemResolution))
        {
            return AppSurfaceThemeDocument.Empty;
        }

        var rootAttributes =
            $"data-as-theme=\"{HtmlEncoder.Default.Encode(systemResolution.Id.Value)}\" data-as-theme-mode=\"system\" data-as-theme-schema=\"{AppSurfaceThemeDocument.SchemaVersion}\"";
        return new AppSurfaceThemeDocument(
            systemResolution.Id.Value,
            "system",
            rootAttributes,
            "color-scheme: light dark;",
            BuildHeadContent(systemResolution, "light dark", preferenceModes: true));
    }

    /// <summary>
    /// Attempts to create a deterministic document from a neutral theme resolution.
    /// </summary>
    /// <param name="resolution">The sealed neutral theme resolution.</param>
    /// <param name="document">The renderable document when the snapshot is safe.</param>
    /// <returns><see langword="true"/> when all values are safe to emit; otherwise <see langword="false"/>.</returns>
    public static bool TrySerialize(
        AppSurfaceThemeResolution? resolution,
        out AppSurfaceThemeDocument document)
    {
        document = AppSurfaceThemeDocument.Empty;
        if (!AppSurfaceThemeRegistry.IsSafeResolution(resolution))
        {
            return false;
        }

        var safeResolution = resolution!;
        var modeText = GetModeText(safeResolution.Mode);
        var colorScheme = GetColorScheme(safeResolution.Mode);
        var rootAttributes =
            $"data-as-theme=\"{HtmlEncoder.Default.Encode(safeResolution.Id.Value)}\" data-as-theme-mode=\"{modeText}\" data-as-theme-schema=\"{AppSurfaceThemeDocument.SchemaVersion}\"";
        var rootStyle = $"color-scheme: {colorScheme};";
        var headContent = BuildHeadContent(safeResolution, colorScheme);

        document = new AppSurfaceThemeDocument(
            safeResolution.Id.Value,
            modeText,
            rootAttributes,
            rootStyle,
            headContent);
        return true;
    }

    /// <summary>
    /// Adds an encoded nonce to the live inline style in a previously serialized head fragment.
    /// </summary>
    /// <param name="document">The nonce-free document.</param>
    /// <param name="nonce">The CSP nonce to apply to the inline style, if present.</param>
    /// <returns>Head content with the nonce applied only to the inline style element.</returns>
    public static string SerializeHeadContent(AppSurfaceThemeDocument document, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsRenderable || string.IsNullOrEmpty(nonce))
        {
            return document.HeadContent;
        }

        var encodedNonce = HtmlEncoder.Default.Encode(nonce);
        var styleOpenIndex = document.HeadContent.IndexOf(StyleOpenTag, StringComparison.Ordinal);
        if (styleOpenIndex < 0)
        {
            return document.HeadContent;
        }

        var insertionIndex = styleOpenIndex + StyleOpenTag.Length - 1;
        return new StringBuilder(document.HeadContent.Length + encodedNonce.Length + 9)
            .Append(document.HeadContent, 0, insertionIndex)
            .Append(" nonce=\"")
            .Append(encodedNonce)
            .Append('"')
            .Append(document.HeadContent, insertionIndex, document.HeadContent.Length - insertionIndex)
            .ToString();
    }

    private static string BuildHeadContent(
        AppSurfaceThemeResolution resolution,
        string colorScheme,
        bool preferenceModes = false)
    {
        var builder = new StringBuilder();
        builder.Append("<meta name=\"color-scheme\" content=\"");
        builder.Append(colorScheme);
        builder.Append("\" />\n");
        builder.Append(StyleOpenTag);
        builder.Append("\n");
        AppendCriticalCss(builder, resolution, preferenceModes);
        builder.Append(StyleCloseTag);
        return builder.ToString();
    }

    private static void AppendCriticalCss(
        StringBuilder builder,
        AppSurfaceThemeResolution resolution,
        bool preferenceModes)
    {
        var selector = $"[data-as-theme=\"{resolution.Id.Value}\"]";
        if (preferenceModes)
        {
            var systemSelector = selector + "[data-as-theme-mode=\"system\"]";
            var lightSelector = selector + "[data-as-theme-mode=\"light\"]";
            var darkSelector = selector + "[data-as-theme-mode=\"dark\"]";
            AppendBranch(builder, systemSelector + ",\n" + lightSelector, resolution.Light);
            AppendColorScheme(builder, lightSelector, "light");
            builder.Append("@media (prefers-color-scheme: dark) {\n");
            AppendBranch(builder, systemSelector, resolution.Dark, indent: "  ");
            builder.Append("}\n");
            AppendBranch(builder, darkSelector, resolution.Dark);
            AppendColorScheme(builder, darkSelector, "dark");
        }
        else if (resolution.Mode == AppSurfaceThemeMode.Dark)
        {
            AppendBranch(builder, selector, resolution.Dark);
        }
        else
        {
            AppendBranch(builder, selector, resolution.Light);
            if (resolution.Mode == AppSurfaceThemeMode.System)
            {
                builder.Append("@media (prefers-color-scheme: dark) {\n");
                AppendBranch(builder, selector, resolution.Dark, indent: "  ");
                builder.Append("}\n");
            }
        }

        builder.Append("[data-as-theme] [data-rw-form-error-generated=\"true\"] {\n");
        builder.Append("  border: 1px solid var(--rw-form-error-border, var(--as-danger));\n");
        builder.Append("  background-color: var(--rw-form-error-bg, var(--as-raised-surface));\n");
        builder.Append("  color: var(--rw-form-error-text, var(--as-text));\n");
        builder.Append("  --rw-form-error-title: var(--as-text);\n");
        builder.Append("  border-radius: var(--rw-form-error-radius, 0.25rem);\n");
        builder.Append("  padding: var(--rw-form-error-spacing, 1rem);\n");
        builder.Append("}\n");
        builder.Append("[data-as-theme] [data-rw-form-error-generated=\"true\"]:focus-visible,\n");
        builder.Append("[data-as-theme] [data-rw-form-error-generated=\"true\"]:focus-within {\n");
        builder.Append("  outline: 2px solid var(--rw-form-error-focus, var(--as-focus));\n");
        builder.Append("  outline-offset: 2px;\n");
        builder.Append("}\n");
        builder.Append("@media (forced-colors: active) {\n");
        builder.Append("  [data-as-theme] {\n");
        builder.Append("    --as-canvas: Canvas;\n");
        builder.Append("    --as-surface: Canvas;\n");
        builder.Append("    --as-raised-surface: Canvas;\n");
        builder.Append("    --as-text: CanvasText;\n");
        builder.Append("    --as-muted-text: GrayText;\n");
        builder.Append("    --as-border: GrayText;\n");
        builder.Append("    --as-accent: Highlight;\n");
        builder.Append("    --as-accent-strong: Highlight;\n");
        builder.Append("    --as-link: LinkText;\n");
        builder.Append("    --as-visited-link: VisitedText;\n");
        builder.Append("    --as-danger: CanvasText;\n");
        builder.Append("    --as-focus: Highlight;\n");
        builder.Append("  }\n");
        builder.Append("  [data-as-theme] [data-rw-form-error-generated=\"true\"] {\n");
        builder.Append("    border-color: CanvasText;\n");
        builder.Append("    background-color: Canvas;\n");
        builder.Append("    color: CanvasText;\n");
        builder.Append("    --rw-form-error-title: CanvasText;\n");
        builder.Append("  }\n");
        builder.Append("  [data-as-theme] [data-rw-form-error-generated=\"true\"]:focus-visible,\n");
        builder.Append("  [data-as-theme] [data-rw-form-error-generated=\"true\"]:focus-within {\n");
        builder.Append("    outline-color: Highlight;\n");
        builder.Append("  }\n");
        builder.Append("}\n");
    }

    private static void AppendBranch(
        StringBuilder builder,
        string selector,
        AppSurfaceThemeRoles roles,
        string indent = "")
    {
        builder.Append(indent);
        builder.Append(selector);
        builder.Append(" {\n");
        AppendToken(builder, indent, "--as-canvas", roles.Canvas);
        AppendToken(builder, indent, "--as-surface", roles.Surface);
        AppendToken(builder, indent, "--as-raised-surface", roles.RaisedSurface);
        AppendToken(builder, indent, "--as-text", roles.Text);
        AppendToken(builder, indent, "--as-muted-text", roles.MutedText);
        AppendToken(builder, indent, "--as-border", roles.Border);
        AppendToken(builder, indent, "--as-accent", roles.Accent);
        AppendToken(builder, indent, "--as-accent-strong", roles.AccentStrong);
        AppendToken(builder, indent, "--as-link", roles.Link);
        AppendToken(builder, indent, "--as-visited-link", roles.VisitedLink);
        AppendToken(builder, indent, "--as-danger", roles.Danger);
        AppendToken(builder, indent, "--as-focus", roles.Focus);
        builder.Append(indent);
        builder.Append("  color: var(--as-text);\n");
        builder.Append(indent);
        builder.Append("  background-color: var(--as-canvas);\n");
        builder.Append(indent);
        builder.Append("}\n");
    }

    private static void AppendColorScheme(StringBuilder builder, string selector, string colorScheme)
    {
        builder.Append(selector);
        builder.Append(" {\n");
        builder.Append("  color-scheme: ");
        builder.Append(colorScheme);
        builder.Append(" !important;\n");
        builder.Append("}\n");
    }

    private static void AppendToken(
        StringBuilder builder,
        string indent,
        string name,
        string value)
    {
        builder.Append(indent);
        builder.Append("  ");
        builder.Append(name);
        builder.Append(": ");
        builder.Append(value);
        builder.Append(";\n");
    }

    private static string GetModeText(AppSurfaceThemeMode mode) =>
        mode switch
        {
            AppSurfaceThemeMode.System => "system",
            AppSurfaceThemeMode.Light => "light",
            AppSurfaceThemeMode.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static string GetColorScheme(AppSurfaceThemeMode mode) =>
        mode switch
        {
            AppSurfaceThemeMode.System => "light dark",
            AppSurfaceThemeMode.Light => "light",
            AppSurfaceThemeMode.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
}
