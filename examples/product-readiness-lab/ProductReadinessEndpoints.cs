using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ProductReadinessLab;

/// <summary>
/// HTTP endpoints exposed by the product-readiness lab.
/// </summary>
internal static class ProductReadinessEndpoints
{
    internal const int AccountNameMaxLength = 120;
    internal const int PlanNameMaxLength = 80;
    internal const int DecisionMaxLength = 40;

    /// <summary>
    /// Maps product-readiness lab endpoints.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder supplied by AppSurface Web.</param>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Text("AppSurface product-readiness lab is running.", "text/plain"));
        endpoints.MapGet("/readiness", async (ProductReadinessReportService reports, CancellationToken cancellationToken) =>
            ReadinessReportResponse.FromReport(await reports.BuildAsync(cancellationToken)));
        endpoints.MapGet("/readiness.md", async (ProductReadinessReportService reports, CancellationToken cancellationToken) =>
        {
            var report = await reports.BuildAsync(cancellationToken);
            return Results.Text(ReadinessReportMarkdownRenderer.Render(report), "text/markdown");
        });
        endpoints.MapGet("/auth/allowed", (IAppSurfaceAspNetCorePolicyEvaluator evaluator) =>
            EvaluatePolicyAsync(evaluator, ProductReadinessPolicies.OperatorsOnly));
        endpoints.MapGet("/auth/forbidden", (IAppSurfaceAspNetCorePolicyEvaluator evaluator) =>
            EvaluatePolicyAsync(evaluator, ProductReadinessPolicies.UnavailableEntitlement));
        endpoints.MapPost("/workflow/start", StartWorkflowAsync)
            .RequireAuthorization(ProductReadinessPolicies.OperatorsOnly);
        endpoints.MapPost("/workflow/{instanceId}/resume", ResumeWorkflowAsync)
            .RequireAuthorization(ProductReadinessPolicies.OperatorsOnly);
    }

    private static async Task<AuthProbe> EvaluatePolicyAsync(IAppSurfaceAspNetCorePolicyEvaluator evaluator, string policyName)
    {
        var result = await evaluator.AuthorizeAsync(policyName);
        return AuthProbe.FromResult(result);
    }

    private static async Task<Results<Created<WorkflowProbe>, BadRequest<WorkflowRequestRejected>>> StartWorkflowAsync(
        ProductApprovalInProcessHost host,
        StartWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var rejected))
        {
            return TypedResults.BadRequest(rejected);
        }

        var probe = await host.StartAsync(request.AccountName, request.PlanName, cancellationToken);
        return TypedResults.Created($"/workflow/{probe.InstanceId}", probe);
    }

    /// <summary>
    /// Resumes a workflow or returns a handled late-event response when the instance is no longer waiting.
    /// </summary>
    /// <param name="host">In-process host that owns waiting workflow instances.</param>
    /// <param name="instanceId">Workflow instance id.</param>
    /// <param name="request">Resume request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completed workflow response, or a conflict response for late or unknown resume events.</returns>
    internal static async Task<Results<Ok<WorkflowProbe>, Conflict<WorkflowResumeRejected>, BadRequest<WorkflowRequestRejected>>> ResumeWorkflowAsync(
        ProductApprovalInProcessHost host,
        ClaimsPrincipal user,
        string instanceId,
        ResumeWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var rejected))
        {
            return TypedResults.BadRequest(rejected);
        }

        var caller = user.FindFirstValue(ProductReadinessClaimNames.Subject);
        if (string.IsNullOrWhiteSpace(caller))
        {
            throw new InvalidOperationException("Authenticated operator subject is required to resume the product-readiness workflow.");
        }

        try
        {
            var probe = await host.ResumeAsync(instanceId, request.Decision, caller, cancellationToken);
            return TypedResults.Ok(probe);
        }
        catch (ProductWorkflowNotWaitingException exception)
        {
            return TypedResults.Conflict(WorkflowResumeRejected.LateEvent(exception.InstanceId));
        }
    }

    private static bool TryValidateRequest<TRequest>(
        TRequest request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out WorkflowRequestRejected? rejected)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(request!);
        if (Validator.TryValidateObject(request!, validationContext, validationResults, validateAllProperties: true))
        {
            rejected = null;
            return true;
        }

        rejected = WorkflowRequestRejected.Invalid(validationResults);
        return false;
    }
}

/// <summary>
/// Request body for starting the lab workflow.
/// </summary>
/// <param name="AccountName">Display account name used in the sample product state.</param>
/// <param name="PlanName">Plan name used in the sample product state.</param>
internal sealed record StartWorkflowRequest(
    [property: Required]
    [property: StringLength(ProductReadinessEndpoints.AccountNameMaxLength, MinimumLength = 1)]
    string AccountName,
    [property: Required]
    [property: StringLength(ProductReadinessEndpoints.PlanNameMaxLength, MinimumLength = 1)]
    string PlanName);

/// <summary>
/// Request body for resuming the lab workflow.
/// </summary>
/// <param name="Decision">Approval decision, normally approved or denied.</param>
internal sealed record ResumeWorkflowRequest(
    [property: Required]
    [property: StringLength(ProductReadinessEndpoints.DecisionMaxLength, MinimumLength = 1)]
    string Decision);

/// <summary>
/// Response body for invalid workflow request payloads.
/// </summary>
/// <param name="Status">Stable invalid-request status.</param>
/// <param name="Errors">Validation messages keyed by request field.</param>
internal sealed record WorkflowRequestRejected(string Status, IReadOnlyDictionary<string, string[]> Errors)
{
    /// <summary>
    /// Creates an invalid-request response from validation results.
    /// </summary>
    /// <param name="validationResults">DataAnnotations validation results.</param>
    /// <returns>A serializable invalid-request response.</returns>
    public static WorkflowRequestRejected Invalid(IEnumerable<ValidationResult> validationResults)
    {
        var errors = validationResults
            .SelectMany(result =>
            {
                var memberNames = result.MemberNames.Any()
                    ? result.MemberNames
                    : new[] { "request" };
                return memberNames.Select(memberName => new
                {
                    MemberName = memberName,
                    Message = result.ErrorMessage ?? "Request value is invalid."
                });
            })
            .GroupBy(item => item.MemberName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Message).ToArray(),
                StringComparer.Ordinal);

        return new WorkflowRequestRejected("InvalidRequest", errors);
    }
}

/// <summary>
/// Response body for resume requests that arrive after an instance is no longer waiting.
/// </summary>
/// <param name="InstanceId">Workflow instance id.</param>
/// <param name="Status">Stable status for late or duplicate resume events.</param>
/// <param name="Reason">Human-readable reason.</param>
internal sealed record WorkflowResumeRejected(string InstanceId, string Status, string Reason)
{
    /// <summary>
    /// Creates a late-event response for a workflow instance that is not waiting.
    /// </summary>
    /// <param name="instanceId">Workflow instance id.</param>
    /// <returns>A serializable late-event response.</returns>
    public static WorkflowResumeRejected LateEvent(string instanceId) =>
        new(instanceId, "IgnoredLateEvent", "Workflow instance is not waiting for this resume event.");
}

/// <summary>
/// Response DTO showing the neutral AppSurface auth result returned by the lab.
/// </summary>
/// <param name="Outcome">Neutral AppSurface outcome name.</param>
/// <param name="Reason">Neutral AppSurface reason name.</param>
/// <param name="Subject">Mapped subject id when the request resolved one.</param>
/// <param name="Metadata">Safe diagnostic metadata returned by the adapter.</param>
internal sealed record AuthProbe(
    string Outcome,
    string Reason,
    string? Subject,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Creates a response DTO from an AppSurface auth result without exposing raw claims.
    /// </summary>
    /// <param name="result">The neutral AppSurface auth result produced by the adapter.</param>
    /// <returns>A serializable probe response.</returns>
    public static AuthProbe FromResult(ForgeTrust.AppSurface.Auth.AppSurfaceAuthResult result) =>
        new(result.Outcome.ToString(), result.Reason.ToString(), result.Context?.User?.Id, result.Metadata);
}
