using ForgeTrust.AppSurface.Auth;

namespace AuthWebRazorWireProofExample.Models;

/// <summary>
/// Canonical auth proof state shared by the Minimal API endpoint, Razor view, README, and tests.
/// </summary>
/// <param name="Surface">
/// Human-readable proof surface name, such as <c>Minimal API</c> or <c>RazorWire-facing state</c>.
/// The value must identify where the same host-policy result is being rendered.
/// </param>
/// <param name="Policy">
/// The ASP.NET Core policy name evaluated through AppSurface. This sample always uses
/// <see cref="AuthProofPolicy.Name"/>.
/// </param>
/// <param name="Outcome">
/// The AppSurface auth outcome as a stable string, for example <c>Allowed</c>, <c>Challenge</c>, or
/// <c>Forbid</c>.
/// </param>
/// <param name="Reason">
/// The AppSurface auth reason as a stable string, for example <c>None</c>, <c>Unauthenticated</c>, or
/// <c>Forbidden</c>.
/// </param>
/// <param name="UiState">
/// Selector-friendly page-state token derived from <paramref name="Outcome"/>. Expected sample values are
/// <c>allowed</c>, <c>anonymous</c>, <c>forbidden</c>, or <c>setup-failure</c>.
/// </param>
/// <param name="Subject">
/// The mapped subject identifier from the evaluated request, or <see langword="null"/> when the request is
/// anonymous or the auth result did not include a subject.
/// </param>
/// <param name="StatusCode">
/// The HTTP status code rendered by the Minimal API proof endpoint for the same outcome.
/// </param>
public sealed record AuthProofState(
    string Surface,
    string Policy,
    string Outcome,
    string Reason,
    string UiState,
    string? Subject,
    int StatusCode)
{
    /// <summary>
    /// Creates a canonical proof state from an AppSurface authorization result.
    /// </summary>
    /// <param name="surface">
    /// Non-empty proof surface label. The label is display and test metadata; it does not affect
    /// authorization.
    /// </param>
    /// <param name="result">
    /// The evaluated AppSurface auth result for the host-owned policy.
    /// </param>
    /// <returns>
    /// A state object with mapped policy, outcome, reason, subject, UI token, and HTTP status fields.
    /// </returns>
    /// <remarks>
    /// Allowed maps to HTTP 200 and <c>allowed</c>; challenge maps to HTTP 401 and
    /// <c>anonymous</c>; forbid maps to HTTP 403 and <c>forbidden</c>. Other outcomes are treated as
    /// setup failures for this sample and map to HTTP 500 plus <c>setup-failure</c>, which keeps unexpected
    /// configuration problems distinct from denied users.
    /// </remarks>
    public static AuthProofState FromResult(string surface, AppSurfaceAuthResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentNullException.ThrowIfNull(result);

        return new AuthProofState(
            surface,
            AuthProofPolicy.Name,
            result.Outcome.ToString(),
            result.Reason.ToString(),
            MapUiState(result.Outcome),
            result.Context?.User?.Id,
            MapStatusCode(result.Outcome));
    }

    private static int MapStatusCode(AppSurfaceAuthOutcome outcome)
    {
        return outcome switch
        {
            AppSurfaceAuthOutcome.Allowed => StatusCodes.Status200OK,
            AppSurfaceAuthOutcome.Challenge => StatusCodes.Status401Unauthorized,
            AppSurfaceAuthOutcome.Forbid => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    private static string MapUiState(AppSurfaceAuthOutcome outcome)
    {
        return outcome switch
        {
            AppSurfaceAuthOutcome.Allowed => "allowed",
            AppSurfaceAuthOutcome.Challenge => "anonymous",
            AppSurfaceAuthOutcome.Forbid => "forbidden",
            _ => "setup-failure",
        };
    }
}
