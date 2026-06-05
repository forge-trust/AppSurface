using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.Tests;

[FlowAuthoring("generated-approval", Version = "2026-06-03")]
public partial class GeneratedApprovalFlow
{
    [FlowStart]
    [FlowNode("start", typeof(GeneratedStartState))]
    [FlowOutcome("approved", FlowOutcomeKind.Next, typeof(GeneratedReviewState))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(GeneratedStartState))]
    [FlowOutcome("approval-timeout", FlowOutcomeKind.TimedOut, typeof(GeneratedStartState))]
    [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
    public partial class StartNode : IFlowTransformerNode<GeneratedStartState, StartNodeOutcomes>
    {
        public ValueTask<StartNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<GeneratedStartState> context,
            CancellationToken cancellationToken = default)
        {
            if (context.ResumeEvent?.EventName == "approval-timeout")
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.ApprovalTimeout(context.State with { Status = "timed-out" }));
            }

            if (string.Equals(context.ResumeEvent?.Payload?.ToString(), "denied", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.Denied(new FlowFault("approval.denied", "The approval request was denied.")));
            }

            if (context.ResumeEvent is not null)
            {
                return ValueTask.FromResult<StartNodeOutcomes>(
                    StartNodeOutcomes.Approved(new GeneratedReviewState(0, $"review:{context.ResumeEvent.Payload}")));
            }

            return ValueTask.FromResult<StartNodeOutcomes>(
                StartNodeOutcomes.ApprovalSubmitted(context.State with { Status = "waiting" }, new FlowTimeout(TimeSpan.FromMinutes(5))));
        }
    }

    [FlowNode("review", typeof(GeneratedReviewState))]
    [FlowOutcome("again", FlowOutcomeKind.Next, typeof(GeneratedReviewState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(GeneratedDoneState))]
    public partial class ReviewNode : IFlowTransformerNode<GeneratedReviewState, ReviewNodeOutcomes>
    {
        public ValueTask<ReviewNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<GeneratedReviewState> context,
            CancellationToken cancellationToken = default)
        {
            if (context.State.Count == 0)
            {
                return ValueTask.FromResult<ReviewNodeOutcomes>(
                    ReviewNodeOutcomes.Again(context.State with { Count = 1 }));
            }

            return ValueTask.FromResult<ReviewNodeOutcomes>(
                ReviewNodeOutcomes.Done(new GeneratedDoneState(context.State.Status)));
        }
    }
}

public sealed record GeneratedStartState(string Status);

public sealed record GeneratedReviewState(int Count, string Status);

public sealed record GeneratedDoneState(string Status);

public sealed class GeneratedAuthoringRuntimeTests
{
    [Fact]
    public async Task GeneratedDefinition_RunsThroughWaitResumeReentrantAndComplete()
    {
        var definition = GeneratedApprovalFlow.BuildDefinition(
            new GeneratedApprovalFlow.StartNode(),
            new GeneratedApprovalFlow.ReviewNode());
        var runner = new InMemoryFlowRunner<GeneratedApprovalFlow.GeneratedApprovalFlowContext>(
            Options.Create(new AppSurfaceFlowOptions()));

        var waiting = await runner.RunAsync(
            definition,
            GeneratedApprovalFlow.CreateStartContext(new GeneratedStartState("created")));

        Assert.Equal(FlowRunStatus.Waiting, waiting.Status);
        Assert.Equal("approval-submitted", waiting.WaitingEventName);
        Assert.Equal("waiting", waiting.Context?.GeneratedStartState?.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), waiting.Timeout?.Duration);

        var completed = await runner.ResumeAsync(
            definition,
            "start",
            waiting.Context!,
            new FlowResumeEvent("approval-submitted", "andrew"));

        Assert.Equal(FlowRunStatus.Completed, completed.Status);
        Assert.Equal("review:andrew", completed.Context?.GeneratedDoneState?.Status);
    }

    [Fact]
    public async Task GeneratedDefinition_WithExplicitGraphConfiguration_RunsThroughComplete()
    {
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

        var completed = await runner.ResumeAsync(
            definition,
            "start",
            GeneratedApprovalFlow.CreateStartContext(new GeneratedStartState("waiting")),
            new FlowResumeEvent("approval-submitted", "andrew"));

        Assert.Equal(FlowRunStatus.Completed, completed.Status);
        Assert.Equal("review:andrew", completed.Context?.GeneratedDoneState?.Status);
    }

    [Fact]
    public async Task GeneratedDefinition_LowersFaultOutcome()
    {
        var definition = GeneratedApprovalFlow.BuildDefinition(
            new GeneratedApprovalFlow.StartNode(),
            new GeneratedApprovalFlow.ReviewNode());
        var runner = new InMemoryFlowRunner<GeneratedApprovalFlow.GeneratedApprovalFlowContext>(
            Options.Create(new AppSurfaceFlowOptions()));

        var result = await runner.ResumeAsync(
            definition,
            "start",
            GeneratedApprovalFlow.CreateStartContext(new GeneratedStartState("waiting")),
            new FlowResumeEvent("approval-submitted", "denied"));

        Assert.Equal(FlowRunStatus.Faulted, result.Status);
        Assert.Equal("approval.denied", result.Fault?.Code);
    }

    [Fact]
    public async Task GeneratedDefinition_LowersTimedOutOutcome()
    {
        var definition = GeneratedApprovalFlow.BuildDefinition(
            new GeneratedApprovalFlow.StartNode(),
            new GeneratedApprovalFlow.ReviewNode());
        var runner = new InMemoryFlowRunner<GeneratedApprovalFlow.GeneratedApprovalFlowContext>(
            Options.Create(new AppSurfaceFlowOptions()));

        var result = await runner.ResumeAsync(
            definition,
            "start",
            GeneratedApprovalFlow.CreateStartContext(new GeneratedStartState("waiting")),
            new FlowResumeEvent("approval-timeout"));

        Assert.Equal(FlowRunStatus.TimedOut, result.Status);
        Assert.Equal("approval-timeout", result.TimedOutEventName);
        Assert.Equal("timed-out", result.Context?.GeneratedStartState?.Status);
    }
}
