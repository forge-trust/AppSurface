namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindProducerBindingRequestTests
{
    [Theory]
    [InlineData("--producer-artifact-id")]
    [InlineData("--expected-subject-sha256")]
    [InlineData("--repository-id")]
    [InlineData("--producer-run-id")]
    [InlineData("--source-commit")]
    public void CliMappingRefusesMissingProducerAuthorityWithoutDefaultingIt(string missing)
    {
        string[] arguments = ["--producer-artifact-id", "501", "--expected-subject-sha256", new string('b', 64),
            "--repository-id", "12345", "--producer-run-id", "901", "--source-commit", new string('a', 40)];
        var remaining = arguments.Chunk(2).Where(pair => pair[0] != missing).SelectMany(pair => pair).ToArray();
        var options = TailwindCommandOptions.Extract(remaining);

        var error = Assert.Throws<PackageIndexException>(() => options.CreateProducerBindingRequest("repository", "bundle", "manifest"));

        Assert.Equal($"Required option '{missing}' is missing.", error.Message);
    }

    [Fact]
    public async Task TypedBindingChecksSourceBeforeResolvingOrReadingBundlePaths()
    {
        var input = new TailwindProducerBindingRequest("repository", "\0invalid-bundle", "\0invalid-manifest", null,
            "501", new string('b', 64), "12345", "901", new string('a', 40));
        var sourceFailure = new PackageIndexException("Source identity failed.");
        var calls = 0;
        using var caller = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindEvidenceWorkflow.ValidateProducerBindingAsync(
            input, caller.Token, (root, commit, token) =>
            {
                Assert.Equal("repository", root);
                Assert.Equal(input.SourceCommit, commit);
                Assert.Equal(caller.Token, token);
                calls++;
                throw sourceFailure;
            }));

        Assert.Same(sourceFailure, error);
        Assert.Equal(1, calls);
    }
}
