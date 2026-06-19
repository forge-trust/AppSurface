using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class InMemoryFlowRunnerTests
{
    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new InMemoryFlowRunner<TestState>(null!));
    }

    [Fact]
    public async Task RunAsync_WithNullDefinition_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Runner().RunAsync(null!, new TestState(0, "created")));
    }

    [Fact]
    public async Task RunAsync_WithNullContext_ThrowsArgumentNullException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Runner().RunAsync(definition, null!));
    }

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
    public async Task RunAsync_WithReentrantNextTarget_Completes()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new ReentrantNode(limit: 3), "start")
            .StartAt("start")
            .Build();

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Completed, result.Status);
        Assert.Equal("start", result.NodeId);
        Assert.Equal(new TestState(3, "complete"), result.Context);
    }

    [Fact]
    public async Task RunAsync_AfterSourceNodeDictionaryMutation_UsesCopiedDefinition()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new NextNode("finish"), "finish"),
            ["finish"] = Descriptor("finish", new CompleteNode()),
        };
        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nodes["start"] = Descriptor("start", new CompleteNode());
        nodes.Remove("finish");

        var result = await Runner().RunAsync(definition, new TestState(0, "created"));

        Assert.Equal(FlowRunStatus.Completed, result.Status);
        Assert.Equal("finish", result.NodeId);
        Assert.Equal(new TestState(1, "complete"), result.Context);
    }

    [Fact]
    public async Task RunAsync_AfterSourceNextNodeIdsMutation_RejectsUndeclaredRuntimeTarget()
    {
        var nextNodeIds = new HashSet<string>(StringComparer.Ordinal) { "finish" };
        var mutableNode = new MutableNextNode("finish");
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = new("start", mutableNode, nextNodeIds),
            ["finish"] = Descriptor("finish", new CompleteNode()),
        };
        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nodes["late"] = Descriptor("late", new CompleteNode());
        nextNodeIds.Add("late");
        mutableNode.Target = "late";

        var exception = await Assert.ThrowsAsync<FlowDefinitionException>(async () =>
            await Runner().RunAsync(definition, new TestState(0, "created")));

        Assert.Contains("undeclared target", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task ResumeAsync_WithValidNonStartNode_RunsThatNode()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new WaitNode(new FlowTimeout(TimeSpan.FromMinutes(10))))
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build();

        var result = await Runner().ResumeAsync(
            definition,
            "finish",
            new TestState(0, "waiting"),
            new FlowResumeEvent("approved", "andrew"));

        Assert.Equal(FlowRunStatus.Completed, result.Status);
        Assert.Equal("finish", result.NodeId);
        Assert.Equal(new TestState(0, "complete"), result.Context);
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
    public async Task RunAsync_WithCanceledToken_ThrowsOperationCanceledException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Runner().RunAsync(definition, new TestState(0, "created"), source.Token));
    }

    [Fact]
    public async Task RunAsync_WhenNodeReturnsNull_ThrowsArgumentNullException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NullOutcomeNode())
            .StartAt("start")
            .Build();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Runner().RunAsync(definition, new TestState(0, "created")));
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
    public async Task ResumeAsync_WithNullDefinition_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Runner().ResumeAsync(null!, "start", new TestState(0, "waiting"), new FlowResumeEvent("approved")));
    }

    [Fact]
    public async Task ResumeAsync_WithNullResumeEvent_ThrowsArgumentNullException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Runner().ResumeAsync(definition, "start", new TestState(0, "waiting"), null!));
    }

    [Fact]
    public async Task ResumeAsync_WithEmptyNodeId_ThrowsArgumentException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Runner().ResumeAsync(definition, " ", new TestState(0, "waiting"), new FlowResumeEvent("approved")));
    }

    [Fact]
    public async Task ResumeAsync_WithMissingNode_ThrowsFlowDefinitionException()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start")
            .Build();

        var exception = await Assert.ThrowsAsync<FlowDefinitionException>(async () =>
            await Runner().ResumeAsync(definition, "missing", new TestState(0, "waiting"), new FlowResumeEvent("approved")));

        Assert.Contains("does not contain node 'missing'", exception.Message, StringComparison.Ordinal);
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

    private static FlowNodeDescriptor<TestState> Descriptor(
        string nodeId,
        IFlowNode<TestState> node,
        params string[] nextNodeIds) =>
        new(nodeId, node, new HashSet<string>(nextNodeIds, StringComparer.Ordinal));

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

    private sealed class MutableNextNode : IFlowNode<TestState>
    {
        internal MutableNextNode(string target)
        {
            Target = target;
        }

        internal string Target { get; set; }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Next(Target, context.State with { Count = context.State.Count + 1 }));
    }

    private sealed class ReentrantNode : IFlowNode<TestState>
    {
        private readonly int _limit;

        internal ReentrantNode(int limit)
        {
            _limit = limit;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default)
        {
            if (context.State.Count >= _limit)
            {
                return ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                    FlowNodeOutcome<TestState>.Complete(context.State with { Status = "complete" }));
            }

            return ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Next("start", context.State with { Count = context.State.Count + 1 }));
        }
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

    private sealed class NullOutcomeNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(null!);
    }
}
