using System.Text;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.Tests;

public sealed class DeploymentArtifactContractTests
{
    [Fact]
    public void CanonicalJsonIsStableAcrossInputOrderWithoutBomAndWithOneTrailingLf()
    {
        var first = Intent(
            DeploymentIntentTests.CreateJob("z-job", environment: new Dictionary<string, string> { ["ZED"] = "2", ["ALPHA"] = "1" }),
            DeploymentIntentTests.CreateJob("a-job"));
        var second = Intent(
            DeploymentIntentTests.CreateJob("a-job"),
            DeploymentIntentTests.CreateJob("z-job", environment: new Dictionary<string, string> { ["ALPHA"] = "1", ["ZED"] = "2" }));

        var firstBytes = DeploymentCanonicalJson.Serialize(first);
        var secondBytes = DeploymentCanonicalJson.Serialize(second);
        Assert.Equal(firstBytes, secondBytes);
        Assert.False(firstBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal((byte)'\n', firstBytes[^1]);
        Assert.NotEqual((byte)'\n', firstBytes[^2]);
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(firstBytes), StringComparison.Ordinal);
        Assert.True(IndexOf(firstBytes, "a-job") < IndexOf(firstBytes, "z-job"));
        Assert.True(IndexOf(firstBytes, "ALPHA") < IndexOf(firstBytes, "ZED"));
        var json = Encoding.UTF8.GetString(firstBytes);
        Assert.Contains("\"phase\": \"candidate-preparation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"run-to-completion-job\"", json, StringComparison.Ordinal);
        Assert.Contains("\"environment-variable\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalJsonRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => DeploymentCanonicalJson.Serialize<object>(null!));
    }

    [Fact]
    public void HashReturnsLowercaseSha256()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", DeploymentCanonicalJson.Hash("abc"u8));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../intent.json")]
    [InlineData("nested/intent.json")]
    [InlineData("nested\\intent.json")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:artifact.json")]
    [InlineData("artifact?.json")]
    [InlineData("artifact*.json")]
    [InlineData("artifact\".json")]
    [InlineData("artifact<.json")]
    [InlineData("artifact>.json")]
    [InlineData("artifact|.json")]
    [InlineData("artifact.json.")]
    [InlineData("artifact.json ")]
    [InlineData("artifact\u0001.json")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("LPT1.log")]
    public void ArtifactFactoryRejectsUnsafeFileNames(string fileName)
    {
        var error = Assert.Throws<ArgumentException>(() => DeploymentArtifact.Create(fileName, [1]));
        Assert.Contains("ASDEPLOY119", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactFactoryRejectsNullContent()
    {
        Assert.Throws<ArgumentNullException>(() => DeploymentArtifact.Create("intent.json", null!));
    }

    [Fact]
    public void ArtifactFactoryCopiesContentAndComputesHash()
    {
        byte[] source = [1, 2, 3];
        var artifact = DeploymentArtifact.Create("intent.json", source);
        source[0] = 9;
        Assert.Equal([1, 2, 3], artifact.Content);
        Assert.Equal(DeploymentCanonicalJson.Hash(artifact.Content), artifact.Sha256);

        var exposedCopy = artifact.Content;
        exposedCopy[1] = 9;
        Assert.Equal([1, 2, 3], artifact.Content);
    }

    [Fact]
    public void DiagnosticAndExceptionExposeSafeStructuredContract()
    {
        var diagnostic = DeploymentDiagnostic.Create("ASDEPLOY199", "Problem.", "Cause.", "Fix.");
        var error = new DeploymentValidationException(diagnostic);
        Assert.Same(diagnostic, error.Diagnostic);
        Assert.Equal("ASDEPLOY199", diagnostic.Code);
        Assert.Equal("Problem.", diagnostic.Problem);
        Assert.Equal("Cause.", diagnostic.Cause);
        Assert.Equal("Fix.", diagnostic.Fix);
        Assert.EndsWith("#asdeploy199", diagnostic.Documentation.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("Cause: Cause.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fix: Fix.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeTargetProvesPortableRenderAndReadOnlyVerifyContracts()
    {
        IDeploymentTarget target = new FakeTarget();
        var renderRequest = new DeploymentRenderRequest(Intent(DeploymentIntentTests.CreateJob()), "bindings.json", "/output", "1.2.3");
        var render = await target.RenderAsync(renderRequest, CancellationToken.None);
        var verify = await target.VerifyAsync(new DeploymentVerifyRequest(render, DeploymentParityMode.Shadow), CancellationToken.None);
        Assert.Equal("fake", target.Name);
        Assert.Contains(DeploymentCapability.RunToCompletionJob, target.Capabilities);
        Assert.Single(render.Artifacts);
        Assert.True(verify.IsMatch);
        Assert.Equal(1, verify.ComparedFields);
        Assert.Empty(verify.Diagnostics);
        Assert.Equal("not-independently-verified", verify.AuthorizationStatus);
    }

    private static DeploymentIntent Intent(params MigrationJobIntent[] jobs) => new("Staging", DeploymentIntentTests.Revision(), jobs);

    private static int IndexOf(byte[] bytes, string value) => Encoding.UTF8.GetString(bytes).IndexOf(value, StringComparison.Ordinal);

    private sealed class FakeTarget : IDeploymentTarget
    {
        public string Name => "fake";

        public IReadOnlySet<DeploymentCapability> Capabilities { get; } = new HashSet<DeploymentCapability>
        {
            DeploymentCapability.RunToCompletionJob,
            DeploymentCapability.PrivateNetwork,
            DeploymentCapability.RelationalConnection,
        };

        public Task<DeploymentRenderResult> RenderAsync(DeploymentRenderRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = DeploymentArtifact.Create("deployment-intent.v1.json", DeploymentCanonicalJson.Serialize(request.Intent));
            return Task.FromResult(new DeploymentRenderResult(Name, [artifact]));
        }

        public Task<DeploymentVerifyResult> VerifyAsync(DeploymentVerifyRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DeploymentVerifyResult(true, 1, [], "not-independently-verified"));
        }
    }
}
