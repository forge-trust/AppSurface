namespace ForgeTrust.AppSurface.Web;

internal static class HealthOptionsValidator
{
    public static IReadOnlyList<HealthDiagnostic> Validate(HealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<HealthDiagnostic>();
        if (!options.Enabled)
        {
            return diagnostics;
        }

        RequireLocalPath(
            options.HealthPath,
            "ASPHEALTH001",
            "HealthOptions.HealthPath must be an app-root-relative path such as /health.",
            diagnostics);
        RequireLocalPath(
            options.ReadyPath,
            "ASPHEALTH002",
            "HealthOptions.ReadyPath must be an app-root-relative path such as /ready.",
            diagnostics);

        if (PwaOptionsValidator.IsSafeLocalPath(options.HealthPath)
            && PwaOptionsValidator.IsSafeLocalPath(options.ReadyPath)
            && string.Equals(
                NormalizeRoutePattern(options.HealthPath),
                NormalizeRoutePattern(options.ReadyPath),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                new HealthDiagnostic(
                    "ASPHEALTH003",
                    "HealthOptions.HealthPath and HealthOptions.ReadyPath must be distinct endpoint paths."));
        }

        if (string.IsNullOrWhiteSpace(options.ReadyTag))
        {
            diagnostics.Add(
                new HealthDiagnostic(
                    "ASPHEALTH004",
                    "HealthOptions.ReadyTag is required when health endpoints are enabled."));
        }

        return diagnostics;
    }

    public static void ThrowIfInvalid(HealthOptions options)
    {
        var errors = Validate(options);
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "AppSurface health endpoint configuration is invalid: "
            + string.Join(" ", errors.Select(error => $"{error.Code}: {error.Message}")));
    }

    internal static string NormalizeRoutePattern(string value)
    {
        var normalized = value.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static void RequireLocalPath(
        string? value,
        string code,
        string message,
        List<HealthDiagnostic> diagnostics)
    {
        if (!PwaOptionsValidator.IsSafeLocalPath(value))
        {
            diagnostics.Add(new HealthDiagnostic(code, message));
        }
    }
}

internal sealed record HealthDiagnostic(string Code, string Message);
