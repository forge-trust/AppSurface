using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Redacted operator-facing AppSurface Docs harvest health response shared by the HTML and JSON health surfaces.
/// </summary>
/// <remarks>
/// This contract intentionally omits repository roots, raw exception messages, stack traces, and other host-local
/// details. Diagnostic cause text is centrally redacted to repository-relative, operator-safe evidence before it reaches
/// this response. Use <see cref="DocAggregator.GetHarvestHealthAsync(System.Threading.CancellationToken)"/> for
/// server-side inspection when trusted code needs the full snapshot.
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
public sealed partial record AppSurfaceDocsHarvestDiagnosticResponse
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
    /// Gets the operator-facing, redacted explanation for why AppSurface Docs reported the diagnostic.
    /// </summary>
    /// <remarks>
    /// This field carries repository-relative evidence or general recovery context. Response mapping centrally redacts
    /// raw host-local exception messages, absolute filesystem paths, stack traces, tokens, and other machine-local
    /// details before serialization.
    /// </remarks>
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
            Cause = RedactCause(diagnostic.Cause),
            Fix = diagnostic.Fix
        };
    }

    internal static string RedactCause(string? cause)
    {
        if (string.IsNullOrWhiteSpace(cause))
        {
            return string.Empty;
        }

        var redacted = StackTraceFrameRegex().Replace(cause, "${indent}at [redacted stack frame]${newline}");
        redacted = WindowsAbsolutePathRegex().Replace(redacted, "[redacted path]");
        redacted = UnixAbsolutePathRegex().Replace(redacted, "[redacted path]");
        redacted = UncPathRegex().Replace(redacted, "[redacted path]");
        redacted = SecretAssignmentRegex().Replace(redacted, "${key}=[redacted]");
        redacted = BearerTokenRegex().Replace(redacted, "${prefix}[redacted]");
        redacted = CommonSecretTokenRegex().Replace(redacted, "[redacted token]");
        redacted = ExceptionMessageRegex().Replace(redacted, "[redacted exception detail]");
        return redacted;
    }

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]+)at[^\r\n]*(?<newline>\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex StackTraceFrameRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s,;:""'<>)]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(@"(?<![\w.-])/(?:Applications|Library|System|Users|Volumes|etc|home|mnt|opt|private|root|srv|tmp|usr|var)(?:/[^\s,;:""'<>)]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixAbsolutePathRegex();

    [GeneratedRegex(@"\\\\[^\s\\/:*?""<>|]+\\[^\s,;:""'<>)]*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"\b(?<key>access_token|api_key|apikey|client_secret|password|pwd|secret|token)=[^&\s,;]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"\b(?<prefix>Bearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b(?:gh[opusr]|github_pat|sk|xox[baprs])[-_][A-Za-z0-9_=-]{8,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommonSecretTokenRegex();

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*Exception\b(?:: [^.\n\r;]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionMessageRegex();
}
