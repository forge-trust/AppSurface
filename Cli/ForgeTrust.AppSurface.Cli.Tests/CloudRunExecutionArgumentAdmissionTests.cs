using ForgeTrust.AppSurface.Testing;
namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionArgumentAdmissionTests
{
    [Theory]
    [InlineData("projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", true)]
    [InlineData("projects/1234567890123456789/locations/us-central1/jobs/job-1/executions/execution-1", true)]
    [InlineData("projects/12345678901234567890/locations/us-central1/jobs/job-1/executions/execution-1", false)]
    [InlineData("projects/p/locations/r/jobs/j", false)]
    [InlineData("projects/p/locations/r/jobs/j/executions/e/extra", false)]
    [InlineData("projects/p/locations/r/jobs/j/executions/e\n", false)]
    public void CanonicalExecution_RequiresExactResourceShape(string value, bool expected)
        // protects=exact execution identity; fails_when=prefix or extra path is accepted; no_claim=provider existence
        => Assert.Equal(expected, CloudRunExecutionArgumentAdmission.IsCanonicalExecution(value));

    [Theory]
    [InlineData("60s", true)]
    [InlineData("2s", true)]
    [InlineData("0s", false)]
    [InlineData("-1s", false)]
    [InlineData("60", false)]
    [InlineData("60x", false)]
    public void DurationAdmission_IsFiniteAndSuffixed(string value, bool expected)
        // protects=finite bounded CLI timer; fails_when=zero, negative, or unitless input is accepted; no_claim=wall-clock scheduling
        => Assert.Equal(expected, CloudRunExecutionArgumentAdmission.TryDuration(value, out _));

    [Theory]
    [InlineData("1ms", true)]
    [InlineData("4294967294ms", true)]
    [InlineData("4294967295ms", false)]
    [InlineData("1193h", true)]
    [InlineData("1194h", false)]
    public void DurationAdmission_UsesMillisecondContractAndRejectsOverflow(string value, bool expected)
        // protects=public finite timer bound; fails_when=overflow or unsupported unit is accepted; no_claim=process preemption
        => Assert.Equal(expected, CloudRunExecutionArgumentAdmission.TryDuration(value, out _));

    [Fact]
    public void RawAdmission_RejectsUnknownDuplicateMissingAndJoinedOptions()
    // protects=pre-CliFx safe admission; fails_when=raw malformed tokens reach generic parser; no_claim=CliFx help rendering
    {
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bogus"]));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--job"]));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--job", "one", "--job", "two"]));
        Assert.False(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle", "--job=one", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json"]));
    }

    [Fact]
    public void RawAdmission_IgnoresOtherCommands()
        // protects=unrelated command compatibility; fails_when=observe admission intercepts another command; no_claim=other command validation
        => Assert.False(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["docs", "--bogus"]));

    [Theory]
    [InlineData("--bundle-dir", "bundle")]
    [InlineData("--job", "job")]
    [InlineData("--execution", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1")]
    [InlineData("--timeout", "60s")]
    [InlineData("--poll-interval", "2s")]
    public void Parse_RequiresCompleteRecognizedCommandBeforeCliFx(string option, string value)
    // protects=pre-CliFx safe argument admission; fails_when=partial argv is delegated to generic parser; no_claim=command execution
    {
        var args = new[] { "deployment", "gcp-cloud-run", "observe-execution", option, value };
        Assert.Null(CloudRunExecutionArgumentAdmission.Parse(args));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(args));
        Assert.False(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--help"]));
    }

    [Fact]
    public void PathAdmission_RejectsReparsePointWhenSupported()
    // protects=bundle path boundary; fails_when=reparse-point path is read; no_claim=adversarial filesystem replacement race
    {
        var root = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-cli-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(CloudRunExecutionArgumentAdmission.IsPathSafe(root));
            var link = root + "-link";
            try
            {
                Directory.CreateSymbolicLink(link, root);
                Assert.False(CloudRunExecutionArgumentAdmission.IsPathSafe(TestPathUtils.PathUnder(link, "child")));
            }
            catch (PlatformNotSupportedException) { }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
            }
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
