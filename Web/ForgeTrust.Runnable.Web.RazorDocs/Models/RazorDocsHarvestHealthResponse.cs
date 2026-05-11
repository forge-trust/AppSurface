using System.Text.Json.Serialization;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.Runnable.Web.RazorDocs.Models;

/// <summary>
/// Redacted operator-facing RazorDocs harvest health response shared by the HTML and JSON health surfaces.
/// </summary>
/// <remarks>
/// This contract intentionally omits repository roots, diagnostic cause text, raw exception messages, stack traces, and
/// other host-local details. Use <see cref="DocAggregator.GetHarvestHealthAsync(System.Threading.CancellationToken)"/>
/// for server-side inspection when trusted code needs the full snapshot.
/// </remarks>
public sealed record RazorDocsHarvestHealthResponse
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
    public RazorDocsHarvestHealthVerification Verification { get; init; } = new();

    /// <summary>
    /// Gets the number of configured harvesters in the snapshot.
    /// </summary>
    [JsonPropertyName("totalHarvesters")]
    public int TotalHarvesters { get; init; }

    /// <summary>
    /// Gets the number of harvesters that completed with docs or a valid empty result.
    /// </summary>
    [JsonPropertyName("successfulHarvesters")]
    public int SuccessfulHarvesters { get; init; }

    /// <summary>
    /// Gets the number of harvesters that failed, timed out, or canceled.
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
    public IReadOnlyList<RazorDocsHarvesterHealthResponse> Harvesters { get; init; } = [];

    /// <summary>
    /// Gets redacted diagnostic entries for failed, degraded, or noteworthy states.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<RazorDocsHarvestDiagnosticResponse> Diagnostics { get; init; } = [];

    /// <summary>
    /// Creates a redacted response from the full server-side harvest health snapshot.
    /// </summary>
    /// <param name="health">The server-side harvest health snapshot.</param>
    /// <returns>A response that is safe to serialize to clients.</returns>
    public static RazorDocsHarvestHealthResponse FromSnapshot(DocHarvestHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);

        var statusCode = GetHttpStatusCode(health.Status);
        return new RazorDocsHarvestHealthResponse
        {
            Status = health.Status.ToString(),
            GeneratedUtc = health.GeneratedUtc,
            Verification = new RazorDocsHarvestHealthVerification
            {
                Ok = IsOk(health.Status),
                HttpStatusCode = statusCode
            },
            TotalHarvesters = health.TotalHarvesters,
            SuccessfulHarvesters = health.SuccessfulHarvesters,
            FailedHarvesters = health.FailedHarvesters,
            TotalDocs = health.TotalDocs,
            Harvesters = health.Harvesters.Select(RazorDocsHarvesterHealthResponse.FromHarvester).ToArray(),
            Diagnostics = health.Diagnostics.Select(RazorDocsHarvestDiagnosticResponse.FromDiagnostic).ToArray()
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
/// Machine-checkable verification rollup for a RazorDocs harvest health response.
/// </summary>
public sealed record RazorDocsHarvestHealthVerification
{
    /// <summary>
    /// Gets a value indicating whether the harvest state should pass local or CI verification.
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>
    /// Gets the HTTP status code RazorDocs uses for this response.
    /// </summary>
    [JsonPropertyName("httpStatusCode")]
    public int HttpStatusCode { get; init; }
}

/// <summary>
/// Redacted per-harvester health entry in the operator-facing harvest health response.
/// </summary>
public sealed record RazorDocsHarvesterHealthResponse
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
    /// Gets the number of documentation nodes returned by the harvester before RazorDocs post-processing.
    /// </summary>
    [JsonPropertyName("docCount")]
    public int DocCount { get; init; }

    /// <summary>
    /// Gets the redacted diagnostic explaining a failed, timed-out, or canceled harvester.
    /// </summary>
    [JsonPropertyName("diagnostic")]
    public RazorDocsHarvestDiagnosticResponse? Diagnostic { get; init; }

    internal static RazorDocsHarvesterHealthResponse FromHarvester(DocHarvesterHealth harvester)
    {
        ArgumentNullException.ThrowIfNull(harvester);

        return new RazorDocsHarvesterHealthResponse
        {
            HarvesterType = harvester.HarvesterType,
            Status = harvester.Status.ToString(),
            DocCount = harvester.DocCount,
            Diagnostic = harvester.Diagnostic is null
                ? null
                : RazorDocsHarvestDiagnosticResponse.FromDiagnostic(harvester.Diagnostic)
        };
    }
}

/// <summary>
/// Redacted diagnostic entry in the operator-facing harvest health response.
/// </summary>
public sealed record RazorDocsHarvestDiagnosticResponse
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
    /// Gets the suggested operator or docs-author recovery action.
    /// </summary>
    [JsonPropertyName("fix")]
    public string Fix { get; init; } = string.Empty;

    internal static RazorDocsHarvestDiagnosticResponse FromDiagnostic(DocHarvestDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new RazorDocsHarvestDiagnosticResponse
        {
            Code = diagnostic.Code,
            Severity = diagnostic.Severity.ToString(),
            HarvesterType = diagnostic.HarvesterType,
            Problem = diagnostic.Problem,
            Fix = diagnostic.Fix
        };
    }
}
