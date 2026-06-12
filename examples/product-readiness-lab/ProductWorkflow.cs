using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.Extensions.Options;

namespace ProductReadinessLab;

/// <summary>
/// In-process workflow host proof for the product-readiness lab.
/// </summary>
/// <remarks>
/// <para>
/// Decision: this type keeps the evaluator experience lightweight by hosting workflow start,
/// wait, resume, timeout, and resume-authorization behavior in the web app process. The private
/// in-memory runner owns the interactive sample request path, while the DurableTask-facing runner and
/// client are used to prove the worker/client boundary that a real host would own. Use this type for
/// local product-readiness evidence and focused tests; use an <see cref="IDurableTaskFlowRunner{TContext}" />
/// and <see cref="IDurableTaskFlowClient{TContext}" /> backed host when production durability,
/// distributed execution, or long-running orchestration storage is required.
/// </para>
/// <para>
/// Pitfall: pending workflow state is held in memory in this process. A restart loses waiting
/// instances, semaphore ordering is not a durability guarantee, and the in-memory runner can differ
/// from a real Durable Task backend in persistence, replay, and concurrency behavior. The product
/// state store proves product/domain state only; it is not Durable Task orchestration storage. The
/// constructor expects the flow definition, DurableTask-facing runner/client, and product state store
/// to be wired before the host starts receiving workflow requests.
/// </para>
/// </remarks>
internal sealed class ProductApprovalInProcessHost
{
    private readonly FlowDefinition<ProductApprovalState> _definition;
    private readonly InMemoryFlowRunner<ProductApprovalState> _runner;
    private readonly IDurableTaskFlowRunner<ProductApprovalState> _durableRunner;
    private readonly IDurableTaskFlowClient<ProductApprovalState> _durableClient;
    private readonly IProductStateStore _store;
    private readonly ConcurrentDictionary<string, ProductWorkflowWaitingRun> _waitingRuns = new(StringComparer.Ordinal);

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
        var waitingRun = new ProductWorkflowWaitingRun(run);
        if (!_waitingRuns.TryAdd(state.RequestId, waitingRun))
        {
            waitingRun.Dispose();
            throw new InvalidOperationException($"Workflow instance '{state.RequestId}' already exists.");
        }

        return WorkflowProbe.FromWaiting(state.RequestId, run);
    }

    /// <summary>
    /// Resumes a waiting approval workflow in process.
    /// </summary>
    public async Task<WorkflowProbe> ResumeAsync(
        string instanceId,
        string decision,
        string caller,
        CancellationToken cancellationToken = default)
    {
        if (!_waitingRuns.TryGetValue(instanceId, out var waitingRun))
        {
            throw new ProductWorkflowNotWaitingException(instanceId);
        }

        ProductWorkflowWaitingRun? removedRun = null;
        WorkflowProbe? response = null;
        await waitingRun.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!_waitingRuns.TryGetValue(instanceId, out var currentRun) || !ReferenceEquals(currentRun, waitingRun))
            {
                throw new ProductWorkflowNotWaitingException(instanceId);
            }

            var waiting = currentRun.Result;
            if (waiting.NodeId is null || waiting.Context is null)
            {
                throw new ProductWorkflowNotWaitingException(instanceId);
            }

            var authorization = await _durableClient.AuthorizeResumeAsync(
                new FlowResumeAuthorizationRequest(
                    ProductReadinessFlowDefinition.FlowId,
                    ProductReadinessFlowDefinition.Version,
                    instanceId,
                    waiting.NodeId,
                    ProductReadinessFlowDefinition.ApprovalEventName,
                    caller),
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

            response = WorkflowProbe.FromCompleted(instanceId, completed);
            if (completed.Status == FlowRunStatus.Waiting)
            {
                currentRun.Result = completed;
            }
            else
            {
                if (_waitingRuns.TryRemove(instanceId, out var removed))
                {
                    removedRun = removed;
                }
            }
        }
        finally
        {
            waitingRun.Gate.Release();
        }

        using (removedRun)
        {
            return response ?? throw new InvalidOperationException("Workflow resume did not produce a response.");
        }
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
/// Waiting workflow state guarded so only one resume attempt can execute at a time.
/// </summary>
internal sealed class ProductWorkflowWaitingRun : IDisposable
{
    /// <summary>
    /// Creates waiting workflow state.
    /// </summary>
    /// <param name="result">Current waiting result.</param>
    public ProductWorkflowWaitingRun(FlowRunResult<ProductApprovalState> result)
    {
        Result = result;
    }

    /// <summary>
    /// Gets the gate that serializes resume attempts for this workflow instance.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Gets or sets the current waiting result.
    /// </summary>
    public FlowRunResult<ProductApprovalState> Result { get; set; }

    /// <summary>
    /// Disposes the resume gate.
    /// </summary>
    public void Dispose()
    {
        Gate.Dispose();
    }
}

/// <summary>
/// Exception thrown when a resume request targets a workflow instance that is no longer waiting.
/// </summary>
internal sealed class ProductWorkflowNotWaitingException : InvalidOperationException
{
    /// <summary>
    /// Creates the not-waiting exception.
    /// </summary>
    /// <param name="instanceId">Workflow instance id.</param>
    public ProductWorkflowNotWaitingException(string instanceId)
        : base($"Workflow instance '{instanceId}' is not waiting.")
    {
        InstanceId = instanceId;
    }

    /// <summary>
    /// Gets the workflow instance id.
    /// </summary>
    public string InstanceId { get; }
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
            && string.Equals(request.Version, ProductReadinessFlowDefinition.Version, StringComparison.Ordinal)
            && string.Equals(request.NodeId, ProductReadinessFlowDefinition.ReviewNodeId, StringComparison.Ordinal)
            && string.Equals(request.EventName, ProductReadinessFlowDefinition.ApprovalEventName, StringComparison.Ordinal)
            && string.Equals(request.Caller, "operator-1", StringComparison.Ordinal);

        return ValueTask.FromResult(allowed
            ? FlowResumeAuthorizationResult.Allow()
            : FlowResumeAuthorizationResult.Deny("product-readiness.resume-denied", "Only the local operator proof can resume the lab flow."));
    }
}
