using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web;

internal static partial class PwaOptionsValidator
{
    public static IReadOnlyList<PwaDiagnostic> Validate(PwaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<PwaDiagnostic>();

        if (!options.Enabled)
        {
            return diagnostics;
        }

        RequireText(options.Name, "ASPWA001", "PwaOptions.Name is required when PWA support is enabled.", diagnostics);
        RequireText(options.ShortName, "ASPWA002", "PwaOptions.ShortName is required when PWA support is enabled.", diagnostics);
        RequireHexColor(options.ThemeColor, "ASPWA003", "PwaOptions.ThemeColor must be a CSS hex color such as #2563eb.", diagnostics);
        RequireHexColor(options.BackgroundColor, "ASPWA004", "PwaOptions.BackgroundColor must be a CSS hex color such as #ffffff.", diagnostics);
        RequireLocalPath(options.ManifestPath, "ASPWA005", "PwaOptions.ManifestPath must be an app-root-relative path such as /manifest.webmanifest.", diagnostics);
        RequireLocalStartUrl(options.StartUrl, "ASPWA006", "PwaOptions.StartUrl must be an app-root-relative URL such as /.", diagnostics);
        RequireLocalPath(options.Scope, "ASPWA007", "PwaOptions.Scope must be an app-root-relative URL such as /.", diagnostics);
        RequireLocalPath(options.DiagnosticsPath, "ASPWA008", "PwaOptions.DiagnosticsPath must be an app-root-relative path such as /_appsurface/pwa.", diagnostics);
        RequireStartUrlWithinScope(options.StartUrl, options.Scope, diagnostics);

        if (!Enum.IsDefined(options.Display))
        {
            diagnostics.Add(new PwaDiagnostic("ASPWA009", PwaDiagnosticSeverity.Error, "PwaOptions.Display is not a supported display mode."));
        }

        if (!options.Icons.Any(icon => string.Equals(icon.Sizes, "192x192", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new PwaDiagnostic("ASPWA010", PwaDiagnosticSeverity.Error, "PWA support requires a declared 192x192 icon."));
        }

        if (!options.Icons.Any(icon => string.Equals(icon.Sizes, "512x512", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new PwaDiagnostic("ASPWA011", PwaDiagnosticSeverity.Error, "PWA support requires a declared 512x512 icon."));
        }

        for (var i = 0; i < options.Icons.Count; i++)
        {
            var icon = options.Icons[i];
            RequireLocalPath(icon.Source, "ASPWA012", $"PwaOptions.Icons[{i}].Source must be an app-root-relative path.", diagnostics);

            if (!IconSizePattern().IsMatch(icon.Sizes ?? string.Empty))
            {
                diagnostics.Add(new PwaDiagnostic("ASPWA013", PwaDiagnosticSeverity.Error, $"PwaOptions.Icons[{i}].Sizes must use WIDTHxHEIGHT, for example 192x192."));
            }

            RequireText(icon.Type, "ASPWA014", $"PwaOptions.Icons[{i}].Type is required.", diagnostics);
        }

        if (options.Offline.Enabled)
        {
            RequireLocalPath(options.Offline.ServiceWorkerPath, "ASPWA015", "PwaOptions.Offline.ServiceWorkerPath must be an app-root-relative path.", diagnostics);
            RequireLocalPath(options.Offline.OfflineFallbackPath, "ASPWA016", "PwaOptions.Offline.OfflineFallbackPath is required when offline support is enabled.", diagnostics);

            for (var i = 0; i < options.Offline.StaticAssetPaths.Length; i++)
            {
                RequireLocalPath(options.Offline.StaticAssetPaths[i], "ASPWA017", $"PwaOptions.Offline.StaticAssetPaths[{i}] must be app-root-relative.", diagnostics);
            }
        }

        return diagnostics;
    }

    public static void ThrowIfInvalid(PwaOptions options)
    {
        var errors = Validate(options)
            .Where(diagnostic => diagnostic.Severity == PwaDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "AppSurface PWA configuration is invalid: "
            + string.Join(" ", errors.Select(error => $"{error.Code}: {error.Message}")));
    }

    internal static bool IsSafeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        if (!string.Equals(value, path, StringComparison.Ordinal))
        {
            return false;
        }

        return value.StartsWith('/')
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains('\\')
            && !value.Contains("://", StringComparison.Ordinal)
            && !value.Contains('?')
            && !value.Contains('#')
            && value.All(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch) && ch is not ('{' or '}'))
            && !HasTraversalSegment(value);
    }

    private static bool HasTraversalSegment(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ContainsMalformedEscape(segment)
                || string.Equals(Uri.UnescapeDataString(segment), "..", StringComparison.Ordinal));
    }

    private static bool ContainsMalformedEscape(string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '%')
            {
                continue;
            }

            if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]))
            {
                return true;
            }

            i += 2;
        }

        return false;
    }

    internal static string FormatDisplayMode(PwaDisplayMode displayMode)
    {
        return displayMode switch
        {
            PwaDisplayMode.Browser => "browser",
            PwaDisplayMode.MinimalUi => "minimal-ui",
            PwaDisplayMode.Standalone => "standalone",
            PwaDisplayMode.Fullscreen => "fullscreen",
            _ => displayMode.ToString().ToLowerInvariant()
        };
    }

    private static void RequireText(string? value, string code, string message, List<PwaDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new PwaDiagnostic(code, PwaDiagnosticSeverity.Error, message));
        }
    }

    private static void RequireHexColor(string? value, string code, string message, List<PwaDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || !HexColorPattern().IsMatch(value))
        {
            diagnostics.Add(new PwaDiagnostic(code, PwaDiagnosticSeverity.Error, message));
        }
    }

    private static void RequireLocalPath(string? value, string code, string message, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalPath(value))
        {
            diagnostics.Add(new PwaDiagnostic(code, PwaDiagnosticSeverity.Error, message));
        }
    }

    private static void RequireLocalStartUrl(string? value, string code, string message, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalStartUrl(value))
        {
            diagnostics.Add(new PwaDiagnostic(code, PwaDiagnosticSeverity.Error, message));
        }
    }

    private static bool IsSafeLocalStartUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var startUrl = value.Trim();
        if (!string.Equals(startUrl, value, StringComparison.Ordinal))
        {
            return false;
        }

        if (!startUrl.StartsWith('/')
            || startUrl.StartsWith("//", StringComparison.Ordinal)
            || startUrl.Contains('\\')
            || startUrl.Contains("://", StringComparison.Ordinal)
            || startUrl.Contains('#')
            || startUrl.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '{' or '}'))
        {
            return false;
        }

        var queryStart = startUrl.IndexOf('?');
        var path = queryStart < 0 ? startUrl : startUrl[..queryStart];
        return !HasTraversalSegment(path);
    }

    private static void RequireStartUrlWithinScope(string? startUrl, string? scope, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalStartUrl(startUrl) || !IsSafeLocalPath(scope))
        {
            return;
        }

        if (!IsPathWithinScope(GetPathWithoutQuery(startUrl!.Trim()), scope!.Trim()))
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA019",
                    PwaDiagnosticSeverity.Error,
                    "PwaOptions.StartUrl must stay within PwaOptions.Scope."));
        }
    }

    private static string GetPathWithoutQuery(string value)
    {
        var queryStart = value.IndexOf('?');
        return queryStart < 0 ? value : value[..queryStart];
    }

    private static bool IsPathWithinScope(string path, string scope)
    {
        if (scope == "/")
        {
            return true;
        }

        if (scope.EndsWith("/", StringComparison.Ordinal))
        {
            return path.StartsWith(scope, StringComparison.Ordinal);
        }

        return string.Equals(path, scope, StringComparison.Ordinal)
            || path.StartsWith(scope + "/", StringComparison.Ordinal);
    }

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex("^[1-9][0-9]*x[1-9][0-9]*$", RegexOptions.IgnoreCase)]
    private static partial Regex IconSizePattern();
}

internal sealed record PwaDiagnostic(string Code, PwaDiagnosticSeverity Severity, string Message);

internal enum PwaDiagnosticSeverity
{
    Info,
    Warning,
    Error
}
