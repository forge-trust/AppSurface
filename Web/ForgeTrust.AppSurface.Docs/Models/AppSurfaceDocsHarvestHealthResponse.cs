using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Redacted operator-facing AppSurface Docs harvest health response shared by the HTML and JSON health surfaces.
/// </summary>
/// <remarks>
/// This contract intentionally omits repository roots, raw exception messages, stack traces, and other host-local
/// details. Diagnostic cause text is included only after harvesters redact it to repository-relative, operator-safe
/// evidence. Use <see cref="DocAggregator.GetHarvestHealthAsync(System.Threading.CancellationToken)"/> for server-side
/// inspection when trusted code needs the full snapshot.
/// </remarks>
public sealed record AppSurfaceDocsHarvestHealthResponse
{
    /// <summary>
    /// Gets the aggregate harvest status name.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Gets the UTC timestamp when the cached harvest snapshot was generated.
    /// </summary>
    [JsonPropertyName("generatedUtc")]
    public DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>
    /// Gets the machine-checkable verification rollup for the response.
    /// </summary>
    [JsonPropertyName("verification")]
    public AppSurfaceDocsHarvestHealthVerification Verification { get; init; } = new();

    /// <summary>
    /// Gets the number of active harvesters that participated in strict aggregate health for the snapshot.
    /// </summary>
    [JsonPropertyName("totalHarvesters")]
    public int TotalHarvesters { get; init; }

    /// <summary>
    /// Gets the number of strict-health harvesters that completed with docs or a valid empty result.
    /// </summary>
    [JsonPropertyName("successfulHarvesters")]
    public int SuccessfulHarvesters { get; init; }

    /// <summary>
    /// Gets the number of strict-health harvesters that failed, timed out, or canceled.
    /// </summary>
    [JsonPropertyName("failedHarvesters")]
    public int FailedHarvesters { get; init; }

    /// <summary>
    /// Gets the number of documentation nodes published by the final cached docs snapshot.
    /// </summary>
    [JsonPropertyName("totalDocs")]
    public int TotalDocs { get; init; }

    /// <summary>
    /// Gets the per-harvester redacted health entries.
    /// </summary>
    [JsonPropertyName("harvesters")]
    public IReadOnlyList<AppSurfaceDocsHarvesterHealthResponse> Harvesters { get; init; } = [];

    /// <summary>
    /// Gets redacted diagnostic entries for failed, degraded, or noteworthy states.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<AppSurfaceDocsHarvestDiagnosticResponse> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the trusted operator rebuild form metadata used by the HTML health page.
    /// </summary>
    /// <remarks>
    /// This value is intentionally ignored by JSON serialization because it contains request-scoped browser URLs and
    /// anti-forgery form context rather than harvest-health facts.
    /// </remarks>
    [JsonIgnore]
    public AppSurfaceDocsHarvestRebuildForm? RebuildForm { get; init; }

    /// <summary>
    /// Creates a redacted response from the full server-side harvest health snapshot.
    /// </summary>
    /// <param name="health">The server-side harvest health snapshot.</param>
    /// <returns>A response that is safe to serialize to clients.</returns>
    public static AppSurfaceDocsHarvestHealthResponse FromSnapshot(DocHarvestHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);

        var statusCode = GetHttpStatusCode(health.Status);
        return new AppSurfaceDocsHarvestHealthResponse
        {
            Status = health.Status.ToString(),
            GeneratedUtc = health.GeneratedUtc,
            Verification = new AppSurfaceDocsHarvestHealthVerification
            {
                Ok = IsOk(health.Status),
                HttpStatusCode = statusCode
            },
            TotalHarvesters = health.TotalHarvesters,
            SuccessfulHarvesters = health.SuccessfulHarvesters,
            FailedHarvesters = health.FailedHarvesters,
            TotalDocs = health.TotalDocs,
            Harvesters = health.Harvesters.Select(AppSurfaceDocsHarvesterHealthResponse.FromHarvester).ToArray(),
            Diagnostics = health.Diagnostics.Select(AppSurfaceDocsHarvestDiagnosticResponse.FromDiagnostic).ToArray()
        };
    }

    internal static bool IsOk(DocHarvestHealthStatus status)
    {
        return status is DocHarvestHealthStatus.Healthy or DocHarvestHealthStatus.Empty;
    }

    internal static int GetHttpStatusCode(DocHarvestHealthStatus status)
    {
        return IsOk(status) ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
    }
}

/// <summary>
/// Request-scoped trusted operator action metadata for rebuilding the live AppSurface Docs harvest.
/// </summary>
public sealed record AppSurfaceDocsHarvestRebuildForm
{
    /// <summary>
    /// Gets the path-base-aware rebuild form action.
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Gets the HTTP method required by the rebuild endpoint.
    /// </summary>
    public string Method { get; init; } = DocsUrlBuilder.HarvestRebuildMethod;

    /// <summary>
    /// Gets the validated app-relative docs URL to revisit after rebuild completion.
    /// </summary>
    public string ReturnUrl { get; init; } = "/";

    /// <summary>
    /// Gets a value indicating whether the current request user may submit the rebuild form.
    /// </summary>
    public bool IsAuthorized { get; init; } = true;

    /// <summary>
    /// Gets the short visible state for the rebuild action.
    /// </summary>
    public string Status { get; init; } = "Ready";

    /// <summary>
    /// Gets the operator-facing explanation for the current rebuild action state.
    /// </summary>
    public string Description { get; init; } =
        "Rebuild the live docs snapshot from source and watch progress before returning to this docs context.";
}

/// <summary>
/// Machine-checkable verification rollup for an AppSurface Docs harvest health response.
/// </summary>
public sealed record AppSurfaceDocsHarvestHealthVerification
{
    /// <summary>
    /// Gets a value indicating whether the harvest state should pass local or CI verification.
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>
    /// Gets the HTTP status code AppSurface Docs uses for this response.
    /// </summary>
    [JsonPropertyName("httpStatusCode")]
    public int HttpStatusCode { get; init; }
}

/// <summary>
/// Redacted per-harvester health entry in the operator-facing harvest health response.
/// </summary>
public sealed record AppSurfaceDocsHarvesterHealthResponse
{
    /// <summary>
    /// Gets the concrete harvester type name.
    /// </summary>
    [JsonPropertyName("harvesterType")]
    public string HarvesterType { get; init; } = string.Empty;

    /// <summary>
    /// Gets the harvester status name.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of documentation nodes returned by the harvester before AppSurface Docs post-processing.
    /// </summary>
    [JsonPropertyName("docCount")]
    public int DocCount { get; init; }

    /// <summary>
    /// Gets the redacted diagnostic explaining a failed, timed-out, or canceled harvester.
    /// </summary>
    [JsonPropertyName("diagnostic")]
    public AppSurfaceDocsHarvestDiagnosticResponse? Diagnostic { get; init; }

    internal static AppSurfaceDocsHarvesterHealthResponse FromHarvester(DocHarvesterHealth harvester)
    {
        ArgumentNullException.ThrowIfNull(harvester);

        return new AppSurfaceDocsHarvesterHealthResponse
        {
            HarvesterType = harvester.HarvesterType,
            Status = harvester.Status.ToString(),
            DocCount = harvester.DocCount,
            Diagnostic = harvester.Diagnostic is null
                ? null
                : AppSurfaceDocsHarvestDiagnosticResponse.FromDiagnostic(harvester.Diagnostic)
        };
    }
}

/// <summary>
/// Redacted diagnostic entry in the operator-facing harvest health response.
/// </summary>
public sealed record AppSurfaceDocsHarvestDiagnosticResponse
{
    /// <summary>
    /// Gets the stable diagnostic code.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Gets the diagnostic severity name.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    /// <summary>
    /// Gets the concrete harvester type when the diagnostic belongs to one harvester.
    /// </summary>
    [JsonPropertyName("harvesterType")]
    public string? HarvesterType { get; init; }

    /// <summary>
    /// Gets the operator-facing problem statement.
    /// </summary>
    [JsonPropertyName("problem")]
    public string Problem { get; init; } = string.Empty;

    /// <summary>
    /// Gets the operator-facing explanation for why AppSurface Docs reported the diagnostic.
    /// </summary>
    [JsonPropertyName("cause")]
    public string Cause { get; init; } = string.Empty;

    /// <summary>
    /// Gets the suggested operator or docs-author recovery action.
    /// </summary>
    [JsonPropertyName("fix")]
    public string Fix { get; init; } = string.Empty;

    internal static AppSurfaceDocsHarvestDiagnosticResponse FromDiagnostic(DocHarvestDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new AppSurfaceDocsHarvestDiagnosticResponse
        {
            Code = diagnostic.Code,
            Severity = diagnostic.Severity.ToString(),
            HarvesterType = diagnostic.HarvesterType,
            Problem = diagnostic.Problem,
            Cause = diagnostic.Cause,
            Fix = diagnostic.Fix
        };
    }
}
