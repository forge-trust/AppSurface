using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

[assembly: ExcludeFromCodeCoverage(Justification = "Executable sample is validated by example runs and snippet tests; Flow runtime and generator behavior are covered by package tests.")]

var request = new ApprovalRequest("APR-1001", "andrew", "Approve the generated authoring sample.");
var inferredDefinition = GeneratedApprovalFlow.BuildDefinition(
    new GeneratedApprovalFlow.IntakeNode(),
    new GeneratedApprovalFlow.ReviewNode());
var explicitDefinition = GeneratedApprovalFlow.BuildDefinition(
    graph => graph
        .MapIntakeNodeReadyForReviewToReviewNode()
        .MarkIntakeNodeApprovalSubmittedTerminal()
        .MarkIntakeNodeApprovalTimeoutTerminal()
        .MarkIntakeNodeDeniedTerminal()
        .MapReviewNodeRetryToReviewNode()
        .MarkReviewNodeApprovedTerminal(),
    new GeneratedApprovalFlow.IntakeNode(),
    new GeneratedApprovalFlow.ReviewNode());
var runner = new InMemoryFlowRunner<GeneratedApprovalFlow.GeneratedApprovalFlowContext>(
    Options.Create(new AppSurfaceFlowOptions()));

var waiting = await runner.RunAsync(
    inferredDefinition,
    GeneratedApprovalFlow.CreateStartContext(new ApprovalOpened(request, "created")));
Console.WriteLine($"Waiting: {waiting.WaitingEventName}, timeout: {waiting.Timeout?.Duration.TotalMinutes:0}m");

var completed = await runner.ResumeAsync(
    inferredDefinition,
    "intake",
    waiting.Context!,
    new FlowResumeEvent("approval-submitted", "approved"));
Console.WriteLine($"Completed after re-entry: {completed.Context?.ApprovalCompleted?.Decision}");

var faulted = await runner.ResumeAsync(
    explicitDefinition,
    "intake",
    GeneratedApprovalFlow.CreateStartContext(new ApprovalOpened(request, "waiting")),
    new FlowResumeEvent("approval-submitted", "denied"));
Console.WriteLine($"Faulted: {faulted.Fault?.Code}");

var timedOut = await runner.ResumeAsync(
    explicitDefinition,
    "intake",
    GeneratedApprovalFlow.CreateStartContext(new ApprovalOpened(request, "waiting")),
    FlowResumeEvent.Timeout("approval-timeout"));
Console.WriteLine($"Timed out: {timedOut.Context?.ApprovalOpened?.Status}");

/// <summary>
/// Example generated-authoring flow for a two-step approval process.
/// </summary>
/// <remarks>
/// The <see cref="FlowAuthoringAttribute"/> generator creates typed outcomes, an envelope context, adapters, and
/// definition builders from this partial specification. The flow starts at <see cref="IntakeNode"/>, waits for an
/// <c>approval-submitted</c> resume event, can fault when the payload is <c>denied</c>, and completes after
/// <see cref="ReviewNode"/> performs one re-entrant retry.
/// </remarks>
[FlowAuthoring("generated-approval")]
internal partial class GeneratedApprovalFlow
{
    /// <summary>
    /// Intake node that opens the approval, waits for an external decision, and emits the review port.
    /// </summary>
    /// <remarks>
    /// The initial execution returns a wait outcome with a five-minute timeout. A resume event named
    /// <c>approval-timeout</c> returns a timed-out outcome; a payload of <c>denied</c> returns a modeled
    /// <see cref="FlowFault"/>; any other resume payload moves to <see cref="ReviewRequested"/>.
    /// </remarks>
    [FlowStart]
    [FlowNode("intake", typeof(ApprovalOpened))]
    [FlowOutcome("ready-for-review", FlowOutcomeKind.Next, typeof(ReviewRequested))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(ApprovalOpened))]
    [FlowOutcome("approval-timeout", FlowOutcomeKind.TimedOut, typeof(ApprovalOpened))]
    [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
    internal partial class IntakeNode : IFlowTransformerNode<ApprovalOpened, IntakeNodeOutcomes>
    {
        /// <summary>
        /// Executes the intake step and returns one generated intake outcome case.
        /// </summary>
        /// <param name="context">Typed transformer context carrying the current <see cref="ApprovalOpened"/> state.</param>
        /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
        /// <returns>An intake outcome representing wait, timeout, fault, or review readiness.</returns>
        public ValueTask<IntakeNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<ApprovalOpened> context,
            CancellationToken cancellationToken = default)
        {
            if (context.ResumeEvent?.EventName == "approval-timeout")
            {
                return ValueTask.FromResult<IntakeNodeOutcomes>(
                    IntakeNodeOutcomes.ApprovalTimeout(context.State with { Status = "timed-out" }));
            }

            if (context.ResumeEvent is null)
            {
                return ValueTask.FromResult<IntakeNodeOutcomes>(
                    IntakeNodeOutcomes.ApprovalSubmitted(
                        context.State with { Status = "waiting" },
                        new FlowTimeout(TimeSpan.FromMinutes(5))));
            }

            if (string.Equals(context.ResumeEvent.Payload?.ToString(), "denied", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IntakeNodeOutcomes>(
                    IntakeNodeOutcomes.Denied(new FlowFault("approval.denied", "The approval request was denied.")));
            }

            return ValueTask.FromResult<IntakeNodeOutcomes>(
                IntakeNodeOutcomes.ReadyForReview(new ReviewRequested(context.State.Request, Attempt: 0)));
        }
    }

    /// <summary>
    /// Review node that demonstrates a re-entrant transition before completing the approval.
    /// </summary>
    /// <remarks>
    /// The first review attempt emits <see cref="ReviewRequested"/> again with <see cref="ReviewRequested.Attempt"/>
    /// incremented to one. Later attempts complete with an <see cref="ApprovalCompleted"/> port.
    /// </remarks>
    [FlowNode("review", typeof(ReviewRequested))]
    [FlowOutcome("retry", FlowOutcomeKind.Next, typeof(ReviewRequested))]
    [FlowOutcome("approved", FlowOutcomeKind.Complete, typeof(ApprovalCompleted))]
    internal partial class ReviewNode : IFlowTransformerNode<ReviewRequested, ReviewNodeOutcomes>
    {
        /// <summary>
        /// Executes review and returns either the retry or approved generated outcome case.
        /// </summary>
        /// <param name="context">Typed transformer context carrying the current <see cref="ReviewRequested"/> state.</param>
        /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
        /// <returns>A review outcome representing a retry or successful approval.</returns>
        public ValueTask<ReviewNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<ReviewRequested> context,
            CancellationToken cancellationToken = default)
        {
            if (context.State.Attempt == 0)
            {
                return ValueTask.FromResult<ReviewNodeOutcomes>(
                    ReviewNodeOutcomes.Retry(context.State with { Attempt = 1 }));
            }

            return ValueTask.FromResult<ReviewNodeOutcomes>(
                ReviewNodeOutcomes.Approved(new ApprovalCompleted(context.State.Request, Decision: "approved")));
        }
    }
}

/// <summary>
/// Immutable approval request identity and display data used by all sample ports.
/// </summary>
/// <param name="Id">Stable approval identifier such as <c>APR-1001</c>.</param>
/// <param name="RequestedBy">Actor or account requesting approval.</param>
/// <param name="Summary">Human-readable approval summary.</param>
internal sealed record ApprovalRequest(string Id, string RequestedBy, string Summary);

/// <summary>
/// Input port for the intake node.
/// </summary>
/// <param name="Request">Approval request being opened.</param>
/// <param name="Status">Lifecycle status such as <c>created</c>, <c>waiting</c>, or <c>timed-out</c>.</param>
internal sealed record ApprovalOpened(ApprovalRequest Request, string Status);

/// <summary>
/// Input and retry port for the review node.
/// </summary>
/// <param name="Request">Approval request being reviewed.</param>
/// <param name="Attempt">Zero-based review attempt count; this sample completes after the first retry.</param>
internal sealed record ReviewRequested(ApprovalRequest Request, int Attempt);

/// <summary>
/// Terminal completion port for approved requests.
/// </summary>
/// <param name="Request">Approved request.</param>
/// <param name="Decision">Decision text returned by the review step.</param>
internal sealed record ApprovalCompleted(ApprovalRequest Request, string Decision);
