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

        Assert.Same(callsite, wait.EventCallsite);
        Assert.Equal(callsite.EventName, wait.EventName);
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

    private sealed record ApprovalSubmitted(string ApprovedBy);

    private sealed record TestState(string Status);
}
