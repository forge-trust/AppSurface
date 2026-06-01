using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class InMemoryFlowRunnerTests
{
    [Fact]
    public async Task RunAsync_FollowsNextUntilComplete()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NextNode("finish"), "finish")
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build();

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Completed, result.Status);
        Assert.Equal("finish", result.NodeId);
        Assert.Equal(new TestState(1, "complete"), result.Context);
    }

    [Fact]
    public async Task RunAsync_StopsAtWait()
    {
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(10));
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new WaitNode(timeout))
            .StartAt("start")
            .Build();

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Waiting, result.Status);
        Assert.Equal("start", result.NodeId);
        Assert.Equal("approved", result.WaitingEventName);
        Assert.Same(timeout, result.Timeout);
    }

    [Fact]
    public async Task ResumeAsync_PassesResumeEventToNode()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new ResumeCompleteNode())
            .StartAt("start")
            .Build();

        var result = await Runner().ResumeAsync(
            definition,
            "start",
            new TestState(0, "waiting"),
            new FlowResumeEvent("approved", "andrew"));

        Assert.Equal(FlowRunStatus.Completed, result.Status);
        Assert.Equal(new TestState(0, "approved:andrew"), result.Context);
    }

    [Fact]
    public async Task RunAsync_ReturnsTimedOutResult()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new TimeoutNode())
            .StartAt("start")
            .Build();

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.TimedOut, result.Status);
        Assert.Equal("approved", result.TimedOutEventName);
    }

    [Fact]
    public async Task RunAsync_ReturnsFaultResult()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new FaultNode())
            .StartAt("start")
            .Build();

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Faulted, result.Status);
        Assert.Equal("approval.failed", result.Fault?.Code);
    }

    [Fact]
    public async Task RunAsync_WithUndeclaredNextTarget_ThrowsFlowDefinitionException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NextNode("finish"))
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build();

        var exception = await Assert.ThrowsAsync<FlowDefinitionException>(async () =>
            await Runner().RunAsync(definition, new TestState(0, "created")));

        Assert.Contains("undeclared target", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenMaxStepsExceeded_ReturnsFault()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("loop")
            .AddNode("start", new NextNode("start"), "start")
            .StartAt("start")
            .Build();

        var result = await Runner(maxSteps: 2).RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Faulted, result.Status);
        Assert.Equal("flow.max-steps-exceeded", result.Fault?.Code);
    }

    [Fact]
    public async Task RunAsync_WithInvalidMaxSteps_ThrowsInvalidOperationException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Runner(maxSteps: 0).RunAsync(definition, new TestState(0, "created")));
    }

    private static InMemoryFlowRunner<TestState> Runner(int maxSteps = 1000) =>
        new(Options.Create(new AppSurfaceFlowOptions { MaxStepsPerRun = maxSteps }));

    private sealed record TestState(int Count, string Status);

    private sealed class NextNode : IFlowNode<TestState>
    {
        private readonly string _target;

        internal NextNode(string target)
        {
            _target = target;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Next(_target, context.State with { Count = context.State.Count + 1 }));
    }

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State with { Status = "complete" }));
    }

    private sealed class WaitNode : IFlowNode<TestState>
    {
        private readonly FlowTimeout _timeout;

        internal WaitNode(FlowTimeout timeout)
        {
            _timeout = timeout;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Wait("approved", context.State with { Status = "waiting" }, _timeout));
    }

    private sealed class ResumeCompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default)
        {
            var payload = context.ResumeEvent?.Payload?.ToString() ?? "missing";
            return ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State with { Status = $"approved:{payload}" }));
        }
    }

    private sealed class TimeoutNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.TimedOut("approved", context.State with { Status = "timed-out" }));
    }

    private sealed class FaultNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."));
    }
}
