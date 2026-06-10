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

    private static async Task<Ok<WorkflowProbe>> ResumeWorkflowAsync(
        ProductApprovalInProcessHost host,
        string instanceId,
        ResumeWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var probe = await host.ResumeAsync(instanceId, request.Decision, cancellationToken);
        return TypedResults.Ok(probe);
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
