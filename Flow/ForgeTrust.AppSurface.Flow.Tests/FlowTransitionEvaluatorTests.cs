namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowTransitionEvaluatorTests
{
    [Fact]
    public void FlowTransitionKind_ValuesAreStableAndActivityIsAppended()
    {
        Assert.Equal(0, (int)FlowTransitionKind.Next);
        Assert.Equal(1, (int)FlowTransitionKind.Wait);
        Assert.Equal(2, (int)FlowTransitionKind.TimedOut);
        Assert.Equal(3, (int)FlowTransitionKind.Complete);
        Assert.Equal(4, (int)FlowTransitionKind.Fault);
        Assert.Equal(5, (int)FlowTransitionKind.Activity);
    }

    [Fact]
    public void Input_CapturesNodeContextAndExternalResume()
    {
        var state = new TestState("waiting");
        var resumeEvent = new FlowResumeEvent("approved", "andrew");

        var input = new FlowTransitionInput<TestState>("review", state, resumeEvent);

        Assert.Equal("review", input.NodeId);
        Assert.Same(state, input.Context);
        Assert.Same(resumeEvent, input.ResumeEvent);
        Assert.Null(input.ActivityResult);
    }

    [Fact]
    public void Input_CapturesActivityResult()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");
        var result = callsite.CreateResult(new TestResult("sent"));

        var input = new FlowTransitionInput<TestState>(
            "review",
            new TestState("waiting"),
            activityResult: result);

        Assert.Same(result, input.ActivityResult);
        Assert.Null(input.ResumeEvent);
    }

    [Fact]
    public void Input_WithBothResumeForms_ThrowsArgumentException()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");

        var exception = Assert.Throws<ArgumentException>(() => new FlowTransitionInput<TestState>(
            "review",
            new TestState("waiting"),
            new FlowResumeEvent("approved"),
            callsite.CreateResult(new TestResult("sent"))));

        Assert.Equal("activityResult", exception.ParamName);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("context")]
    public void Input_WithInvalidRequiredValue_Throws(string scenario)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => scenario switch
        {
            "node" => new FlowTransitionInput<TestState>(" ", new TestState("waiting")),
            "context" => new FlowTransitionInput<TestState>("review", null!),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Fact]
    public async Task EvaluateAsync_WithNullDefinitionOrInput_ThrowsArgumentNullException()
    {
        var evaluator = new FlowTransitionEvaluator<TestState>();
        var input = new FlowTransitionInput<TestState>("start", new TestState("created"));
        var definition = Definition(new OutcomeNode(FlowNodeOutcome<TestState>.Complete(input.Context)));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await evaluator.EvaluateAsync(null!, input));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await evaluator.EvaluateAsync(definition, null!));
    }

    [Fact]
    public async Task EvaluateAsync_WithMissingNode_ReturnsFaultTransition()
    {
        var transition = await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
            Definition(new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("complete")))),
            new FlowTransitionInput<TestState>("missing", new TestState("created")));

        Assert.Equal(FlowTransitionKind.Fault, transition.Kind);
        Assert.Equal("missing", transition.NodeId);
        Assert.Equal("flow.node-missing", transition.Fault?.Code);
        Assert.Null(transition.Context);
    }

    [Fact]
    public async Task EvaluateAsync_MapsDeclaredNextWithoutExecutingTarget()
    {
        var target = new TrackingNode(FlowNodeOutcome<TestState>.Complete(new TestState("unexpected")));
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Next("finish", new TestState("next"))), "finish")
            .AddNode("finish", target)
            .StartAt("start")
            .Build();

        var transition = await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
            definition,
            new FlowTransitionInput<TestState>("start", new TestState("created")));

        Assert.Equal(FlowTransitionKind.Next, transition.Kind);
        Assert.Equal("start", transition.NodeId);
        Assert.Equal("finish", transition.NextNodeId);
        Assert.Equal(new TestState("next"), transition.Context);
        Assert.Equal(0, target.ExecutionCount);
    }

    [Fact]
    public async Task EvaluateAsync_WithUndeclaredNext_ReturnsStableFault()
    {
        var definition = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new OutcomeNode(FlowNodeOutcome<TestState>.Next("finish", new TestState("next"))))
            .AddNode("finish", new OutcomeNode(FlowNodeOutcome<TestState>.Complete(new TestState("complete"))))
            .StartAt("start")
            .Build();

        var transition = await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
            definition,
            new FlowTransitionInput<TestState>("start", new TestState("created")));

        Assert.Equal(FlowTransitionKind.Fault, transition.Kind);
        Assert.Equal("flow.next-node-invalid", transition.Fault?.Code);
        Assert.Null(transition.NextNodeId);
    }

    [Fact]
    public async Task EvaluateAsync_MapsWaitWithTimeout()
    {
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        var transition = await Evaluate(
            FlowNodeOutcome<TestState>.Wait("approved", new TestState("waiting"), timeout));

        Assert.Equal(FlowTransitionKind.Wait, transition.Kind);
        Assert.Equal("approved", transition.EventName);
        Assert.Equal(new TestState("waiting"), transition.Context);
        Assert.Same(timeout, transition.Timeout);
    }

    [Fact]
    public async Task EvaluateAsync_MapsTypedWaitContractMetadata()
    {
        var callsite = new FlowEventCallsite<TestEvent>("approved", "approval.submitted", "v1");

        var transition = await Evaluate(
            FlowNodeOutcome<TestState>.Wait(callsite, new TestState("waiting")));

        Assert.Equal(FlowTransitionKind.Wait, transition.Kind);
        Assert.Equal(callsite.EventName, transition.EventName);
        Assert.NotSame(callsite, transition.EventCallsite);
        Assert.Equal(typeof(TestEvent), transition.EventCallsite?.PayloadType);
        Assert.Equal(callsite.ContractName, transition.EventCallsite?.ContractName);
        Assert.Equal(callsite.ContractVersion, transition.EventCallsite?.ContractVersion);
    }

    [Fact]
    public async Task EvaluateAsync_MapsTimedOutCompleteAndFault()
    {
        var timedOut = await Evaluate(
            FlowNodeOutcome<TestState>.TimedOut("approved", new TestState("timed-out")));
        var completed = await Evaluate(
            FlowNodeOutcome<TestState>.Complete(new TestState("complete")));
        var faulted = await Evaluate(
            FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."));

        Assert.Equal(FlowTransitionKind.TimedOut, timedOut.Kind);
        Assert.Equal("approved", timedOut.EventName);
        Assert.Equal(new TestState("timed-out"), timedOut.Context);
        Assert.Equal(FlowTransitionKind.Complete, completed.Kind);
        Assert.Equal(new TestState("complete"), completed.Context);
        Assert.Equal(FlowTransitionKind.Fault, faulted.Kind);
        Assert.Equal("approval.failed", faulted.Fault?.Code);
    }

    [Fact]
    public async Task EvaluateAsync_WithUnsupportedOutcome_ReturnsStableFault()
    {
        var transition = await Evaluate(new UnsupportedOutcome());

        Assert.Equal(FlowTransitionKind.Fault, transition.Kind);
        Assert.Equal("flow.outcome-unsupported", transition.Fault?.Code);
        Assert.Contains(typeof(UnsupportedOutcome).FullName!, transition.Fault?.Message, StringComparison.Ordinal);
        Assert.Null(transition.Context);
    }

    [Fact]
    public async Task EvaluateAsync_MapsActivityWithReflectionFreeMetadata()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3);
        var work = new TestWork("APR-1001");

        var transition = await Evaluate(
            FlowNodeOutcome<TestState>.Activity(callsite, work, new TestState("activity-pending")));

        Assert.Equal(FlowTransitionKind.Activity, transition.Kind);
        Assert.Equal(new TestState("activity-pending"), transition.Context);
        Assert.Equal("send-email", transition.Activity?.CallsiteId);
        Assert.Equal(typeof(TestWork), transition.Activity?.WorkType);
        Assert.Equal(typeof(TestResult), transition.Activity?.ResultType);
        Assert.Same(work, transition.Activity?.Work);
    }

    [Fact]
    public async Task EvaluateAsync_PassesExactlyOneResumeInputToNode()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");
        var capture = new CaptureContextNode();
        var definition = Definition(capture);
        var evaluator = new FlowTransitionEvaluator<TestState>();
        var resumeEvent = new FlowResumeEvent("approved", "andrew");

        await evaluator.EvaluateAsync(
            definition,
            new FlowTransitionInput<TestState>("start", new TestState("waiting"), resumeEvent));
        Assert.Same(resumeEvent, capture.LastContext.ResumeEvent);
        Assert.Null(capture.LastContext.ActivityResult);

        var activityResult = callsite.CreateResult(new TestResult("sent"));
        await evaluator.EvaluateAsync(
            definition,
            new FlowTransitionInput<TestState>(
                "start",
                new TestState("waiting"),
                activityResult: activityResult));
        Assert.Null(capture.LastContext.ResumeEvent);
        Assert.Same(activityResult, capture.LastContext.ActivityResult);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNodeReturnsNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
                Definition(new NullNode()),
                new FlowTransitionInput<TestState>("start", new TestState("created"))));
    }

    [Fact]
    public async Task EvaluateAsync_WhenCanceledBeforeExecution_DoesNotInvokeNode()
    {
        var node = new TrackingNode(FlowNodeOutcome<TestState>.Complete(new TestState("complete")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
                Definition(node),
                new FlowTransitionInput<TestState>("start", new TestState("created")),
                source.Token));

        Assert.Equal(0, node.ExecutionCount);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNodeThrows_PropagatesException()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
                Definition(new ThrowingNode()),
                new FlowTransitionInput<TestState>("start", new TestState("created"))));

        Assert.Equal("node failed", exception.Message);
    }

    private static async ValueTask<FlowTransition<TestState>> Evaluate(FlowNodeOutcome<TestState> outcome) =>
        await new FlowTransitionEvaluator<TestState>().EvaluateAsync(
            Definition(new OutcomeNode(outcome)),
            new FlowTransitionInput<TestState>("start", new TestState("created")));

    private static FlowDefinition<TestState> Definition(IFlowNode<TestState> node) =>
        FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", node)
            .StartAt("start")
            .Build();

    private sealed record TestState(string Status);

    private sealed record TestWork(string ApprovalId);

    private sealed record TestResult(string Status);

    private sealed record TestEvent(string ApprovedBy);

    private sealed record UnsupportedOutcome : FlowNodeOutcome<TestState>;

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

    private sealed class TrackingNode : IFlowNode<TestState>
    {
        private readonly FlowNodeOutcome<TestState> _outcome;

        internal TrackingNode(FlowNodeOutcome<TestState> outcome)
        {
            _outcome = outcome;
        }

        internal int ExecutionCount { get; private set; }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(_outcome);
        }
    }

    private sealed class CaptureContextNode : IFlowNode<TestState>
    {
        internal FlowExecutionContext<TestState> LastContext { get; private set; }

        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State));
        }
    }

    private sealed class NullNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(null!);
    }

    private sealed class ThrowingNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("node failed");
    }
}
