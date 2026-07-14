namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowEventCallsiteTests
{
    [Fact]
    public void Callsite_CapturesStableTypedContractMetadata()
    {
        IFlowEventCallsite callsite = new FlowEventCallsite<ApprovalSubmitted>(
            "approval-submitted",
            "approval.submitted",
            "v1");

        Assert.Equal("approval-submitted", callsite.EventName);
        Assert.Equal(typeof(ApprovalSubmitted), callsite.PayloadType);
        Assert.Equal("approval.submitted", callsite.ContractName);
        Assert.Equal("v1", callsite.ContractVersion);
    }

    [Theory]
    [InlineData("eventName")]
    [InlineData("contractName")]
    [InlineData("contractVersion")]
    public void Callsite_WithEmptyRequiredValue_ThrowsArgumentException(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "eventName" => new FlowEventCallsite<ApprovalSubmitted>(" ", "approval.submitted", "v1"),
            "contractName" => new FlowEventCallsite<ApprovalSubmitted>("approval-submitted", " ", "v1"),
            "contractVersion" => new FlowEventCallsite<ApprovalSubmitted>(
                "approval-submitted",
                "approval.submitted",
                " "),
            _ => throw new InvalidOperationException("Unknown parameter name."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void TypedWait_CapturesCallsiteContextAndTimeout()
    {
        var callsite = new FlowEventCallsite<ApprovalSubmitted>(
            "approval-submitted",
            "approval.submitted",
            "v1");
        var context = new TestState("waiting");
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        var wait = FlowNodeOutcome<TestState>.Wait(callsite, context, timeout);

        Assert.NotSame(callsite, wait.EventCallsite);
        Assert.Equal(callsite.EventName, wait.EventName);
        Assert.Equal(callsite.PayloadType, wait.EventCallsite?.PayloadType);
        Assert.Equal(callsite.ContractName, wait.EventCallsite?.ContractName);
        Assert.Equal(callsite.ContractVersion, wait.EventCallsite?.ContractVersion);
        Assert.Same(context, wait.Context);
        Assert.Same(timeout, wait.Timeout);
    }

    [Fact]
    public void TypedWait_WithNullCallsite_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            FlowNodeOutcome<TestState>.Wait<ApprovalSubmitted>(null!, new TestState("waiting")));

        Assert.Equal("callsite", exception.ParamName);
    }

    [Fact]
    public void TypedWait_SnapshotsExtensibleCallsiteMetadata()
    {
        var callsite = new MutableCallsite
        {
            EventName = "approval-submitted",
            PayloadType = typeof(ApprovalSubmitted),
            ContractName = "approval.submitted",
            ContractVersion = "v1",
        };

        var wait = new FlowWait<TestState>(callsite, new TestState("waiting"));
        callsite.EventName = "changed";
        callsite.PayloadType = typeof(string);
        callsite.ContractName = "changed";
        callsite.ContractVersion = "v2";

        Assert.Equal("approval-submitted", wait.EventName);
        Assert.Equal(typeof(ApprovalSubmitted), wait.EventCallsite?.PayloadType);
        Assert.Equal("approval.submitted", wait.EventCallsite?.ContractName);
        Assert.Equal("v1", wait.EventCallsite?.ContractVersion);
    }

    [Fact]
    public void TypedWaitingResult_SnapshotsExtensibleCallsiteMetadata()
    {
        var callsite = new MutableCallsite
        {
            EventName = "approval-submitted",
            PayloadType = typeof(ApprovalSubmitted),
            ContractName = "approval.submitted",
            ContractVersion = "v1",
        };

        var result = FlowRunResult<TestState>.Waiting("review", callsite, new TestState("waiting"));
        callsite.ContractVersion = "v2";

        Assert.Equal("approval-submitted", result.WaitingEventName);
        Assert.Equal(typeof(ApprovalSubmitted), result.EventCallsite?.PayloadType);
        Assert.Equal("approval.submitted", result.EventCallsite?.ContractName);
        Assert.Equal("v1", result.EventCallsite?.ContractVersion);
    }

    [Fact]
    public void TypedWaitingResult_WithNullPayloadType_ThrowsArgumentNullException()
    {
        var callsite = new MutableCallsite
        {
            EventName = "approval-submitted",
            PayloadType = null!,
            ContractName = "approval.submitted",
            ContractVersion = "v1",
        };

        var exception = Assert.Throws<ArgumentNullException>(() =>
            FlowRunResult<TestState>.Waiting("review", callsite, new TestState("waiting")));

        Assert.Equal("eventCallsite", exception.ParamName);
    }

    private sealed record ApprovalSubmitted(string ApprovedBy);

    private sealed record TestState(string Status);

    private sealed class MutableCallsite : IFlowEventCallsite
    {
        public required string EventName { get; set; }

        public required Type PayloadType { get; set; }

        public required string ContractName { get; set; }

        public required string ContractVersion { get; set; }
    }
}
