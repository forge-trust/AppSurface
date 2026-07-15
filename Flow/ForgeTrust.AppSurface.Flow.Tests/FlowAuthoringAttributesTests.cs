namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowAuthoringAttributesTests
{
    [Fact]
    public void FlowAuthoringAttribute_WithFlowId_StoresFlowMetadata()
    {
        var attribute = new FlowAuthoringAttribute("approval")
        {
            Version = "2026-06-06",
        };

        Assert.Equal("approval", attribute.FlowId);
        Assert.Equal("2026-06-06", attribute.Version);
    }

    [Fact]
    public void FlowAuthoringAttribute_DefaultsVersionToOne()
    {
        var attribute = new FlowAuthoringAttribute("approval");

        Assert.Equal("1", attribute.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FlowAuthoringAttribute_WithMissingFlowId_ThrowsArgumentException(string? flowId)
    {
        var exception = Assert.Throws<ArgumentException>(() => new FlowAuthoringAttribute(flowId!));

        Assert.Equal("flowId", exception.ParamName);
    }

    [Fact]
    public void FlowNodeAttribute_WithNodeIdAndInputType_StoresNodeMetadata()
    {
        var attribute = new FlowNodeAttribute("start", typeof(StartState));

        Assert.Equal("start", attribute.NodeId);
        Assert.Equal(typeof(StartState), attribute.InputContextType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FlowNodeAttribute_WithMissingNodeId_ThrowsArgumentException(string? nodeId)
    {
        var exception = Assert.Throws<ArgumentException>(() => new FlowNodeAttribute(nodeId!, typeof(StartState)));

        Assert.Equal("nodeId", exception.ParamName);
    }

    [Fact]
    public void FlowNodeAttribute_WithNullInputType_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new FlowNodeAttribute("start", null!));

        Assert.Equal("inputContextType", exception.ParamName);
    }

    [Fact]
    public void FlowOutcomeAttribute_WithNameKindAndOutputType_StoresOutcomeMetadata()
    {
        var attribute = new FlowOutcomeAttribute("done", FlowOutcomeKind.Complete, typeof(DoneState));

        Assert.Equal("done", attribute.Name);
        Assert.Equal(FlowOutcomeKind.Complete, attribute.Kind);
        Assert.Equal(typeof(DoneState), attribute.OutputContextType);
        Assert.Null(attribute.WorkType);
        Assert.Null(attribute.ResultType);
        Assert.Null(attribute.CallsiteId);
        Assert.Equal(1, attribute.WorkContractVersion);
        Assert.Equal(1, attribute.ResultContractVersion);
    }

    [Fact]
    public void FlowOutcomeAttribute_WithActivityContracts_StoresActivityMetadata()
    {
        var attribute = new FlowOutcomeAttribute(
            "send-email",
            FlowOutcomeKind.Activity,
            typeof(StartState),
            typeof(ActivityWork),
            typeof(ActivityResult))
        {
            CallsiteId = "approval.send-email",
            WorkContractVersion = 2,
            ResultContractVersion = 3,
        };

        Assert.Equal(FlowOutcomeKind.Activity, attribute.Kind);
        Assert.Equal(typeof(ActivityWork), attribute.WorkType);
        Assert.Equal(typeof(ActivityResult), attribute.ResultType);
        Assert.Equal("approval.send-email", attribute.CallsiteId);
        Assert.Equal(2, attribute.WorkContractVersion);
        Assert.Equal(3, attribute.ResultContractVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FlowOutcomeAttribute_WithMissingName_ThrowsArgumentException(string? name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FlowOutcomeAttribute(name!, FlowOutcomeKind.Complete, typeof(DoneState)));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void FlowOutcomeAttribute_WithNullOutputType_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new FlowOutcomeAttribute("done", FlowOutcomeKind.Complete, null!));

        Assert.Equal("outputContextType", exception.ParamName);
    }

    [Fact]
    public void FlowOutcomeKind_ValuesRemainStable()
    {
        Assert.Equal(0, (int)FlowOutcomeKind.Next);
        Assert.Equal(1, (int)FlowOutcomeKind.Wait);
        Assert.Equal(2, (int)FlowOutcomeKind.TimedOut);
        Assert.Equal(3, (int)FlowOutcomeKind.Complete);
        Assert.Equal(4, (int)FlowOutcomeKind.Fault);
        Assert.Equal(5, (int)FlowOutcomeKind.Activity);
    }

    [Fact]
    public void FlowGraphMappingAttribute_WithNodeOutcomeAndOutputType_StoresMappingMetadata()
    {
        var attribute = new FlowGraphMappingAttribute("start", "done", typeof(DoneState));

        Assert.Equal("start", attribute.NodeId);
        Assert.Equal("done", attribute.OutcomeName);
        Assert.Equal(typeof(DoneState), attribute.OutputContextType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FlowGraphMappingAttribute_WithMissingNodeId_ThrowsArgumentException(string? nodeId)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FlowGraphMappingAttribute(nodeId!, "done", typeof(DoneState)));

        Assert.Equal("nodeId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FlowGraphMappingAttribute_WithMissingOutcomeName_ThrowsArgumentException(string? outcomeName)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FlowGraphMappingAttribute("start", outcomeName!, typeof(DoneState)));

        Assert.Equal("outcomeName", exception.ParamName);
    }

    [Fact]
    public void FlowGraphMappingAttribute_WithNullOutputType_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new FlowGraphMappingAttribute("start", "done", null!));

        Assert.Equal("outputContextType", exception.ParamName);
    }

    [Fact]
    public void FlowTransformerContext_WithStateAndResumeEvent_StoresExecutionMetadata()
    {
        var resumeEvent = new FlowResumeEvent("approved", payload: "yes");
        var state = new StartState("case-1");

        var context = new FlowTransformerContext<StartState>(
            "approval",
            "1",
            "start",
            state,
            resumeEvent);

        Assert.Equal("approval", context.FlowId);
        Assert.Equal("1", context.Version);
        Assert.Equal("start", context.NodeId);
        Assert.Equal(state, context.State);
        Assert.Equal(resumeEvent, context.ResumeEvent);
        Assert.Null(context.ActivityResult);
    }

    [Fact]
    public void FlowTransformerContext_WithActivityResult_StoresTypedResult()
    {
        var callsite = new FlowActivityCallsite<ActivityWork, ActivityResult>("send-email");
        var result = callsite.CreateResult(new ActivityResult("sent"));

        var context = new FlowTransformerContext<StartState>("approval", "1", "start", new StartState())
        {
            ActivityResult = result,
        };

        Assert.Same(result, context.ActivityResult);
    }

    private sealed record StartState(string CaseId = "case");

    private sealed record DoneState;

    private sealed record ActivityWork(string Id);

    private sealed record ActivityResult(string Status);
}
