namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Validates and resolves DevAuth-local navigation targets.
/// </summary>
/// <remarks>
/// Request-supplied targets fail closed by being omitted, while configured persona landing URLs fail fast during
/// registration. The two policies share the same local-path rules but intentionally have different failure behavior.
/// </remarks>
internal static class AppSurfaceDevAuthLocalReturnUrl
{
    /// <summary>
    /// Gets a value indicating whether a candidate is a safe rooted local path.
    /// </summary>
    internal static bool IsSafe(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith("/", StringComparison.Ordinal) &&
            !value.StartsWith("//", StringComparison.Ordinal) &&
            !value.StartsWith("/\\", StringComparison.Ordinal) &&
            !value.Contains('\\', StringComparison.Ordinal) &&
            !value.Any(char.IsControl);
    }

    /// <summary>
    /// Returns the safe candidate or <see langword="null"/> when a request-supplied target is unusable.
    /// </summary>
    internal static string? GetSafeOrNull(string? value)
    {
        return IsSafe(value) ? value : null;
    }

    /// <summary>
    /// Returns a safe marker target, falling back to the site root for the legacy marker-option contract.
    /// </summary>
    internal static string NormalizeOrRoot(string? value)
    {
        return GetSafeOrNull(value) ?? "/";
    }

    /// <summary>
    /// Validates a configured persona landing URL.
    /// </summary>
    /// <exception cref="AppSurfaceDevAuthException">
    /// Thrown with <c>ASDEV007</c> when the configured value is not a safe rooted local path.
    /// </exception>
    internal static string ValidateConfigured(string? value)
    {
        var safeValue = GetSafeOrNull(value);
        if (safeValue is not null)
        {
            return safeValue;
        }

        throw new AppSurfaceDevAuthException(
            AppSurfaceDevAuthDiagnostics.InvalidPersonaLandingUrl,
            "ASDEV007 Problem: DevAuth persona landing URLs must be safe rooted local paths. Cause: a configured landing URL was blank, non-rooted, absolute, protocol-relative, contained a backslash, or included a control character. Fix: use a local path such as '/dashboard' or '/viewer?tab=proof'. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
    }

    /// <summary>
    /// Resolves a select-persona navigation target in precedence order.
    /// </summary>
    /// <param name="explicitTarget">Safe caller-supplied target, when the host intentionally owns the switch flow.</param>
    /// <param name="personaLandingUrl">Configured safe landing URL for the selected persona.</param>
    /// <param name="fallbackTarget">Source-specific fallback, such as the marker's current request URL.</param>
    /// <returns>The first safe target in precedence order, or <see langword="null"/> when selection should render the control response.</returns>
    internal static string? ResolveSelectTarget(
        string? explicitTarget,
        string? personaLandingUrl,
        string? fallbackTarget)
    {
        return GetSafeOrNull(explicitTarget) ??
            GetSafeOrNull(personaLandingUrl) ??
            GetSafeOrNull(fallbackTarget);
    }
}
