using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.Tests;

/// <summary>
/// Public test fixture flow used to verify generated authoring runtime behavior.
/// </summary>
/// <remarks>
/// The flow starts at <see cref="StartNode"/>, waits for an approval event, maps accepted events into
/// <see cref="GeneratedReviewState"/>, retries review once, and completes with <see cref="GeneratedDoneState"/>.
/// It also documents the timeout and fault branches that generated adapters lower into the low-level runtime.
/// </remarks>
[FlowAuthoring("generated-approval", Version = "2026-06-03")]
public partial class GeneratedApprovalFlow
{
    /// <summary>
    /// Start node that waits, resumes, times out, faults, or advances to review based on the resume event.
    /// </summary>
    [FlowStart]
    [FlowNode("start", typeof(GeneratedStartState))]
    [FlowOutcome("approved", FlowOutcomeKind.Next, typeof(GeneratedReviewState))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(GeneratedStartState))]
    [FlowOutcome("approval-timeout", FlowOutcomeKind.TimedOut, typeof(GeneratedStartState))]
    [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
    public partial class StartNode : IFlowTransformerNode<GeneratedStartState, StartNodeOutcomes>
    {
        /// <summary>
        /// Executes the start node and returns one generated start outcome case.
        /// </summary>
        /// <param name="context">Typed context containing <see cref="GeneratedStartState"/> and an optional resume event.</param>
        /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
        /// <returns>
        /// Waits with a five-minute timeout initially, times out on <c>approval-timeout</c>, faults on payload
        /// <c>denied</c>, or advances to review for other resume payloads.
        /// </returns>
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

    /// <summary>
    /// Review node that retries once before completing the generated flow.
    /// </summary>
    [FlowNode("review", typeof(GeneratedReviewState))]
    [FlowOutcome("again", FlowOutcomeKind.Next, typeof(GeneratedReviewState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(GeneratedDoneState))]
    public partial class ReviewNode : IFlowTransformerNode<GeneratedReviewState, ReviewNodeOutcomes>
    {
        /// <summary>
        /// Executes review and returns either the re-entrant review outcome or the completion outcome.
        /// </summary>
        /// <param name="context">Typed review context with the current count and status.</param>
        /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
        /// <returns>A generated review outcome that retries while count is zero and completes otherwise.</returns>
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

/// <summary>
/// Start-node input state used by generated runtime tests.
/// </summary>
/// <param name="Status">Current approval status such as <c>created</c>, <c>waiting</c>, or <c>timed-out</c>.</param>
public sealed record GeneratedStartState(string Status);

/// <summary>
/// Review-node input state and re-entry port used by generated runtime tests.
/// </summary>
/// <param name="Count">Number of review executions already attempted.</param>
/// <param name="Status">Status text carried from the resume payload.</param>
public sealed record GeneratedReviewState(int Count, string Status);

/// <summary>
/// Terminal completion state produced by generated runtime tests.
/// </summary>
/// <param name="Status">Final status copied from the review state.</param>
public sealed record GeneratedDoneState(string Status);

/// <summary>
/// Verifies generated authoring definitions through the public in-memory runner.
/// </summary>
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

    [Theory]
    [InlineData("next")]
    [InlineData("wait")]
    [InlineData("timedOut")]
    [InlineData("complete")]
    [InlineData("fault")]
    public void GeneratedOutcomeFactories_WithNullContext_ThrowArgumentNullException(string scenario)
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = scenario switch
            {
                "next" => (object)GeneratedApprovalFlow.StartNodeOutcomes.Approved(null!),
                "wait" => GeneratedApprovalFlow.StartNodeOutcomes.ApprovalSubmitted(null!),
                "timedOut" => GeneratedApprovalFlow.StartNodeOutcomes.ApprovalTimeout(null!),
                "complete" => GeneratedApprovalFlow.ReviewNodeOutcomes.Done(null!),
                "fault" => GeneratedApprovalFlow.StartNodeOutcomes.Denied(null!),
                _ => throw new InvalidOperationException($"Unknown scenario '{scenario}'."),
            };
        });
    }

    [Theory]
    [InlineData("next")]
    [InlineData("wait")]
    [InlineData("timedOut")]
    [InlineData("complete")]
    [InlineData("fault")]
    public void GeneratedOutcomeConstructors_WithNullContext_ThrowArgumentNullException(string scenario)
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = scenario switch
            {
                "next" => (object)new GeneratedApprovalFlow.StartNodeOutcomes.ApprovedOutcome(null!),
                "wait" => new GeneratedApprovalFlow.StartNodeOutcomes.ApprovalSubmittedOutcome(null!),
                "timedOut" => new GeneratedApprovalFlow.StartNodeOutcomes.ApprovalTimeoutOutcome(null!),
                "complete" => new GeneratedApprovalFlow.ReviewNodeOutcomes.DoneOutcome(null!),
                "fault" => new GeneratedApprovalFlow.StartNodeOutcomes.DeniedOutcome(null!),
                _ => throw new InvalidOperationException($"Unknown scenario '{scenario}'."),
            };
        });
    }
}
