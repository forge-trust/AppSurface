using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactRetentionTests
{
    private const string Cobertura = "<coverage line-rate=\"1\" />";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalizeDetailed_RemovesRawOnlyWhenVerifiedCleanupRequested(bool removeRaw)
    {
        using var temp = new TestTempDirectory();
        var invocation = CreateCollectorInvocation(temp.Path, Cobertura);

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(invocation, CancellationToken.None, removeRedundantRaw: removeRaw);

        Assert.Equal("produced", result.Status);
        Assert.Equal(Cobertura, await File.ReadAllTextAsync(Path.Join(invocation.ProjectOutputDirectory, "coverage.cobertura.xml")));
        Assert.Equal(!removeRaw, File.Exists(RawPath(invocation)));
    }

    [Fact]
    public async Task NormalizeDetailed_MalformedRawIsRetained()
    {
        using var temp = new TestTempDirectory();
        var invocation = CreateCollectorInvocation(temp.Path, "<coverage");

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(invocation, CancellationToken.None, removeRedundantRaw: true);

        Assert.Equal("malformed", result.Status);
        Assert.True(File.Exists(RawPath(invocation)));
    }

    [Fact]
    public async Task NormalizeDetailed_FailedPromotionRetainsRaw()
    {
        using var temp = new TestTempDirectory();
        var invocation = CreateCollectorInvocation(temp.Path, Cobertura);

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(invocation, CancellationToken.None,
            commitGate: _ => throw new IOException("promotion rejected"), removeRedundantRaw: true);

        Assert.Equal("unreadable", result.Status);
        Assert.True(File.Exists(RawPath(invocation)));
        Assert.False(File.Exists(Path.Join(invocation.ProjectOutputDirectory, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task NormalizeDetailed_CancellationDuringPromotionRetainsRaw()
    {
        using var temp = new TestTempDirectory();
        var invocation = CreateCollectorInvocation(temp.Path, Cobertura);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoverageRunDriverStrategy.NormalizeDetailedAsync(
            invocation, cancellation.Token, commitGate: _ => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }, removeRedundantRaw: true));

        Assert.True(File.Exists(RawPath(invocation)));
    }

    [Theory]
    [InlineData("managed", false, "managed", true)]
    [InlineData("full", false, "full", true)]
    [InlineData("managed", true, "managed", false)]
    [InlineData("full", true, "full", false)]
    public void CreateRequest_MapsRetentionAndNoClean(string mode, bool noClean, string expectedMode, bool expectedClean)
    {
        var command = new CoverageRunCommand(CreateWorkflow()) { ArtifactRetention = mode, NoClean = noClean };

        var request = command.CreateRequest();

        Assert.Equal(expectedMode, request.ArtifactRetention);
        Assert.Equal(expectedClean, request.Clean);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public void CreateRequest_RejectsInvalidRetentionBeforeExecution(string mode)
    {
        var command = new CoverageRunCommand(CreateWorkflow()) { ArtifactRetention = mode };

        var error = Assert.Throws<CliFx.CommandException>(() => command.CreateRequest());

        Assert.Contains("ASCOV208", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceProducerDeclaration_PreservesLegacyConstructorAndDefaultsRetention()
    {
        var legacy = new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], [], [], 60, null);
        var oldJson = """{"Id":"coverage","Kind":"coverage","Version":"1.0.0","RequiredResources":[],"AssertionIds":[],"ArtifactSlots":[],"TimeoutSeconds":60,"CoverageGate":null}""";

        Assert.Equal("managed", legacy.CoverageArtifactRetention);
        Assert.Equal("managed", JsonSerializer.Deserialize<EvidenceProducerDeclaration>(oldJson)!.CoverageArtifactRetention);
    }

    [Fact]
    public void EvidenceProducerDeclaration_SerializesRetentionAndAllowsInitCompatibility()
    {
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], [], [], 60)
        {
            CoverageArtifactRetention = "full"
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(declaration));

        Assert.Equal("full", json.RootElement.GetProperty(nameof(EvidenceProducerDeclaration.CoverageArtifactRetention)).GetString());
    }

    private static CoverageRunWorkflow CreateWorkflow()
    {
        var runner = new CliWrapCoverageRunProcessRunner();
        return new CoverageRunWorkflow(runner, new CoverageRunReportGenerator(runner, new ReportGeneratorPackageLocator()), TimeProvider.System);
    }

    private static CoverageRunDriverInvocation CreateCollectorInvocation(string root, string xml)
    {
        var projectOutput = Path.Join(root, "project");
        Directory.CreateDirectory(projectOutput);
        var raw = Path.Join(projectOutput, "raw");
        Directory.CreateDirectory(raw);
        var rawPath = Path.Join(raw, "coverage.cobertura.xml");
        File.WriteAllText(rawPath, xml);
        return new CoverageRunDriverInvocation(CoverageRunDriver.Collector, projectOutput, raw, []);
    }

    private static string RawPath(CoverageRunDriverInvocation invocation) =>
        Path.Join(invocation.RawResultsDirectory!, "coverage.cobertura.xml");

    private sealed class TestTempDirectory : IDisposable
    {
        public TestTempDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "coverage-retention-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
