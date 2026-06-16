using ForgeTrust.AppSurface.Auth;

namespace AuthWebRazorWireProofExample.Models;

/// <summary>
/// Canonical auth proof state shared by the Minimal API endpoint, Razor view, README, and tests.
/// </summary>
public sealed record AuthProofState(
    string Surface,
    string Policy,
    string Outcome,
    string Reason,
    string UiState,
    string? Subject,
    int StatusCode)
{
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
            AppSurfaceAuthOutcome.Challenge => "unauthenticated",
            AppSurfaceAuthOutcome.Forbid => "forbidden",
            _ => "setup failure",
        };
    }
}
