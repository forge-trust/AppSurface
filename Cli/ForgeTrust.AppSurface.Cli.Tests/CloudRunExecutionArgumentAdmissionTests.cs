using ForgeTrust.AppSurface.Testing;
namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionArgumentAdmissionTests
{
    [Theory]
    [InlineData("projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", true)]
    [InlineData("projects/1234567890123456789/locations/us-central1/jobs/job-1/executions/execution-1", true)]
    [InlineData("projects/12345678901234567890/locations/us-central1/jobs/job-1/executions/execution-1", false)]
    [InlineData("projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1\n", false)]
    [InlineData("projects/p/locations/r/jobs/j", false)]
    [InlineData("projects/p/locations/r/jobs/j/executions/e/extra", false)]
    [InlineData("projects/p/locations/r/jobs/j/executions/e\n", false)]
    public void CanonicalExecution_RequiresExactResourceShape(string value, bool expected)
        // protects=exact execution identity; fails_when=prefix or extra path is accepted; no_claim=provider existence
        => Assert.Equal(expected, CloudRunExecutionArgumentAdmission.IsCanonicalExecution(value));

    [Theory]
    [InlineData("60s", true)]
    [InlineData("2s", true)]
    [InlineData("1m", true)]
    [InlineData("1d", true)]
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
    public void DurationAdmission_RejectsMissingAndUnrepresentableTickValues()
    {
        // protects=duration parser boundary for absent and sub-tick precision; fails_when=null, whitespace, or fractional ticks are admitted; no_claim=timer accuracy
        Assert.False(CloudRunExecutionArgumentAdmission.TryDuration(null, out _));
        Assert.False(CloudRunExecutionArgumentAdmission.TryDuration("   ", out _));
        Assert.False(CloudRunExecutionArgumentAdmission.TryDuration("0.00001ms", out _));
        Assert.False(CloudRunExecutionArgumentAdmission.TryDuration("0.00011ms", out _));
    }

    [Fact]
    public void LogicalIdMustConsumeEntireInput()
    {
        // protects=complete logical binding grammar; fails_when=a final newline passes local CLI admission;
        // no_claim=the valid logical ID is declared in a deployment bundle.
        Assert.False(CloudRunExecutionArgumentAdmission.TryValidate("bundle", "schema-preparation\n",
            "projects/demo-project/locations/us-central1/jobs/schema-preparation/executions/schema-preparation-run",
            "60s", "2s", out _, out _));
    }

    [Fact]
    public void RawAdmission_RejectsUnknownDuplicateMissingAndJoinedOptions()
    // protects=pre-CliFx safe admission; fails_when=raw malformed tokens reach generic parser; no_claim=CliFx help rendering
    {
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bogus"]));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--job"]));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--job", "one", "--job", "two"]));
        Assert.False(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle", "--job=one", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json"]));

        // protects=json switch syntax and uniqueness; fails_when=value-attached or repeated json flag reaches binding; no_claim=JSON payload validity
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle", "--job=one", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json=true"]));
        Assert.True(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle", "--job=one", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json", "--json"]));
    }

    [Fact]
    public void RawAdmission_IgnoresOtherCommands()
    // protects=unrelated command compatibility; fails_when=observe admission intercepts another command; no_claim=other command validation
    {
        Assert.False(CloudRunExecutionArgumentAdmission.IsMalformedRaw(["docs", "--bogus"]));
        Assert.Null(CloudRunExecutionArgumentAdmission.Parse(["docs", "--bogus"]));
    }

    [Fact]
    public void Parse_ConsumesRecognizedJsonSwitch()
    {
        var parsed = CloudRunExecutionArgumentAdmission.Parse(["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle", "--job=job", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json"]);

        // protects=the valueless JSON switch parses as a boolean option; fails_when=valid switch is rejected or altered; no_claim=command output serialization
        Assert.NotNull(parsed);
        Assert.Equal("true", parsed["--json"]);
    }

    [Fact]
    public void Normalization_PreservesHelpOtherCommandsAndInvalidInput()
    {
        Assert.Throws<ArgumentNullException>(() => CloudRunExecutionArgumentAdmission.NormalizeJoinedValueOptions(null!));
        foreach (var args in new[]
        {
            new[] { "deployment", "gcp-cloud-run", "observe-execution", "--help" },
            new[] { "docs", "--output=docs" },
            new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bogus=value" },
        })
            Assert.Same(args, CloudRunExecutionArgumentAdmission.NormalizeJoinedValueOptions(args));
    }

    [Fact]
    public void Normalization_RetainsEmbeddedEqualsAndOptionalJson()
    {
        string[] args = ["deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=bundle=one", "--job=job",
            "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1"];

        Assert.Equal(new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir", "bundle=one", "--job", "job",
            "--execution", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1" },
            CloudRunExecutionArgumentAdmission.NormalizeJoinedValueOptions(args));
    }

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
        var target = TestPathUtils.PathUnder(root, "target.txt");
        var fileLink = TestPathUtils.PathUnder(root, "file-link.txt");
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
            File.WriteAllText(target, "target");
            Assert.True(CloudRunExecutionArgumentAdmission.IsPathSafe(target));
            Assert.True(CloudRunExecutionArgumentAdmission.IsPathSafe(TestPathUtils.PathUnder(root, "not-created", "child")));
            try
            {
                try
                {
                    File.CreateSymbolicLink(fileLink, target);
                    // protects=file-component reparse-point rejection; fails_when=path through a file symlink is admitted; no_claim=filesystem race after admission
                    Assert.False(CloudRunExecutionArgumentAdmission.IsPathSafe(fileLink));
                }
                catch (PlatformNotSupportedException) { }
            }
            finally
            {
                if (File.Exists(fileLink)) File.Delete(fileLink);
            }
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
            Directory.Delete(root);
        }
    }
}
