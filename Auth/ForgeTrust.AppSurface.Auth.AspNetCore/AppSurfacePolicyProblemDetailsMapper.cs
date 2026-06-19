using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Maps AppSurface auth failures to API-safe ProblemDetails responses.
/// </summary>
internal static class AppSurfacePolicyProblemDetailsMapper
{
    private const string OutcomeExtension = "appsurfaceAuthOutcome";
    private const string ReasonExtension = "appsurfaceAuthReason";
    private const string PolicyNameExtension = "appsurfacePolicyName";
    private static readonly IReadOnlyDictionary<AppSurfaceAuthOutcome, int> StatusCodesByOutcome =
        new Dictionary<AppSurfaceAuthOutcome, int>
        {
            [AppSurfaceAuthOutcome.Challenge] = StatusCodes.Status401Unauthorized,
            [AppSurfaceAuthOutcome.Forbid] = StatusCodes.Status403Forbidden,
            [AppSurfaceAuthOutcome.SetupFailure] = StatusCodes.Status500InternalServerError,
            [AppSurfaceAuthOutcome.UnsafeNavigation] = StatusCodes.Status400BadRequest,
            [AppSurfaceAuthOutcome.StaleOrUnknownSession] = StatusCodes.Status401Unauthorized,
        };

    private static readonly IReadOnlyDictionary<AppSurfaceAuthOutcome, string> TitlesByOutcome =
        new Dictionary<AppSurfaceAuthOutcome, string>
        {
            [AppSurfaceAuthOutcome.Challenge] = "Authentication required",
            [AppSurfaceAuthOutcome.Forbid] = "Authorization failed",
            [AppSurfaceAuthOutcome.SetupFailure] = "AppSurface auth setup failure",
            [AppSurfaceAuthOutcome.UnsafeNavigation] = "Unsafe auth navigation",
            [AppSurfaceAuthOutcome.StaleOrUnknownSession] = "Stale or unknown auth session",
        };

    /// <summary>
    /// Converts a non-allowed AppSurface auth result into a ProblemDetails result.
    /// </summary>
    /// <param name="result">The auth failure to render.</param>
    /// <param name="policyName">The policy name being evaluated for the endpoint.</param>
    /// <returns>An ASP.NET Core result that writes ProblemDetails JSON.</returns>
    public static IResult ToResult(AppSurfaceAuthResult result, string policyName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (result.IsAllowed)
        {
            throw new ArgumentException("Allowed AppSurface auth results do not map to a ProblemDetails response.", nameof(result));
        }

        var problem = new ProblemDetails
        {
            Status = GetStatusCode(result),
            Title = GetTitle(result),
            Detail = result.Message,
        };
        problem.Extensions[OutcomeExtension] = result.Outcome.ToString();
        problem.Extensions[ReasonExtension] = result.Reason.ToString();
        problem.Extensions[PolicyNameExtension] = policyName;

        CopySafeMetadata(result, problem);

        return Results.Json(problem, statusCode: problem.Status, contentType: "application/problem+json");
    }

    private static int GetStatusCode(AppSurfaceAuthResult result)
    {
        return StatusCodesByOutcome[result.Outcome];
    }

    private static string GetTitle(AppSurfaceAuthResult result)
    {
        return TitlesByOutcome[result.Outcome];
    }

    private static void CopySafeMetadata(AppSurfaceAuthResult result, ProblemDetails problem)
    {
        CopyIfPresent(result, problem, AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode);
        CopyIfPresent(result, problem, AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName);
        CopyIfPresent(result, problem, AppSurfaceAspNetCoreAuthMetadataKeys.MissingService);
        CopyIfPresent(result, problem, AppSurfaceAspNetCoreAuthMetadataKeys.SubjectClaimTypes);
    }

    private static void CopyIfPresent(AppSurfaceAuthResult result, ProblemDetails problem, string key)
    {
        if (result.Metadata.TryGetValue(key, out var value))
        {
            problem.Extensions[key] = value;
        }
    }
}
