namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class FlowResumeAuthorizationTests
{
    [Fact]
    public void Request_CapturesRequiredFieldsAndMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant"] = "north",
        };

        var request = new FlowResumeAuthorizationRequest(
            "approval",
            "1",
            "instance-1",
            "review",
            "approved",
            "andrew",
            metadata);

        Assert.Equal("approval", request.FlowId);
        Assert.Equal("1", request.Version);
        Assert.Equal("instance-1", request.InstanceId);
        Assert.Equal("review", request.NodeId);
        Assert.Equal("approved", request.EventName);
        Assert.Equal("andrew", request.Caller);
        Assert.Same(metadata, request.Metadata);
    }

    [Fact]
    public void Request_WithoutMetadata_UsesEmptyMetadata()
    {
        var request = Request();

        Assert.Empty(request.Metadata);
    }

    [Theory]
    [InlineData("flowId")]
    [InlineData("version")]
    [InlineData("instanceId")]
    [InlineData("nodeId")]
    [InlineData("eventName")]
    [InlineData("caller")]
    public void Request_WithEmptyRequiredText_ThrowsArgumentException(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "flowId" => Request(flowId: " "),
            "version" => Request(version: " "),
            "instanceId" => Request(instanceId: " "),
            "nodeId" => Request(nodeId: " "),
            "eventName" => Request(eventName: " "),
            "caller" => Request(caller: " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Allow_CreatesDefaultAllowedResult()
    {
        var result = FlowResumeAuthorizationResult.Allow();

        Assert.True(result.Allowed);
        Assert.Equal("flow.resume-allowed", result.Code);
        Assert.Equal("Resume event allowed.", result.Message);
    }

    [Fact]
    public void Allow_CanUseCustomCodeAndMessage()
    {
        var result = FlowResumeAuthorizationResult.Allow("tenant.allowed", "Tenant can resume.");

        Assert.True(result.Allowed);
        Assert.Equal("tenant.allowed", result.Code);
        Assert.Equal("Tenant can resume.", result.Message);
    }

    [Fact]
    public void Deny_CreatesDeniedResult()
    {
        var result = FlowResumeAuthorizationResult.Deny("tenant.denied", "Tenant cannot resume.");

        Assert.False(result.Allowed);
        Assert.Equal("tenant.denied", result.Code);
        Assert.Equal("Tenant cannot resume.", result.Message);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("message")]
    public void Result_WithEmptyRequiredText_ThrowsArgumentException(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "code" => new FlowResumeAuthorizationResult(true, " ", "Allowed."),
            "message" => new FlowResumeAuthorizationResult(true, "allowed", " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    private static FlowResumeAuthorizationRequest Request(
        string flowId = "approval",
        string version = "1",
        string instanceId = "instance-1",
        string nodeId = "review",
        string eventName = "approved",
        string caller = "andrew") =>
        new(flowId, version, instanceId, nodeId, eventName, caller);
}
