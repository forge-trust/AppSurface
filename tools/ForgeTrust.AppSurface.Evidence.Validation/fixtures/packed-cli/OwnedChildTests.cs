using ForgeTrust.AppSurface.Evidence.Contracts;
using Xunit;

public sealed class OwnedChildTests
{
    [Fact]
    public async Task CandidateHelperEnrollsAndJoinsActualChildWork()
    {
        string[] channelVariables =
        [
            "APPSURFACE_EVIDENCE_CHILD_PIPE",
            "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID",
            "APPSURFACE_EVIDENCE_CHILD_NONCE",
        ];
        Assert.True(channelVariables.All(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))),
            "An invocation-private channel must be present.");
        var completed = 0;
        await EvidenceOwnedChild.RunAsync(async _ =>
        {
            await Task.Yield();
            Interlocked.Increment(ref completed);
        }, CancellationToken.None);
        Assert.Equal(1, completed);
    }
}
