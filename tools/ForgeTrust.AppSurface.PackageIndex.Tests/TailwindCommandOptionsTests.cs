namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindCommandOptionsTests
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "tailwind-command-options", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Extract_PreservesGeneralFlagsAndBindsReleaseIdentity()
    {
        var options = TailwindCommandOptions.Extract([
            "--repo-root", _root,
            "--mode", "aggregate",
            "--producer-artifact-id", "501",
            "--expected-subject-sha256", new string('a', 64),
            "--artifact-manifest", "manifest.json",
            "--native-invocation-id", "native-798-1"
        ]);

        Assert.Equal("aggregate", options.Mode);
        Assert.Equal("501", options.ProducerArtifactId);
        Assert.Equal("native-798-1", options.NativeInvocationId);
        Assert.Equal(["--repo-root", _root, "--artifact-manifest", "manifest.json"], options.RemainingArguments);
    }

    [Theory]
    [InlineData("--mode", "release", "--mode", "local")]
    [InlineData("--producer-artifact-id", "501", "--producer-artifact-id", "502")]
    public void Extract_RejectsDuplicateAuthorityFlags(string first, string firstValue, string second, string secondValue)
    {
        var error = Assert.Throws<PackageIndexException>(() => TailwindCommandOptions.Extract([
            first, firstValue, second, secondValue
        ]));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RejectsMissingAuthorityValue()
    {
        var error = Assert.Throws<PackageIndexException>(() => TailwindCommandOptions.Extract(["--producer-artifact-id"]));
        Assert.Contains("requires a value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightAndStartValidation_KeepOriginalIdsAndSeparateReportPaths()
    {
        var options = TailwindCommandOptions.Extract([
            "--producer-subject", "producer/tailwind-proof-subject.json",
            "--producer-artifact-id", "501",
            "--expected-subject-sha256", new string('a', 64),
            "--repository-id", "12345",
            "--producer-run-id", "901",
            "--source-commit", new string('b', 40),
            "--aggregate-input", "aggregate",
            "--aggregate-artifact-id", "702",
            "--expected-aggregate-sha256", new string('c', 64),
            "--publication-directory", "prepared",
            "--publication-start-receipt", "downloaded/publication-start-receipt.json",
            "--publication-start-artifact-id", "703",
            "--report-directory", "report"
        ]);

        var preflight = options.CreatePreflightRequest(_root, "bundle", "manifest.json");
        var start = options.CreateStartValidationRequest(_root, "bundle", "manifest.json");
        var publish = options.CreatePublicationRequest(_root, "bundle", "manifest.json");

        Assert.Equal("501", preflight.ProducerArtifactId);
        Assert.Equal("702", preflight.AggregateArtifactId);
        Assert.Equal(string.Empty, preflight.PublicationStartArtifactId);
        Assert.Equal(TestPathUtils.PathUnder(_root, "report", "publication-start-receipt.json"), preflight.PublicationStartReceiptPath);
        Assert.Equal("703", start.PublicationStartArtifactId);
        Assert.Equal(TestPathUtils.PathUnder(_root, "downloaded", "publication-start-receipt.json"), start.PublicationStartReceiptPath);
        Assert.Equal(TestPathUtils.PathUnder(_root, "prepared"), start.PublicationDirectory);
        Assert.Equal("703", publish.PublicationStartArtifactId);
    }

    [Fact]
    public void PublicationRequest_RejectsMissingOriginalReceiptIdentity()
    {
        var options = TailwindCommandOptions.Extract(["--producer-subject", "subject.json"]);

        Assert.Throws<PackageIndexException>(() => options.CreatePublicationRequest(_root, "bundle", "manifest.json"));
    }
}
