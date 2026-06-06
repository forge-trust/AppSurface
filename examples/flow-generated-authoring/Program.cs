using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

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
    new FlowResumeEvent("approval-timeout", isTimeout: true));
Console.WriteLine($"Timed out: {timedOut.Context?.ApprovalOpened?.Status}");

[FlowAuthoring("generated-approval")]
internal partial class GeneratedApprovalFlow
{
    [FlowStart]
    [FlowNode("intake", typeof(ApprovalOpened))]
    [FlowOutcome("ready-for-review", FlowOutcomeKind.Next, typeof(ReviewRequested))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(ApprovalOpened))]
    [FlowOutcome("approval-timeout", FlowOutcomeKind.TimedOut, typeof(ApprovalOpened))]
    [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
    internal partial class IntakeNode : IFlowTransformerNode<ApprovalOpened, IntakeNodeOutcomes>
    {
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

    [FlowNode("review", typeof(ReviewRequested))]
    [FlowOutcome("retry", FlowOutcomeKind.Next, typeof(ReviewRequested))]
    [FlowOutcome("approved", FlowOutcomeKind.Complete, typeof(ApprovalCompleted))]
    internal partial class ReviewNode : IFlowTransformerNode<ReviewRequested, ReviewNodeOutcomes>
    {
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

internal sealed record ApprovalRequest(string Id, string RequestedBy, string Summary);

internal sealed record ApprovalOpened(ApprovalRequest Request, string Status);

internal sealed record ReviewRequested(ApprovalRequest Request, int Attempt);

internal sealed record ApprovalCompleted(ApprovalRequest Request, string Decision);
