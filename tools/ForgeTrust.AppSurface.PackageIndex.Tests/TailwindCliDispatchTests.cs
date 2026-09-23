namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Checks the public command boundary for release proof modes and missing authority.</summary>
public sealed class TailwindCliDispatchTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder("/private/tmp", "tailwind-cli-dispatch", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("verify-tailwind-consumer", "invalid", "must be local or release")]
    [InlineData("verify-tailwind-evidence", "invalid", "must be producer-binding")]
    [InlineData("verify-tailwind-evidence", "publish-preflight", "--report-directory")]
    [InlineData("verify-tailwind-evidence", "validate-publication-start", "--report-directory")]
    public async Task ReleaseCommands_RejectInvalidOrIncompleteAuthorityAtCliBoundary(
        string command, string mode, string expectedError)
    {
        Directory.CreateDirectory(_root);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            [command, "--repo-root", _root, "--mode", mode], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains(expectedError, stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("succeeded", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateCli_LeavesNoSuccessReceiptWhenProducerAuthorityIsMissing()
    {
        Directory.CreateDirectory(_root);
        var report = TestPathUtils.PathUnder(_root, "aggregate-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            ["verify-tailwind-evidence", "--repo-root", _root, "--mode", "aggregate",
                "--report-directory", report], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(TestPathUtils.PathUnder(report, "tailwind-native-host-evidence.json")));
        Assert.True(stderr.ToString().Length > 0 || stdout.ToString().Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReleaseConsumerCli_ReportsProducerBindingFailureWithoutInvokingNativeCommands()
    {
        Directory.CreateDirectory(_root);
        var work = TestPathUtils.PathUnder(_root, "release-work");
        var report = TestPathUtils.PathUnder(_root, "release-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["verify-tailwind-consumer", "--repo-root", _root, "--mode", "release",
                "--work-directory", work, "--report-directory", report],
            stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains("failed", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json"))
            || File.Exists(TestPathUtils.PathUnder(report, "tailwind-failure-diagnostics.json")));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task PublishPrereleaseCli_ForwardsTailwindIdentityToInjectedPublisher()
    {
        Directory.CreateDirectory(_root);
        var artifacts = TestPathUtils.PathUnder(_root, "candidate-bundle");
        var manifest = TestPathUtils.PathUnder(_root, "candidate-manifest.json");
        var subject = TestPathUtils.PathUnder(artifacts, "tailwind-proof-subject.json");
        var aggregate = TestPathUtils.PathUnder(_root, "aggregate");
        var publication = TestPathUtils.PathUnder(_root, "prepared");
        var startReceipt = TestPathUtils.PathUnder(_root, "start", "publication-start-receipt.json");
        var report = TestPathUtils.PathUnder(_root, "publish-report");
        const string subjectHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string aggregateHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string sourceCommit = "cccccccccccccccccccccccccccccccccccccccc";
        PackagePublishRequest? dispatched = null;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["publish-prerelease", "--repo-root", _root, "--artifacts-input", artifacts,
                "--artifact-manifest", manifest, "--publish-log", TestPathUtils.PathUnder(_root, "publish.md"),
                "--producer-subject", subject, "--producer-artifact-id", "123",
                "--expected-subject-sha256", subjectHash, "--repository-id", "456",
                "--producer-run-id", "789", "--source-commit", sourceCommit,
                "--aggregate-input", aggregate, "--aggregate-artifact-id", "321",
                "--expected-aggregate-sha256", aggregateHash, "--publication-directory", publication,
                "--publication-start-receipt", startReceipt, "--publication-start-artifact-id", "654",
                "--report-directory", report],
            stdout,
            stderr,
            _root,
            publishPrereleaseAsync: (request, _) =>
            {
                dispatched = request;
                return Task.FromResult(new PackagePublishLedger("1.2.3-preview.798", request.Source, []));
            });

        Assert.Equal(0, exit);
        Assert.Empty(stderr.ToString());
        Assert.Contains("Published 0 prerelease package artifacts", stdout.ToString(), StringComparison.Ordinal);
        Assert.NotNull(dispatched?.TailwindEvidence);
        Assert.Equal("123", dispatched!.TailwindEvidence!.ProducerArtifactId);
        Assert.Equal(subjectHash, dispatched.TailwindEvidence.ExpectedSubjectSha256);
        Assert.Equal(aggregate, dispatched.TailwindEvidence.AggregateInputPath);
        Assert.Equal("654", dispatched.TailwindEvidence.PublicationStartArtifactId);
        Assert.Equal(artifacts, dispatched.ArtifactsInputPath);
        Assert.Equal(manifest, dispatched.ArtifactManifestPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
