using System.Text;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Deployment.Tests;

public sealed class DeploymentArtifactBundleWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppSurfaceDeploymentTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteCreatesOwnedBundleInPreviouslyMissingDirectory()
    {
        var output = OutputPath();
        await DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "first")]);

        Assert.Equal("first", File.ReadAllText(Path.Combine(output, "intent.json")));
        Assert.Equal("gcp-staging\n", File.ReadAllText(TestPathUtils.PathUnder(output, DeploymentArtifactBundleWriter.OwnershipMarkerFileName)));
        Assert.Empty(StageDirectories());
    }

    [Fact]
    public async Task WriteAllowsAnExistingEmptyDirectory()
    {
        var output = OutputPath();
        Directory.CreateDirectory(output);
        await DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "first")]);
        Assert.True(File.Exists(Path.Combine(output, "intent.json")));
    }

    [Fact]
    public async Task WriteAtomicallyReplacesCompleteOwnedBundle()
    {
        var output = OutputPath();
        await DeploymentArtifactBundleWriter.WriteAsync(
            output,
            "gcp-staging",
            [Artifact("intent.json", "old"), Artifact("plan.json", "old-plan")]);

        await DeploymentArtifactBundleWriter.WriteAsync(
            output,
            "gcp-staging",
            [Artifact("intent.json", "new"), Artifact("plan.json", "new-plan")]);

        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "intent.json")));
        Assert.Equal("new-plan", File.ReadAllText(Path.Combine(output, "plan.json")));
        Assert.Empty(StageDirectories());
    }

    [Fact]
    public async Task WriteRejectsNonOwnedNonEmptyDirectoryWithoutChangingIt()
    {
        var output = OutputPath();
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "user.txt"), "owned by user");

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(
            () => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "new")]));

        Assert.Equal("ASDEPLOY123", error.Diagnostic.Code);
        Assert.Equal("owned by user", File.ReadAllText(Path.Combine(output, "user.txt")));
        Assert.False(File.Exists(Path.Combine(output, "intent.json")));
    }

    [Fact]
    public async Task WriteRejectsMarkerOwnedByDifferentTarget()
    {
        var output = OutputPath();
        Directory.CreateDirectory(output);
        File.WriteAllText(TestPathUtils.PathUnder(output, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), "another-target\n");

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(
            () => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "new")]));

        Assert.Equal("ASDEPLOY124", error.Diagnostic.Code);
    }

    [Fact]
    public async Task WriteRejectsUnexpectedEntryInOwnedDirectory()
    {
        var output = OutputPath();
        Directory.CreateDirectory(output);
        File.WriteAllText(TestPathUtils.PathUnder(output, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), "gcp-staging\n");
        File.WriteAllText(Path.Combine(output, "unexpected.txt"), "preserve");

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(
            () => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "new")]));

        Assert.Equal("ASDEPLOY125", error.Diagnostic.Code);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(output, "unexpected.txt")));
    }

    [Fact]
    public async Task WriteRejectsDuplicateArtifactsBeforeCreatingOutput()
    {
        var output = OutputPath();
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(
            () => DeploymentArtifactBundleWriter.WriteAsync(
                output,
                "gcp-staging",
                [Artifact("intent.json", "one"), Artifact("intent.json", "two")]));

        Assert.Equal("ASDEPLOY120", error.Diagnostic.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task WriteRejectsPortableCaseCollisionBeforeCreatingOutput()
    {
        var output = OutputPath();
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "one"), Artifact("INTENT.JSON", "two")]));
        Assert.Equal("ASDEPLOY120", error.Diagnostic.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task WriteRejectsReservedOwnershipMarkerBeforeCreatingOutput()
    {
        var output = OutputPath();
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact(DeploymentArtifactBundleWriter.OwnershipMarkerFileName, "forged")]));
        Assert.Equal("ASDEPLOY120", error.Diagnostic.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CancellationCleansStagingAndPreservesExistingBundle()
    {
        var output = OutputPath();
        await DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "old")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DeploymentArtifactBundleWriter.WriteAsync(output, "gcp-staging", [Artifact("intent.json", "new")], cancellation.Token));

        Assert.Equal("old", File.ReadAllText(Path.Combine(output, "intent.json")));
        Assert.Empty(StageDirectories());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task WriteRejectsBlankOutputDirectory(string output)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DeploymentArtifactBundleWriter.WriteAsync(output, "target", []));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task WriteRejectsBlankTarget(string target)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DeploymentArtifactBundleWriter.WriteAsync(OutputPath(), target, []));
    }

    [Fact]
    public async Task WriteRejectsNullArtifacts()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => DeploymentArtifactBundleWriter.WriteAsync(OutputPath(), "target", null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string OutputPath() => Path.Combine(_root, "bundle");

    private string[] StageDirectories() => Directory.Exists(_root)
        ? Directory.GetDirectories(_root, ".bundle.appsurface-*", SearchOption.TopDirectoryOnly)
        : [];

    private static DeploymentArtifact Artifact(string name, string content) =>
        DeploymentArtifact.Create(name, Encoding.UTF8.GetBytes(content));
}
