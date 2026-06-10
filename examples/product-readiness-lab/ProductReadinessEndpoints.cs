using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ProductReadinessLab;

/// <summary>
/// HTTP endpoints exposed by the product-readiness lab.
/// </summary>
internal static class ProductReadinessEndpoints
{
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
        endpoints.MapGet("/auth/allowed", EvaluatePolicyAsync);
        endpoints.MapGet("/auth/forbidden", EvaluatePolicyAsync);
        endpoints.MapPost("/workflow/start", StartWorkflowAsync);
        endpoints.MapPost("/workflow/{instanceId}/resume", ResumeWorkflowAsync);
    }

    private static async Task<AuthProbe> EvaluatePolicyAsync(IAppSurfaceAspNetCorePolicyEvaluator evaluator)
    {
        var result = await evaluator.AuthorizeAsync(ProductReadinessPolicies.OperatorsOnly);
        return AuthProbe.FromResult(result);
    }

    private static async Task<Created<WorkflowProbe>> StartWorkflowAsync(
        ProductApprovalInProcessHost host,
        StartWorkflowRequest request,
        CancellationToken cancellationToken)
    {
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
    internal static async Task<Results<Ok<WorkflowProbe>, Conflict<WorkflowResumeRejected>>> ResumeWorkflowAsync(
        ProductApprovalInProcessHost host,
        string instanceId,
        ResumeWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var probe = await host.ResumeAsync(instanceId, request.Decision, cancellationToken);
            return TypedResults.Ok(probe);
        }
        catch (ProductWorkflowNotWaitingException exception)
        {
            return TypedResults.Conflict(WorkflowResumeRejected.LateEvent(exception.InstanceId));
        }
    }
}

/// <summary>
/// Request body for starting the lab workflow.
/// </summary>
/// <param name="AccountName">Display account name used in the sample product state.</param>
/// <param name="PlanName">Plan name used in the sample product state.</param>
internal sealed record StartWorkflowRequest(string AccountName, string PlanName);

/// <summary>
/// Request body for resuming the lab workflow.
/// </summary>
/// <param name="Decision">Approval decision, normally approved or denied.</param>
internal sealed record ResumeWorkflowRequest(string Decision);

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
