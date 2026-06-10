using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.Extensions.Options;

namespace ProductReadinessLab;

/// <summary>
/// In-process workflow host proof for the product-readiness lab.
/// </summary>
internal sealed class ProductApprovalInProcessHost
{
    private readonly FlowDefinition<ProductApprovalState> _definition;
    private readonly InMemoryFlowRunner<ProductApprovalState> _runner;
    private readonly IDurableTaskFlowRunner<ProductApprovalState> _durableRunner;
    private readonly IDurableTaskFlowClient<ProductApprovalState> _durableClient;
    private readonly IProductStateStore _store;
    private readonly ConcurrentDictionary<string, FlowRunResult<ProductApprovalState>> _waitingRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the in-process host proof.
    /// </summary>
    public ProductApprovalInProcessHost(
        FlowDefinition<ProductApprovalState> definition,
        IDurableTaskFlowRunner<ProductApprovalState> durableRunner,
        IDurableTaskFlowClient<ProductApprovalState> durableClient,
        IProductStateStore store)
    {
        _definition = definition;
        _runner = new InMemoryFlowRunner<ProductApprovalState>(Options.Create(new AppSurfaceFlowOptions()));
        _durableRunner = durableRunner;
        _durableClient = durableClient;
        _store = store;
    }

    /// <summary>
    /// Starts the approval workflow in process.
    /// </summary>
    public async Task<WorkflowProbe> StartAsync(
        string accountName,
        string planName,
        CancellationToken cancellationToken = default)
    {
        var subscription = new ProductSubscription(Guid.NewGuid(), accountName, planName, "pending-approval");
        await _store.SaveAsync(subscription, cancellationToken);

        var state = new ProductApprovalState(subscription.Id.ToString("N"), accountName, planName, "created", null);
        var run = await _runner.RunAsync(_definition, state, cancellationToken);
        _waitingRuns[state.RequestId] = run;

        return WorkflowProbe.FromWaiting(state.RequestId, run);
    }

    /// <summary>
    /// Resumes a waiting approval workflow in process.
    /// </summary>
    public async Task<WorkflowProbe> ResumeAsync(
        string instanceId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        if (!_waitingRuns.TryGetValue(instanceId, out var waiting) || waiting.NodeId is null || waiting.Context is null)
        {
            throw new InvalidOperationException($"Workflow instance '{instanceId}' is not waiting.");
        }

        var authorization = await _durableClient.AuthorizeResumeAsync(
            new FlowResumeAuthorizationRequest(
                ProductReadinessFlowDefinition.FlowId,
                ProductReadinessFlowDefinition.Version,
                instanceId,
                waiting.NodeId,
                ProductReadinessFlowDefinition.ApprovalEventName,
                "operator-1"),
            cancellationToken);

        if (!authorization.Allowed)
        {
            throw new InvalidOperationException($"Resume denied: {authorization.Code}.");
        }

        var completed = await _runner.ResumeAsync(
            _definition,
            waiting.NodeId,
            waiting.Context,
            new FlowResumeEvent(ProductReadinessFlowDefinition.ApprovalEventName, decision),
            cancellationToken);

        _waitingRuns.TryRemove(instanceId, out _);

        return WorkflowProbe.FromCompleted(instanceId, completed);
    }

    /// <summary>
    /// Runs the local host-shape proof used by the readiness report.
    /// </summary>
    public async Task<WorkflowHostShapeProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var instanceId = "probe-" + Guid.NewGuid().ToString("N");
        var start = await _durableRunner.StartAsync(
            ProductReadinessFlowDefinition.FlowId,
            ProductReadinessFlowDefinition.Version,
            instanceId,
            new ProductApprovalState(instanceId, "readiness-lab", "team", "created", null),
            cancellationToken);

        var waitStep = new DurableTaskFlowStep<ProductApprovalState>(
            ProductReadinessFlowDefinition.FlowId,
            ProductReadinessFlowDefinition.Version,
            instanceId,
            ProductReadinessFlowDefinition.ReviewNodeId,
            new ProductApprovalState(instanceId, "readiness-lab", "team", "waiting", null));
        var waiting = await _durableRunner.RunNodeAsync(waitStep, cancellationToken);
        var timeout = await _durableRunner.ResumeAsync(
            WithResumeEvent(waitStep, FlowResumeEvent.Timeout(ProductReadinessFlowDefinition.TimeoutEventName)),
            ProductReadinessFlowDefinition.TimeoutEventName,
            cancellationToken);
        var fault = await _durableRunner.ResumeAsync(
            WithResumeEvent(waitStep, new FlowResumeEvent(ProductReadinessFlowDefinition.ApprovalEventName, "denied")),
            ProductReadinessFlowDefinition.ApprovalEventName,
            cancellationToken);
        var late = await _durableRunner.ResumeAsync(
            WithResumeEvent(waitStep, new FlowResumeEvent("unexpected-event", "late")),
            ProductReadinessFlowDefinition.ApprovalEventName,
            cancellationToken);
        var completed = await _durableRunner.ResumeAsync(
            WithResumeEvent(waitStep, new FlowResumeEvent(ProductReadinessFlowDefinition.ApprovalEventName, "approved")),
            ProductReadinessFlowDefinition.ApprovalEventName,
            cancellationToken);

        return new WorkflowHostShapeProbe(
            start.Kind.ToString(),
            waiting.Kind.ToString(),
            waiting.Timeout?.Duration.TotalMinutes ?? 0,
            completed.Kind.ToString(),
            timeout.Kind.ToString(),
            late.Kind.ToString(),
            fault.Fault?.Code ?? "none");
    }

    private static DurableTaskFlowStep<ProductApprovalState> WithResumeEvent(
        DurableTaskFlowStep<ProductApprovalState> step,
        FlowResumeEvent resumeEvent) =>
        new(step.FlowId, step.Version, step.InstanceId, step.NodeId, step.Context, resumeEvent);
}

/// <summary>
/// Workflow probe response.
/// </summary>
internal sealed record WorkflowProbe(
    string InstanceId,
    string Status,
    string? WaitingEventName,
    string? FaultCode)
{
    /// <summary>
    /// Creates a waiting probe response.
    /// </summary>
    public static WorkflowProbe FromWaiting(string instanceId, FlowRunResult<ProductApprovalState> result) =>
        new(instanceId, result.Status.ToString(), result.WaitingEventName, result.Fault?.Code);

    /// <summary>
    /// Creates a completed probe response.
    /// </summary>
    public static WorkflowProbe FromCompleted(string instanceId, FlowRunResult<ProductApprovalState> result) =>
        new(instanceId, result.Status.ToString(), result.WaitingEventName, result.Fault?.Code);
}

/// <summary>
/// Workflow host-shape evidence used by the readiness report.
/// </summary>
internal sealed record WorkflowHostShapeProbe(
    string StartedStatus,
    string WaitingStatus,
    double TimeoutMinutes,
    string CompletedStatus,
    string TimeoutStatus,
    string LateEventStatus,
    string FaultCode);

/// <summary>
/// Serializable workflow context carried by the lab approval flow.
/// </summary>
internal sealed record ProductApprovalState(
    string RequestId,
    string AccountName,
    string PlanName,
    string Status,
    string? Decision);

/// <summary>
/// Product approval flow definition factory.
/// </summary>
internal static class ProductReadinessFlowDefinition
{
    /// <summary>
    /// Stable flow id.
    /// </summary>
    public const string FlowId = "product-approval";

    /// <summary>
    /// Stable flow version.
    /// </summary>
    public const string Version = "1";

    /// <summary>
    /// Review node id.
    /// </summary>
    public const string ReviewNodeId = "review";

    /// <summary>
    /// Approval event name.
    /// </summary>
    public const string ApprovalEventName = "approval-submitted";

    /// <summary>
    /// Timeout event name.
    /// </summary>
    public const string TimeoutEventName = "approval-timeout";

    /// <summary>
    /// Builds the lab flow definition.
    /// </summary>
    public static FlowDefinition<ProductApprovalState> Build() =>
        FlowGraphBuilder<ProductApprovalState>
            .Create(FlowId, Version)
            .AddNode(ReviewNodeId, new ProductApprovalReviewNode())
            .StartAt(ReviewNodeId)
            .Build();
}

/// <summary>
/// Review node for the product approval flow.
/// </summary>
internal sealed class ProductApprovalReviewNode : IFlowNode<ProductApprovalState>
{
    /// <inheritdoc />
    public ValueTask<FlowNodeOutcome<ProductApprovalState>> ExecuteAsync(
        FlowExecutionContext<ProductApprovalState> context,
        CancellationToken cancellationToken = default)
    {
        if (context.ResumeEvent?.EventName == ProductReadinessFlowDefinition.TimeoutEventName)
        {
            return ValueTask.FromResult<FlowNodeOutcome<ProductApprovalState>>(
                FlowNodeOutcome<ProductApprovalState>.TimedOut(
                    ProductReadinessFlowDefinition.TimeoutEventName,
                    context.State with { Status = "timed-out" }));
        }

        if (context.ResumeEvent is null)
        {
            return ValueTask.FromResult<FlowNodeOutcome<ProductApprovalState>>(
                FlowNodeOutcome<ProductApprovalState>.Wait(
                    ProductReadinessFlowDefinition.ApprovalEventName,
                    context.State with { Status = "waiting-for-approval" },
                    new FlowTimeout(TimeSpan.FromMinutes(5))));
        }

        if (string.Equals(context.ResumeEvent.Payload?.ToString(), "denied", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<FlowNodeOutcome<ProductApprovalState>>(
                FlowNodeOutcome<ProductApprovalState>.Fault("approval.denied", "The product approval was denied."));
        }

        return ValueTask.FromResult<FlowNodeOutcome<ProductApprovalState>>(
            FlowNodeOutcome<ProductApprovalState>.Complete(context.State with { Status = "approved", Decision = "approved" }));
    }
}

/// <summary>
/// Lab authorizer that allows only the expected local operator resume path.
/// </summary>
internal sealed class ProductReadinessResumeAuthorizer : IFlowResumeAuthorizer
{
    /// <inheritdoc />
    public ValueTask<FlowResumeAuthorizationResult> AuthorizeAsync(
        FlowResumeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var allowed =
            string.Equals(request.FlowId, ProductReadinessFlowDefinition.FlowId, StringComparison.Ordinal)
            && string.Equals(request.EventName, ProductReadinessFlowDefinition.ApprovalEventName, StringComparison.Ordinal)
            && string.Equals(request.Caller, "operator-1", StringComparison.Ordinal);

        return ValueTask.FromResult(allowed
            ? FlowResumeAuthorizationResult.Allow()
            : FlowResumeAuthorizationResult.Deny("product-readiness.resume-denied", "Only the local operator proof can resume the lab flow."));
    }
}
