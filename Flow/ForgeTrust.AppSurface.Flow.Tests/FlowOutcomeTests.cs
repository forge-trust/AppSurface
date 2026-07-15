namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowOutcomeTests
{
    [Fact]
    public void FlowRunStatus_ValuesAreStable()
    {
        Assert.Equal(0, (int)FlowRunStatus.Waiting);
        Assert.Equal(1, (int)FlowRunStatus.Completed);
        Assert.Equal(2, (int)FlowRunStatus.Faulted);
        Assert.Equal(3, (int)FlowRunStatus.TimedOut);
        Assert.Equal(4, (int)FlowRunStatus.ActivityPending);
    }

    [Fact]
    public void Factories_CreateExpectedOutcomeTypes()
    {
        var state = new TestState("ready");
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        Assert.IsType<FlowNext<TestState>>(FlowNodeOutcome<TestState>.Next("next", state));
        Assert.IsType<FlowWait<TestState>>(FlowNodeOutcome<TestState>.Wait("approved", state, timeout));
        Assert.IsType<FlowTimedOut<TestState>>(FlowNodeOutcome<TestState>.TimedOut("approved", state));
        Assert.IsType<FlowComplete<TestState>>(FlowNodeOutcome<TestState>.Complete(state));
        Assert.IsType<FlowFaultOutcome<TestState>>(FlowNodeOutcome<TestState>.Fault("approval.failed", "Approval failed."));
    }

    [Fact]
    public void Timeout_WithNonPositiveDuration_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void ResumeEventTimeout_MarksEventAsTimeout()
    {
        var resumeEvent = FlowResumeEvent.Timeout("approved");

        Assert.Equal("approved", resumeEvent.EventName);
        Assert.True(resumeEvent.IsTimeout);
    }

    [Fact]
    public void Fault_WithEmptyCode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FlowFault(" ", "message"));
    }

    [Fact]
    public void Fault_WithEmptyMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FlowFault("approval.failed", " "));
    }

    [Theory]
    [InlineData("next-node")]
    [InlineData("wait-event")]
    [InlineData("timed-out-event")]
    public void Factories_WithEmptyRequiredText_ThrowArgumentException(string scenario)
    {
        var state = new TestState("ready");

        Assert.Throws<ArgumentException>(() => scenario switch
        {
            "next-node" => FlowNodeOutcome<TestState>.Next(" ", state),
            "wait-event" => FlowNodeOutcome<TestState>.Wait(" ", state),
            "timed-out-event" => FlowNodeOutcome<TestState>.TimedOut(" ", state),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });
    }

    [Theory]
    [InlineData("next")]
    [InlineData("wait")]
    [InlineData("timed-out")]
    [InlineData("complete")]
    public void Factories_WithNullContext_ThrowArgumentNullException(string scenario)
    {
        Assert.Throws<ArgumentNullException>(() => scenario switch
        {
            "next" => FlowNodeOutcome<TestState>.Next("next", null!),
            "wait" => FlowNodeOutcome<TestState>.Wait("approved", null!),
            "timed-out" => FlowNodeOutcome<TestState>.TimedOut("approved", null!),
            "complete" => FlowNodeOutcome<TestState>.Complete(null!),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });
    }

    [Fact]
    public void FaultOutcome_WithNullFault_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FlowFaultOutcome<TestState>(null!));
    }

    [Fact]
    public void ActivityPendingResult_CapturesRequest()
    {
        var outcome = FlowNodeOutcome<TestState>.Activity(
            new FlowActivityCallsite<TestWork, TestResult>("send-email"),
            new TestWork("APR-1001"),
            new TestState("waiting"));

        var result = FlowRunResult<TestState>.ActivityPending("review", outcome);

        Assert.Equal(FlowRunStatus.ActivityPending, result.Status);
        Assert.Equal(outcome.CallsiteId, result.Activity?.CallsiteId);
        Assert.Same(outcome.Work, result.Activity?.Work);
        Assert.Same(outcome.Context, result.Context);
    }

    [Fact]
    public void ActivityPendingResult_WithNullRequest_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FlowRunResult<TestState>.ActivityPending("review", null!));
    }

    [Fact]
    public void ActivityPendingResult_SnapshotsExtensibleRequestMetadata()
    {
        var request = new MutableActivityRequest();

        var result = FlowRunResult<TestState>.ActivityPending("review", request);
        request.CallsiteId = "changed";
        request.WorkType = typeof(string);
        request.WorkContractVersion = 9;
        request.ResultType = typeof(string);
        request.ResultContractVersion = 10;
        request.Work = "changed";
        request.Context = new TestState("changed");

        Assert.Equal("send-email", result.Activity?.CallsiteId);
        Assert.Equal(typeof(TestWork), result.Activity?.WorkType);
        Assert.Equal(2, result.Activity?.WorkContractVersion);
        Assert.Equal(typeof(TestResult), result.Activity?.ResultType);
        Assert.Equal(3, result.Activity?.ResultContractVersion);
        Assert.Equal(new TestWork("APR-1001"), result.Activity?.Work);
        Assert.Equal(new TestState("waiting"), result.Context);
        var workResult = Assert.IsType<FlowActivityWorkResult<TestResult>>(
            result.Activity?.CreateResult(new TestResult("sent")));
        Assert.Equal("sent", workResult.Value.Status);
    }

    [Theory]
    [InlineData("callsite")]
    [InlineData("work-type")]
    [InlineData("work-version")]
    [InlineData("result-type")]
    [InlineData("result-version")]
    [InlineData("work")]
    [InlineData("work-mismatch")]
    [InlineData("context")]
    public void ActivityPendingResult_RejectsInvalidExtensibleRequest(string scenario)
    {
        var request = new MutableActivityRequest();
        switch (scenario)
        {
            case "callsite": request.CallsiteId = " "; break;
            case "work-type": request.WorkType = null!; break;
            case "work-version": request.WorkContractVersion = 0; break;
            case "result-type": request.ResultType = null!; break;
            case "result-version": request.ResultContractVersion = 0; break;
            case "work": request.Work = null!; break;
            case "work-mismatch": request.Work = "wrong"; break;
            case "context": request.Context = null!; break;
        }

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            FlowRunResult<TestState>.ActivityPending("review", request));

        Assert.Equal("activity", exception.ParamName);
        var expectedProperty = scenario switch
        {
            "work-type" => nameof(request.WorkType),
            "work-version" => nameof(request.WorkContractVersion),
            "result-type" => nameof(request.ResultType),
            "result-version" => nameof(request.ResultContractVersion),
            "work" or "work-mismatch" => nameof(request.Work),
            "context" => nameof(request.Context),
            _ => null,
        };
        if (expectedProperty is not null)
        {
            Assert.Contains(expectedProperty, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("input-type")]
    [InlineData("null-factory-result")]
    [InlineData("mismatched-factory-result")]
    [InlineData("substituted-factory-result")]
    public void ActivityPendingResult_CreateResultValidatesCapturedContract(string scenario)
    {
        var request = new MutableActivityRequest();
        if (scenario == "null-factory-result")
        {
            request.ResultFactory = _ => null!;
        }
        else if (scenario == "mismatched-factory-result")
        {
            request.ResultFactory = result =>
                new FlowActivityCallsite<TestWork, TestResult>("other", 2, 3).CreateResult((TestResult)result);
        }
        else if (scenario == "substituted-factory-result")
        {
            request.ResultFactory = _ =>
                new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3)
                    .CreateResult(new TestResult("substituted"));
        }

        var activity = FlowRunResult<TestState>.ActivityPending("review", request).Activity!;

        if (scenario == "input-type")
        {
            Assert.Throws<ArgumentException>(() => activity.CreateResult("wrong"));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => activity.CreateResult(new TestResult("sent")));
        }
    }

    [Fact]
    public void ActivityPendingResult_CreateResultPreservesDecodedValueType()
    {
        var request = new MutableActivityRequest
        {
            ResultType = typeof(int),
            ResultFactory = result =>
                new FlowActivityCallsite<TestWork, int>("send-email", 2, 3).CreateResult((int)result),
        };
        var activity = FlowRunResult<TestState>.ActivityPending("review", request).Activity!;

        var result = activity.CreateResult(42);

        Assert.Equal(42, result.Result);
    }

    private sealed record TestState(string Value);

    private sealed record TestWork(string Id);

    private sealed record TestResult(string Status);

    private sealed class MutableActivityRequest : IFlowActivityRequest<TestState>
    {
        public string CallsiteId { get; set; } = "send-email";

        public Type WorkType { get; set; } = typeof(TestWork);

        public int WorkContractVersion { get; set; } = 2;

        public Type ResultType { get; set; } = typeof(TestResult);

        public int ResultContractVersion { get; set; } = 3;

        public object Work { get; set; } = new TestWork("APR-1001");

        public TestState Context { get; set; } = new("waiting");

        public Func<object, FlowActivityWorkResult> ResultFactory { get; set; } = result =>
            new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3).CreateResult((TestResult)result);

        public FlowActivityWorkResult CreateResult(object result) => ResultFactory(result);
    }
}
