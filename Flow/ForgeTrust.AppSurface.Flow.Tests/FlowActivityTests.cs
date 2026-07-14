namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowActivityTests
{
    [Fact]
    public void FlowRunStatus_ActivityValueIsAppended()
    {
        Assert.Equal(4, (int)FlowRunStatus.ActivityPending);
    }

    [Fact]
    public void FlowOutcomeKind_ActivityValueIsAppended()
    {
        Assert.Equal(5, (int)FlowOutcomeKind.Activity);
    }

    [Fact]
    public void Callsite_CapturesStableContractMetadata()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3);

        Assert.Equal("send-email", callsite.CallsiteId);
        Assert.Equal(2, callsite.WorkContractVersion);
        Assert.Equal(3, callsite.ResultContractVersion);
    }

    [Fact]
    public void ActivityFactory_ExposesTypedAndUntypedRequestMetadata()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3);
        var work = new TestWork("APR-1001");
        var state = new TestState("waiting");

        var outcome = FlowNodeOutcome<TestState>.Activity(callsite, work, state);
        var request = Assert.IsAssignableFrom<IFlowActivityRequest<TestState>>(outcome);

        Assert.Same(callsite, outcome.Callsite);
        Assert.Same(work, outcome.Work);
        Assert.Same(state, outcome.Context);
        Assert.Equal("send-email", request.CallsiteId);
        Assert.Equal(typeof(TestWork), request.WorkType);
        Assert.Equal(2, request.WorkContractVersion);
        Assert.Equal(typeof(TestResult), request.ResultType);
        Assert.Equal(3, request.ResultContractVersion);
        Assert.Same(work, request.Work);
        Assert.Same(state, request.Context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Callsite_WithNonPositiveWorkVersion_ThrowsArgumentOutOfRangeException(int version)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowActivityCallsite<TestWork, TestResult>("send-email", version, 1));

        Assert.Equal("workContractVersion", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Callsite_WithNonPositiveResultVersion_ThrowsArgumentOutOfRangeException(int version)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowActivityCallsite<TestWork, TestResult>("send-email", 1, version));

        Assert.Equal("resultContractVersion", exception.ParamName);
    }

    [Fact]
    public void Callsite_WithEmptyId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new FlowActivityCallsite<TestWork, TestResult>(" "));
    }

    [Theory]
    [InlineData("callsite")]
    [InlineData("work")]
    [InlineData("context")]
    public void Activity_WithNullRequiredValue_ThrowsArgumentNullException(string value)
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");

        var exception = Assert.Throws<ArgumentNullException>(() => value switch
        {
            "callsite" => FlowNodeOutcome<TestState>.Activity<TestWork, TestResult>(
                null!,
                new TestWork("APR-1001"),
                new TestState("waiting")),
            "work" => FlowNodeOutcome<TestState>.Activity<TestWork, TestResult>(
                callsite,
                null!,
                new TestState("waiting")),
            "context" => FlowNodeOutcome<TestState>.Activity<TestWork, TestResult>(
                callsite,
                new TestWork("APR-1001"),
                null!),
            _ => throw new InvalidOperationException("Unknown null scenario."),
        });

        Assert.Equal(value, exception.ParamName);
    }

    [Fact]
    public void CreateResult_ExposesTypedAndUntypedMetadata()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3);
        var value = new TestResult("provider-123");

        var result = callsite.CreateResult(value);

        Assert.Equal("send-email", result.CallsiteId);
        Assert.Equal(typeof(TestResult), result.ResultType);
        Assert.Equal(3, result.ResultContractVersion);
        Assert.Same(value, result.Value);
        Assert.Same(value, result.Result);
    }

    [Fact]
    public void UntypedRequest_CreateResultBridgesRegisteredCodecWithoutReflection()
    {
        IFlowActivityRequest<TestState> request = FlowNodeOutcome<TestState>.Activity(
            new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3),
            new TestWork("APR-1001"),
            new TestState("waiting"));
        var decoded = new TestResult("provider-123");

        var result = request.CreateResult(decoded);

        var typed = Assert.IsType<FlowActivityWorkResult<TestResult>>(result);
        Assert.Same(decoded, typed.Value);
        Assert.Equal("send-email", typed.CallsiteId);
        Assert.Equal(3, typed.ResultContractVersion);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-type")]
    public void UntypedRequest_CreateResultRejectsInvalidDecodedValue(string scenario)
    {
        IFlowActivityRequest<TestState> request = FlowNodeOutcome<TestState>.Activity(
            new FlowActivityCallsite<TestWork, TestResult>("send-email"),
            new TestWork("APR-1001"),
            new TestState("waiting"));

        Assert.ThrowsAny<ArgumentException>(() => request.CreateResult(
            scenario == "null" ? null! : new OtherResult("wrong")));
    }

    [Fact]
    public void CreateResult_WithNullValue_ThrowsArgumentNullException()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email");

        Assert.Throws<ArgumentNullException>(() => callsite.CreateResult(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkResult_WithNonPositiveResultVersion_ThrowsArgumentOutOfRangeException(int version)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowActivityWorkResult<TestResult>("send-email", version, new TestResult("provider-123")));

        Assert.Equal("resultContractVersion", exception.ParamName);
    }

    [Fact]
    public void TryGetResult_WithExactCallsiteTypeAndVersion_ReturnsTypedResult()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 2, 3);
        var expected = new TestResult("provider-123");

        var matched = callsite.TryGetResult(callsite.CreateResult(expected), out var actual);

        Assert.True(matched);
        Assert.Same(expected, actual);
        Assert.Same(expected, callsite.GetResult(callsite.CreateResult(expected)));
    }

    [Fact]
    public void TryGetResult_WithNullOrMismatchedResult_ReturnsFalse()
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 1, 2);
        var otherCallsite = new FlowActivityCallsite<TestWork, TestResult>("other", 1, 2);
        var otherVersion = new FlowActivityCallsite<TestWork, TestResult>("send-email", 1, 3);
        var otherType = new FlowActivityCallsite<TestWork, OtherResult>("send-email", 1, 2);

        Assert.False(callsite.TryGetResult(null, out _));
        Assert.False(callsite.TryGetResult(otherCallsite.CreateResult(new TestResult("x")), out _));
        Assert.False(callsite.TryGetResult(otherVersion.CreateResult(new TestResult("x")), out _));
        Assert.False(callsite.TryGetResult(otherType.CreateResult(new OtherResult("x")), out _));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    public void GetResult_WithMissingOrMismatchedResult_ThrowsFlowDefinitionException(string scenario)
    {
        var callsite = new FlowActivityCallsite<TestWork, TestResult>("send-email", 1, 2);
        var workResult = scenario == "missing"
            ? null
            : new FlowActivityCallsite<TestWork, TestResult>("other", 1, 2)
                .CreateResult(new TestResult("x"));

        var exception = Assert.Throws<FlowDefinitionException>(() => callsite.GetResult(workResult));

        Assert.Contains("send-email", exception.Message, StringComparison.Ordinal);
    }

    private sealed record TestState(string Status);

    private sealed record TestWork(string ApprovalId);

    private sealed record TestResult(string ProviderId);

    private sealed record OtherResult(string Value);
}
