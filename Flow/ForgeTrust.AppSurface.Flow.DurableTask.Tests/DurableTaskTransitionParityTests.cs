using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskTransitionParityTests
{
    public static TheoryData<FlowNodeOutcome<TestState>, FlowTransitionKind, DurableTaskFlowDecisionKind> Cases =>
        new()
        {
            {
                FlowNodeOutcome<TestState>.Next("finish", new TestState("next")),
                FlowTransitionKind.Next,
                DurableTaskFlowDecisionKind.ScheduleNode
            },
            {
                FlowNodeOutcome<TestState>.Wait(
                    new FlowEventCallsite<TestEvent>("approved", "approval.submitted", "v1"),
                    new TestState("waiting"),
                    new FlowTimeout(TimeSpan.FromMinutes(1))),
                FlowTransitionKind.Wait,
                DurableTaskFlowDecisionKind.WaitForExternalEvent
            },
            {
                FlowNodeOutcome<TestState>.TimedOut("approved", new TestState("timed-out")),
                FlowTransitionKind.TimedOut,
                DurableTaskFlowDecisionKind.TimedOut
            },
            {
                FlowNodeOutcome<TestState>.Complete(new TestState("complete")),
                FlowTransitionKind.Complete,
                DurableTaskFlowDecisionKind.Complete
            },
            {
                FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."),
                FlowTransitionKind.Fault,
                DurableTaskFlowDecisionKind.Fault
            },
            {
                FlowNodeOutcome<TestState>.Activity(
                    new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3),
                    new TestWork("APR-1001"),
                    new TestState("activity-pending")),
                FlowTransitionKind.Activity,
                DurableTaskFlowDecisionKind.ScheduleActivity
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DurableTaskDecision_PreservesSharedTransitionSemantics(
        FlowNodeOutcome<TestState> outcome,
        FlowTransitionKind expectedTransitionKind,
        DurableTaskFlowDecisionKind expectedDecisionKind)
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(outcome), "finish")
            .AddNode("finish", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("finish"))))
            .StartAt("start")
            .Build();
        var evaluator = new CountingEvaluator<TestState>(new FlowTransitionEvaluator<TestState>());
        var transition = await evaluator.EvaluateAsync(
            definition,
            new FlowTransitionInput<TestState>("start", new TestState("created")));
        var registry = new FlowDefinitionRegistry();
        registry.Register(definition);
        var runner = new DurableTaskFlowRunner<TestState>(
            registry,
            new FlowContextSerializationValidator(new SystemTextJsonFlowContextSerializer()),
            Options.Create(new AppSurfaceFlowDurableTaskOptions()),
            evaluator);

        var decision = await runner.RunNodeAsync(
            new DurableTaskFlowStep<TestState>(
                "approval",
                "1",
                "instance-1",
                "start",
                new TestState("created")));

        Assert.Equal(expectedTransitionKind, transition.Kind);
        Assert.Equal(expectedDecisionKind, decision.Kind);
        Assert.Equal(2, evaluator.EvaluationCount);
        Assert.Equal(transition.Context, decision.Context);
        Assert.Equal(transition.NextNodeId, expectedDecisionKind == DurableTaskFlowDecisionKind.ScheduleNode ? decision.NodeId : null);
        Assert.Equal(transition.EventName, decision.EventName);
        Assert.Equal(transition.EventCallsite, decision.EventCallsite);
        Assert.Equal(transition.Timeout, decision.Timeout);
        Assert.Equal(transition.Fault, decision.Fault);
        Assert.Equal(transition.Activity, decision.Activity);
    }

    public sealed record TestState(string Status);

    public sealed record TestWork(string ApprovalId);

    public sealed record TestResult(string Status);

    public sealed record TestEvent(string ApprovedBy);

    private sealed class OutcomeNode : IFlowNode<TestState>
    {
        private readonly FlowNodeOutcome<TestState> _outcome;

        internal OutcomeNode(FlowNodeOutcome<TestState> outcome)
        {
            _outcome = outcome;
        }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_outcome);
    }

    private sealed class CountingEvaluator<TContext> : IFlowTransitionEvaluator<TContext>
    {
        private readonly IFlowTransitionEvaluator<TContext> _inner;

        internal CountingEvaluator(IFlowTransitionEvaluator<TContext> inner)
        {
            _inner = inner;
        }

        internal int EvaluationCount { get; private set; }

        public string EvaluatorId => _inner.EvaluatorId;

        public string EvaluatorVersion => _inner.EvaluatorVersion;

        public ValueTask<FlowTransition<TContext>> EvaluateAsync(
            FlowDefinition<TContext> definition,
            FlowTransitionInput<TContext> input,
            CancellationToken cancellationToken = default)
        {
            EvaluationCount++;
            return _inner.EvaluateAsync(definition, input, cancellationToken);
        }
    }
}
