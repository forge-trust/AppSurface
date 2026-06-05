using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

var definition = GeneratedApprovalFlow.BuildDefinition(
    graph => graph
        .MapStartNodeApprovedToReviewNode()
        .MarkStartNodeApprovalSubmittedTerminal()
        .MarkStartNodeApprovalTimeoutTerminal()
        .MarkStartNodeDeniedTerminal()
        .MapReviewNodeAgainToReviewNode()
        .MarkReviewNodeDoneTerminal(),
    new GeneratedApprovalFlow.StartNode(),
    new GeneratedApprovalFlow.ReviewNode());
var runner = new InMemoryFlowRunner<GeneratedApprovalFlow.GeneratedApprovalFlowContext>(
    Options.Create(new AppSurfaceFlowOptions()));

var waiting = await runner.RunAsync(
    definition,
    GeneratedApprovalFlow.CreateStartContext(new StartState("created")));
Console.WriteLine($"Waiting: {waiting.WaitingEventName}, timeout: {waiting.Timeout?.Duration.TotalMinutes:0}m");

var completed = await runner.ResumeAsync(
    definition,
    "start",
    waiting.Context!,
    new FlowResumeEvent("approval-submitted", "approved"));
Console.WriteLine($"Completed after re-entry: {completed.Context?.DoneState?.Status}");

var faulted = await runner.ResumeAsync(
    definition,
    "start",
    GeneratedApprovalFlow.CreateStartContext(new StartState("waiting")),
    new FlowResumeEvent("approval-submitted", "denied"));
Console.WriteLine($"Faulted: {faulted.Fault?.Code}");

var timedOut = await runner.ResumeAsync(
    definition,
    "start",
    GeneratedApprovalFlow.CreateStartContext(new StartState("waiting")),
    new FlowResumeEvent("approval-timeout", isTimeout: true));
Console.WriteLine($"Timed out: {timedOut.Context?.StartState?.Status}");

[FlowAuthoring("generated-approval")]
internal partial class GeneratedApprovalFlow
{
    [FlowStart]
    [FlowNode("start", typeof(StartState))]
    [FlowOutcome("approved", FlowOutcomeKind.Next, typeof(ReviewState))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(StartState))]
    [FlowOutcome("approval-timeout", FlowOutcomeKind.TimedOut, typeof(StartState))]
    [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
    internal partial class StartNode : IFlowTransformerNode<StartState, StartNodeOutcomes>
    {
        public ValueTask<StartNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<StartState> context,
            CancellationToken cancellationToken = default)
        {
            if (context.ResumeEvent?.EventName == "approval-timeout")
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.ApprovalTimeout(context.State with { Status = "timed-out" }));
            }

            if (context.ResumeEvent is null)
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.ApprovalSubmitted(
                        context.State with { Status = "waiting" },
                        new FlowTimeout(TimeSpan.FromMinutes(5))));
            }

            if (string.Equals(context.ResumeEvent.Payload?.ToString(), "denied", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.Denied(new FlowFault("approval.denied", "The approval request was denied.")));
            }

            return ValueTask.FromResult<StartNodeOutcomes>(
                StartNodeOutcomes.Approved(new ReviewState(0, "reviewing")));
        }
    }

    [FlowNode("review", typeof(ReviewState))]
    [FlowOutcome("again", FlowOutcomeKind.Next, typeof(ReviewState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneState))]
    internal partial class ReviewNode : IFlowTransformerNode<ReviewState, ReviewNodeOutcomes>
    {
        public ValueTask<ReviewNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<ReviewState> context,
            CancellationToken cancellationToken = default)
        {
            if (context.State.Count == 0)
            {
                return ValueTask.FromResult<ReviewNodeOutcomes>(
                    ReviewNodeOutcomes.Again(context.State with { Count = 1 }));
            }

            return ValueTask.FromResult<ReviewNodeOutcomes>(
                ReviewNodeOutcomes.Done(new DoneState("approved")));
        }
    }
}

internal sealed record StartState(string Status);

internal sealed record ReviewState(int Count, string Status);

internal sealed record DoneState(string Status);
