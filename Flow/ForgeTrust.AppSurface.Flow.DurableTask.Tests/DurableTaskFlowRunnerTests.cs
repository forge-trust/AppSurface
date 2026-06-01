using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskFlowRunnerTests
{
    [Fact]
    public async Task StartAsync_WithMissingDefinition_ReturnsFaultDecision()
    {
        var decision = await Runner().StartAsync("missing", "1", "instance-1", new TestState("created"));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.definition-missing", decision.Fault?.Code);
    }

    [Fact]
    public async Task RunNodeAsync_WithMissingNode_ReturnsFaultDecision()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new WaitNode(), "review"));

        var decision = await Runner(registry).RunNodeAsync(
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "missing", new TestState("created")));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.node-missing", decision.Fault?.Code);
    }

    [Fact]
    public async Task RunNodeAsync_WithMissingDefinitionAndBadContext_ReturnsMissingDefinition()
    {
        var runner = new DurableTaskFlowRunner<NonSerializableState>(
            new FlowDefinitionRegistry(),
            new FlowContextSerializationValidator(new ThrowingSerializer()),
            Options.Create(new AppSurfaceFlowDurableTaskOptions()));

        var decision = await runner.RunNodeAsync(
            new DurableTaskFlowStep<NonSerializableState>("missing", "1", "instance-1", "start", new NonSerializableState()));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.definition-missing", decision.Fault?.Code);
    }

    [Fact]
    public async Task RunNodeAsync_WithMissingNodeAndBadContext_ReturnsMissingNode()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(FlowGraphBuilder<NonSerializableState>
            .Create("approval")
            .AddNode("start", new NonSerializableCompleteNode())
            .StartAt("start")
            .Build());
        var runner = new DurableTaskFlowRunner<NonSerializableState>(
            registry,
            new FlowContextSerializationValidator(new ThrowingSerializer()),
            Options.Create(new AppSurfaceFlowDurableTaskOptions()));

        var decision = await runner.RunNodeAsync(
            new DurableTaskFlowStep<NonSerializableState>("approval", "1", "instance-1", "missing", new NonSerializableState()));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.node-missing", decision.Fault?.Code);
    }

    [Fact]
    public async Task RunNodeAsync_MapsNextOutcomeToScheduleDecision()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NextNode("finish"), "finish")
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build());

        var decision = await Runner(registry).StartAsync("approval", "1", "instance-1", new TestState("created"));

        Assert.Equal(DurableTaskFlowDecisionKind.ScheduleNode, decision.Kind);
        Assert.Equal("finish", decision.NodeId);
        Assert.Equal(new TestState("next"), decision.Context);
    }

    [Fact]
    public async Task RunNodeAsync_AttachesConfiguredRetryPolicyToScheduleDecision()
    {
        var retryPolicy = new FlowRetryPolicy(3, TimeSpan.FromSeconds(1), 2);
        var registry = new FlowDefinitionRegistry();
        registry.Register(FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NextNode("finish"), "finish")
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build());

        var decision = await Runner(
                registry,
                new AppSurfaceFlowDurableTaskOptions { NodeRetryPolicy = retryPolicy })
            .StartAsync("approval", "1", "instance-1", new TestState("created"));

        Assert.Same(retryPolicy, decision.RetryPolicy);
    }

    [Fact]
    public async Task RunNodeAsync_MapsWaitOutcomeToExternalEventDecisionWithTimeout()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new WaitNode(), "review"));

        var decision = await Runner(registry).RunNodeAsync(
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "review", new TestState("created")));

        Assert.Equal(DurableTaskFlowDecisionKind.WaitForExternalEvent, decision.Kind);
        Assert.Equal("approved", decision.EventName);
        Assert.Equal(TimeSpan.FromMinutes(2), decision.Timeout?.Duration);
    }

    [Fact]
    public async Task RunNodeAsync_MapsCompleteFaultAndTimeoutOutcomes()
    {
        await AssertDecision(new CompleteNode(), DurableTaskFlowDecisionKind.Complete, null);
        await AssertDecision(new FaultNode(), DurableTaskFlowDecisionKind.Fault, "approval.failed");
        await AssertDecision(new TimeoutNode(), DurableTaskFlowDecisionKind.TimedOut, null);
    }

    [Fact]
    public async Task RunNodeAsync_WithInvalidNextTarget_ReturnsFaultDecision()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new NextNode("finish"))
            .AddNode("finish", new CompleteNode())
            .StartAt("start")
            .Build());

        var decision = await Runner(registry).RunNodeAsync(
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "start", new TestState("created")));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.next-node-invalid", decision.Fault?.Code);
    }

    [Fact]
    public async Task ResumeAsync_WithMismatchedEvent_IgnoresLateEventByDefault()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new CompleteNode(), "review"));
        var step = new DurableTaskFlowStep<TestState>(
            "approval",
            "1",
            "instance-1",
            "review",
            new TestState("waiting"),
            new FlowResumeEvent("denied"));

        var decision = await Runner(registry).ResumeAsync(step, "approved");

        Assert.Equal(DurableTaskFlowDecisionKind.IgnoreLateEvent, decision.Kind);
        Assert.Equal("denied", decision.EventName);
    }

    [Fact]
    public async Task ResumeAsync_WithMismatchedEvent_CanFault()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new CompleteNode(), "review"));
        var step = new DurableTaskFlowStep<TestState>(
            "approval",
            "1",
            "instance-1",
            "review",
            new TestState("waiting"),
            new FlowResumeEvent("denied"));

        var decision = await Runner(
                registry,
                new AppSurfaceFlowDurableTaskOptions { IgnoreLateResumeEvents = false })
            .ResumeAsync(step, "approved");

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.resume-event-late", decision.Fault?.Code);
    }

    [Fact]
    public async Task ResumeAsync_WithExpectedEvent_RunsNode()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new CompleteNode(), "review"));
        var step = new DurableTaskFlowStep<TestState>(
            "approval",
            "1",
            "instance-1",
            "review",
            new TestState("waiting"),
            new FlowResumeEvent("approved"));

        var decision = await Runner(registry).ResumeAsync(step, "approved");

        Assert.Equal(DurableTaskFlowDecisionKind.Complete, decision.Kind);
    }

    [Fact]
    public async Task RunNodeAsync_WithNonSerializableContext_ReturnsFaultDecision()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(FlowGraphBuilder<NonSerializableState>
            .Create("approval")
            .AddNode("start", new NonSerializableCompleteNode())
            .StartAt("start")
            .Build());
        var runner = new DurableTaskFlowRunner<NonSerializableState>(
            registry,
            new FlowContextSerializationValidator(new ThrowingSerializer()),
            Options.Create(new AppSurfaceFlowDurableTaskOptions()));

        var decision = await runner.RunNodeAsync(
            new DurableTaskFlowStep<NonSerializableState>("approval", "1", "instance-1", "start", new NonSerializableState()));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.context-not-durable", decision.Fault?.Code);
    }

    [Theory]
    [InlineData(NonDurableOutcomeNode.Next)]
    [InlineData(NonDurableOutcomeNode.Wait)]
    [InlineData(NonDurableOutcomeNode.TimedOut)]
    [InlineData(NonDurableOutcomeNode.Complete)]
    public async Task RunNodeAsync_WithNonSerializableOutcomeContext_ReturnsFaultDecision(string outcomeKind)
    {
        var registry = new FlowDefinitionRegistry();
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("review", new NonDurableOutcomeNode(outcomeKind), "finish")
            .AddNode("finish", new CompleteNode())
            .StartAt("review");
        registry.Register(builder.Build());

        var decision = await Runner(registry, serializer: new StatusSensitiveSerializer()).RunNodeAsync(
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "review", new TestState("created")));

        Assert.Equal(DurableTaskFlowDecisionKind.Fault, decision.Kind);
        Assert.Equal("flow.context-not-durable", decision.Fault?.Code);
    }

    [Fact]
    public async Task RunNodeAsync_CanSkipSerializationValidation()
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(new CompleteNode(), "review"));

        var decision = await Runner(
                registry,
                new AppSurfaceFlowDurableTaskOptions { ValidateContextSerialization = false },
                new ThrowingSerializer())
            .RunNodeAsync(new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "review", new TestState("created")));

        Assert.Equal(DurableTaskFlowDecisionKind.Complete, decision.Kind);
    }

    private static async Task AssertDecision(
        IFlowNode<TestState> node,
        DurableTaskFlowDecisionKind expectedKind,
        string? expectedFaultCode)
    {
        var registry = new FlowDefinitionRegistry();
        registry.Register(Definition(node, "review"));

        var decision = await Runner(registry).RunNodeAsync(
            new DurableTaskFlowStep<TestState>("approval", "1", "instance-1", "review", new TestState("created")));

        Assert.Equal(expectedKind, decision.Kind);
        if (expectedFaultCode is not null)
        {
            Assert.Equal(expectedFaultCode, decision.Fault?.Code);
        }
    }

    private static DurableTaskFlowRunner<TestState> Runner(
        FlowDefinitionRegistry? registry = null,
        AppSurfaceFlowDurableTaskOptions? options = null,
        IFlowContextSerializer? serializer = null) =>
        new(
            registry ?? new FlowDefinitionRegistry(),
            new FlowContextSerializationValidator(serializer ?? new SystemTextJsonFlowContextSerializer()),
            Options.Create(options ?? new AppSurfaceFlowDurableTaskOptions()));

    private static FlowDefinition<TestState> Definition(IFlowNode<TestState> node, string nodeId) =>
        FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode(nodeId, node)
            .StartAt(nodeId)
            .Build();

    private sealed record TestState(string Status);

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
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(FlowNodeOutcome<TestState>.Next(_target, new TestState("next")));
    }

    private sealed class WaitNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Wait("approved", new TestState("waiting"), new FlowTimeout(TimeSpan.FromMinutes(2))));
    }

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(FlowNodeOutcome<TestState>.Complete(new TestState("complete")));
    }

    private sealed class FaultNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."));
    }

    private sealed class TimeoutNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(FlowNodeOutcome<TestState>.TimedOut("approved", new TestState("timeout")));
    }

    private sealed class NonDurableOutcomeNode : IFlowNode<TestState>
    {
        internal const string Complete = "complete";
        internal const string Next = "next";
        internal const string TimedOut = "timed-out";
        internal const string Wait = "wait";

        private readonly string _outcomeKind;

        internal NonDurableOutcomeNode(string outcomeKind)
        {
            _outcomeKind = outcomeKind;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default)
        {
            var state = new TestState("non-durable");
            FlowNodeOutcome<TestState> outcome = _outcomeKind switch
            {
                Next => FlowNodeOutcome<TestState>.Next("finish", state),
                Wait => FlowNodeOutcome<TestState>.Wait("approved", state),
                TimedOut => FlowNodeOutcome<TestState>.TimedOut("approved", state),
                Complete => FlowNodeOutcome<TestState>.Complete(state),
                _ => FlowNodeOutcome<TestState>.Fault("test.invalid-outcome", "Invalid test outcome."),
            };

            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingSerializer : IFlowContextSerializer
    {
        public string Serialize<TContext>(TContext context) => throw new InvalidOperationException("Not durable.");

        public TContext Deserialize<TContext>(string payload) => throw new InvalidOperationException("Not durable.");
    }

    private sealed class StatusSensitiveSerializer : IFlowContextSerializer
    {
        public string Serialize<TContext>(TContext context)
        {
            if (context is TestState { Status: "non-durable" })
            {
                throw new InvalidOperationException("Returned context is not durable.");
            }

            return JsonSerializer.Serialize(context);
        }

        public TContext Deserialize<TContext>(string payload)
        {
            var context = JsonSerializer.Deserialize<TContext>(payload);
            return context ?? throw new JsonException("Payload did not contain a context value.");
        }
    }

    private sealed class NonSerializableState
    {
    }

    private sealed class NonSerializableCompleteNode : IFlowNode<NonSerializableState>
    {
        public ValueTask<FlowNodeOutcome<NonSerializableState>> ExecuteAsync(
            FlowExecutionContext<NonSerializableState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<NonSerializableState>>(FlowNodeOutcome<NonSerializableState>.Complete(context.State));
    }
}
