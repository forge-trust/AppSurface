using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web;

internal static partial class PwaOptionsValidator
{
    public static IReadOnlyList<PwaDiagnostic> Validate(PwaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<PwaDiagnostic>();

        if (options.Worker.HasServiceWorkerPathConflict)
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA020",
                    PwaDiagnosticSeverity.Error,
                    "PwaOptions.Worker.ServiceWorkerPath and the Offline.ServiceWorkerPath compatibility alias cannot be configured with different values."));
        }

        if (!options.HasAnySurfaceEnabled)
        {
            return diagnostics;
        }

        if (options.Enabled)
        {
            RequireText(options.Name, "ASPWA001", "PwaOptions.Name is required when install metadata is enabled.", diagnostics);
            RequireText(options.ShortName, "ASPWA002", "PwaOptions.ShortName is required when install metadata is enabled.", diagnostics);
            RequireHexColor(options.ThemeColor, "ASPWA003", "PwaOptions.ThemeColor must be a CSS hex color such as #2563eb.", diagnostics);
            RequireHexColor(options.BackgroundColor, "ASPWA004", "PwaOptions.BackgroundColor must be a CSS hex color such as #ffffff.", diagnostics);
            RequireLocalStartUrl(options.StartUrl, "ASPWA006", "PwaOptions.StartUrl must be an app-root-relative URL such as /.", diagnostics);
            RequireStartUrlWithinScope(options.StartUrl, options.Scope, diagnostics);

            if (!Enum.IsDefined(options.Display))
            {
                diagnostics.Add(new PwaDiagnostic("ASPWA009", PwaDiagnosticSeverity.Error, "PwaOptions.Display is not a supported display mode."));
            }

            if (!options.Icons.Any(icon => HasIconSizeToken(icon.Sizes, "192x192")))
            {
                diagnostics.Add(new PwaDiagnostic("ASPWA010", PwaDiagnosticSeverity.Error, "PWA install metadata requires a declared 192x192 icon."));
            }

            if (!options.Icons.Any(icon => HasIconSizeToken(icon.Sizes, "512x512")))
            {
                diagnostics.Add(new PwaDiagnostic("ASPWA011", PwaDiagnosticSeverity.Error, "PWA install metadata requires a declared 512x512 icon."));
            }

            for (var i = 0; i < options.Icons.Count; i++)
            {
                var icon = options.Icons[i];
                RequireLocalPath(icon.Source, "ASPWA012", $"PwaOptions.Icons[{i}].Source must be an app-root-relative path.", diagnostics);

                if (!HasValidIconSizeTokens(icon.Sizes))
                {
                    diagnostics.Add(new PwaDiagnostic("ASPWA013", PwaDiagnosticSeverity.Error, $"PwaOptions.Icons[{i}].Sizes must use WIDTHxHEIGHT tokens, for example 192x192 or 192x192 512x512."));
                }

                RequireText(icon.Type, "ASPWA014", $"PwaOptions.Icons[{i}].Type is required.", diagnostics);
            }
        }

        RequireEndpointPath(options.ManifestPath, "ASPWA005", "PwaOptions.ManifestPath must be an app-root-relative endpoint path without percent escapes.", diagnostics);
        RequireScope(options.Scope, options.IsWorkerEnabled, diagnostics);
        RequireEndpointPath(options.DiagnosticsPath, "ASPWA008", "PwaOptions.DiagnosticsPath must be an app-root-relative endpoint path without percent escapes.", diagnostics);
        RequireEndpointPath(options.Worker.ServiceWorkerPath, "ASPWA015", "PwaOptions.Worker.ServiceWorkerPath must be an app-root-relative endpoint path without percent escapes.", diagnostics);

        if (options.Offline.Enabled)
        {
            RequireLocalPath(options.Offline.OfflineFallbackPath, "ASPWA016", "PwaOptions.Offline.OfflineFallbackPath is required when offline support is enabled.", diagnostics);

            for (var i = 0; i < options.Offline.StaticAssetPaths.Length; i++)
            {
                RequireLocalPath(options.Offline.StaticAssetPaths[i], "ASPWA017", $"PwaOptions.Offline.StaticAssetPaths[{i}] must be app-root-relative.", diagnostics);
            }
        }

        if (options.Push.Enabled)
        {
            RequireEndpointPath(options.Worker.RegistrationHelperPath, "ASPWA021", "PwaOptions.Worker.RegistrationHelperPath must be an app-root-relative endpoint path without percent escapes.", diagnostics);
            if (options.Push.HandlerScriptPath is not null)
            {
                RequireLocalPath(options.Push.HandlerScriptPath, "ASPWA022", "PwaOptions.Push.HandlerScriptPath must be an app-root-relative path.", diagnostics);
            }
        }
        if (options.Badging.Enabled)
        {
            RequireEndpointPath(options.Badging.HelperPath, "ASPWA027", "PwaOptions.Badging.HelperPath must be an app-root-relative endpoint path without percent escapes.", diagnostics);
        }

        ValidateKnownRouteCollisions(options, diagnostics);

        return diagnostics;
    }

    private static void ValidateKnownRouteCollisions(PwaOptions options, List<PwaDiagnostic> diagnostics)
    {
        var routes = new List<(string Name, string Path)>();
        if (options.Enabled)
        {
            routes.Add(("manifest", options.ManifestPath));
        }

        if (options.DiagnosticsExposure != PwaDiagnosticEndpointExposure.Never)
        {
            routes.Add(("diagnostics", options.DiagnosticsPath));
            if (!string.IsNullOrWhiteSpace(options.DiagnosticsPath))
            {
                routes.Add(("diagnostics status", $"{options.DiagnosticsPath.TrimEnd('/')}/status.json"));
            }
        }

        if (options.IsWorkerEnabled)
        {
            routes.Add(("service worker", options.Worker.ServiceWorkerPath));
        }

        if (options.Push.Enabled)
        {
            routes.Add(("registration helper", options.Worker.RegistrationHelperPath));
            if (options.Push.HandlerScriptPath is not null)
            {
                routes.Add(("custom push handler", options.Push.HandlerScriptPath));
            }
        }
        if (options.Badging.Enabled)
        {
            routes.Add(("badging helper", options.Badging.HelperPath));
        }

        if (options.Offline.Enabled)
        {
            routes.Add(("offline fallback", options.Offline.OfflineFallbackPath));
        }

        for (var i = 0; i < routes.Count; i++)
        {
            if (!IsSafeLocalPath(routes[i].Path))
            {
                continue;
            }

            for (var j = i + 1; j < routes.Count; j++)
            {
                if (!IsSafeLocalPath(routes[j].Path)
                    || !string.Equals(NormalizeRouteIdentity(routes[i].Path), NormalizeRouteIdentity(routes[j].Path), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                diagnostics.Add(
                    new PwaDiagnostic(
                        "ASPWA023",
                        PwaDiagnosticSeverity.Error,
                        $"The AppSurface PWA {routes[i].Name} and {routes[j].Name} routes must use different paths."));
            }
        }
    }

    private static string NormalizeRouteIdentity(string path)
    {
        var normalized = Uri.UnescapeDataString(path);
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
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
        var decoded = path;
        while (true)
        {
            if (ContainsMalformedEscape(decoded))
            {
                return true;
            }

            var next = Uri.UnescapeDataString(decoded);
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
        }

        return decoded.StartsWith("//", StringComparison.Ordinal)
            || decoded.Contains('\\')
            || decoded.Contains('?')
            || decoded.Contains('#')
            || decoded.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '{' or '}')
            || decoded.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..");
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

    private static void RequireScope(string? value, bool workerEnabled, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalPath(value) || (workerEnabled && value!.Contains('%')))
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA007",
                    PwaDiagnosticSeverity.Error,
                    "PwaOptions.Scope must be an app-root-relative URL such as / and cannot contain percent escapes when a worker capability is enabled."));
        }
    }

    private static void RequireEndpointPath(string? value, string code, string message, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalPath(value) || value!.Contains('%'))
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

    internal static bool IsSafeLocalStartUrl(string? value)
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
        if (queryStart >= 0 && startUrl.IndexOf('?', queryStart + 1) >= 0)
        {
            return false;
        }

        var path = queryStart < 0 ? startUrl : startUrl[..queryStart];
        return !HasTraversalSegment(path);
    }

    private static void RequireStartUrlWithinScope(string? startUrl, string? scope, List<PwaDiagnostic> diagnostics)
    {
        if (!IsSafeLocalStartUrl(startUrl) || !IsSafeLocalPath(scope))
        {
            return;
        }

        if (!PwaScopePathMatcher.IsPathWithinScope(GetPathWithoutQuery(startUrl!), scope!))
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

    private static bool HasIconSizeToken(string? sizes, string expected)
    {
        return GetIconSizeTokens(sizes).Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasValidIconSizeTokens(string? sizes)
    {
        var tokens = GetIconSizeTokens(sizes);
        return tokens.Count > 0 && tokens.All(token => IconSizePattern().IsMatch(token));
    }

    private static IReadOnlyList<string> GetIconSizeTokens(string? sizes)
    {
        return string.IsNullOrWhiteSpace(sizes)
            ? []
            : sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
