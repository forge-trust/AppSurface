namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskFlowClientTests
{
    [Fact]
    public async Task DefaultAuthorizer_DeniesResumeEvents()
    {
        var authorizer = new DenyAllFlowResumeAuthorizer();

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.False(result.Allowed);
        Assert.Equal("flow.resume-denied", result.Code);
    }

    [Fact]
    public async Task Client_DelegatesToRegisteredAuthorizer()
    {
        var client = new DurableTaskFlowClient<TestState>(new AllowAuthorizer());

        var result = await client.AuthorizeResumeAsync(Request());

        Assert.True(result.Allowed);
        Assert.Equal("flow.resume-allowed", result.Code);
    }

    [Fact]
    public void AuthorizationRequest_WithEmptyCaller_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FlowResumeAuthorizationRequest(
            "approval",
            "1",
            "instance-1",
            "review",
            "approved",
            " "));
    }

    private static FlowResumeAuthorizationRequest Request() =>
        new("approval", "1", "instance-1", "review", "approved", "andrew");

    private sealed record TestState(string Value);

    private sealed class AllowAuthorizer : IFlowResumeAuthorizer
    {
        public ValueTask<FlowResumeAuthorizationResult> AuthorizeAsync(
            FlowResumeAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FlowResumeAuthorizationResult.Allow());
    }
}
