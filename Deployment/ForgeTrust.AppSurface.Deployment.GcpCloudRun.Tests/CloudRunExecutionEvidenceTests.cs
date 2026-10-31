using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class CloudRunExecutionEvidenceTests
{
    [Fact]
    public void CompleteResultRequiresZeroExitAndValidUtf8()
    {
        // protects=bounded reader result contract; fails_when=nonzero or invalid output is represented as complete; no_claim=provider payload is semantically valid.
        Assert.Throws<ArgumentException>(() => new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 1));
        Assert.Throws<ArgumentException>(() => new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, new byte[] { 0xff }));
    }

    [Fact]
    public void OutputLimitIsInclusiveAtOneMiBAndRejectsOneByteBeyond()
    {
        // protects=streaming output boundary; fails_when=the byte cap is off by one; no_claim=process cleanup was confirmed.
        var exact = new byte[1_048_576];
        var result = new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, exact);
        Assert.Equal(exact.Length, result.StandardOutput.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.OutputLimit, null, new byte[1_048_577]));
    }

    [Fact]
    public void OutputIsDefensivelyCopied()
    {
        // protects=immutable safe result; fails_when=callers can mutate captured evidence; no_claim=raw provider output is safe to log.
        var bytes = new byte[] { 65 };
        var result = new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, bytes);
        bytes[0] = 66;
        var returned = result.StandardOutput;
        returned[0] = 67;

        Assert.Equal(65, result.StandardOutput[0]);
    }
}
