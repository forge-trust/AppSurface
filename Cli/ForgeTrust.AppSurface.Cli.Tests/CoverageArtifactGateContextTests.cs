using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactGateContextTests
{
    [Fact]
    public async Task AcquireAsync_StandaloneUsesReaderForInputAndWriterForSeparateOutput()
    {
        using var fixture = TestDirectory.Create();
        var repository = fixture.CreateDirectory("repo");
        var input = fixture.CreateDirectory("repo/coverage-input");
        var coverage = Path.Join(input, "coverage.xml");
        await RegisterRunAsync(input, repository, coverage, "coverage"u8.ToArray());
        var output = fixture.CreateDirectory("repo/coverage-reports");
        var request = new CoverageGateRequest(coverage, output, 0, 0);

        using (await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None))
        {
            using var additionalReader = CoverageArtifactOperationLease.Acquire(input, exclusive: false);
            Assert.Throws<CoverageExecutionException>(() =>
                CoverageArtifactOperationLease.Acquire(input, exclusive: true));
            Assert.Throws<CoverageExecutionException>(() =>
                CoverageArtifactOperationLease.Acquire(output, exclusive: false));
        }

        using var replacementWriter = CoverageArtifactOperationLease.Acquire(input, exclusive: true);
        using var outputReader = CoverageArtifactOperationLease.Acquire(output, exclusive: false);
    }

    [Fact]
    public async Task AcquireAsync_CopiedSidecarDoesNotAdoptOrLeaseAnUnregisteredInput()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var original = fixture.CreateDirectory("repo/original");
        var coverage = Path.Join(original, "coverage.xml");
        await RegisterRunAsync(original, repository, coverage, "coverage"u8.ToArray());
        var copied = fixture.CreateDirectory("repo/copied");
        var copiedCoverage = Path.Join(copied, "coverage.xml");
        File.Copy(coverage, copiedCoverage);
        File.Copy(Path.Join(original, CoverageArtifactSession.SidecarName),
            Path.Join(copied, CoverageArtifactSession.SidecarName));
        var output = fixture.CreateDirectory("repo/reports");

        using var context = await CoverageArtifactGateContext.AcquireAsync(
            new CoverageGateRequest(copiedCoverage, output, 0, 0), TextWriter.Null, CancellationToken.None);

        using var unmanagedWriter = CoverageArtifactOperationLease.Acquire(copied, exclusive: true);
        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(output, exclusive: false));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task AcquireAsync_InvalidSidecarDoesNotGrantInputOwnership(string sidecar)
    {
        using var fixture = TestDirectory.Create();
        var input = fixture.CreateDirectory("input");
        var coverage = Path.Join(input, "coverage.xml");
        await File.WriteAllTextAsync(coverage, "coverage");
        await File.WriteAllTextAsync(Path.Join(input, CoverageArtifactSession.SidecarName), sidecar);
        var output = fixture.CreateDirectory("reports");

        using var context = await CoverageArtifactGateContext.AcquireAsync(
            new CoverageGateRequest(coverage, output, 0, 0), TextWriter.Null, CancellationToken.None);

        using var unmanagedWriter = CoverageArtifactOperationLease.Acquire(input, exclusive: true);
    }

    [Fact]
    public async Task AcquireAsync_InvalidNestedSidecarStillProtectsTheBoundAncestor()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var input = fixture.CreateDirectory("repo/coverage");
        await RegisterRunAsync(input, repository, Path.Join(input, "coverage.xml"), "coverage"u8.ToArray());
        var nested = fixture.CreateDirectory("repo/coverage/nested");
        await File.WriteAllTextAsync(Path.Join(nested, CoverageArtifactSession.SidecarName), "not-json");
        var coverage = Path.Join(nested, "coverage.xml");
        await File.WriteAllTextAsync(coverage, "coverage");

        using var context = await CoverageArtifactGateContext.AcquireAsync(
            new CoverageGateRequest(coverage, fixture.CreateDirectory("reports"), 0, 0),
            TextWriter.Null, CancellationToken.None);

        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(input, exclusive: true));
    }

    [Fact]
    public async Task FindOwnedInputRoot_MemoPreservesEachReportsAncestorBound()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var input = fixture.CreateDirectory("repo/coverage");
        await RegisterRunAsync(input, repository, Path.Join(input, "coverage.xml"), "coverage"u8.ToArray());
        var paths = new List<string> { input };
        for (var index = 0; index < 35; index++)
            paths.Add(Directory.CreateDirectory(Path.Join(paths[^1], "d")).FullName);
        var observed = new Dictionary<string, bool>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        Assert.Equal(input, CoverageArtifactSession.FindOwnedInputRoot(Path.Join(input, "coverage.xml"), observed));
        Assert.Null(CoverageArtifactSession.FindOwnedInputRoot(Path.Join(paths[35], "coverage.xml"), observed));
        Assert.Equal(input, CoverageArtifactSession.FindOwnedInputRoot(Path.Join(paths[20], "coverage.xml"), observed));
        Assert.Equal(
            CoverageArtifactSession.FindOwnedInputRoot(Path.Join(paths[20], "coverage.xml")),
            CoverageArtifactSession.FindOwnedInputRoot(Path.Join(paths[20], "coverage.xml"), observed));
    }

    [Fact]
    public async Task ExecuteAsync_WhenOutputLeaseIsBusy_FailsBeforeEvaluatingCoverage()
    {
        using var fixture = TestDirectory.Create();
        var output = fixture.CreateDirectory("reports");
        using var reader = CoverageArtifactOperationLease.Acquire(output, exclusive: false);
        var command = new CoverageGateCommand
        {
            CoveragePath = Path.Join(fixture.Path, "missing-coverage.xml"),
            OutputDirectory = output,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var error = await Record.ExceptionAsync(async () =>
            await command.ExecuteAsync(console, CancellationToken.None));

        Assert.NotNull(error);
        Assert.Contains("ASCOV202", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, CoverageGateArtifactNames.Json)));
        Assert.False(File.Exists(Path.Join(output, CoverageGateArtifactNames.Markdown)));
    }

    [Fact]
    public async Task AcquireAsync_ReaderLeaseCoexistsWithReadersAndBlocksReplacementWriter()
    {
        using var fixture = TestDirectory.Create();
        var repository = fixture.CreateDirectory("repo");
        var input = fixture.CreateDirectory("repo/coverage-input");
        var coverage = Path.Join(input, "coverage.xml");
        await RegisterRunAsync(input, repository, coverage, "coverage"u8.ToArray());
        var output = fixture.CreateDirectory("repo/reports");
        var request = new CoverageGateRequest(coverage, output, 0, 0);

        var context = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None);
        using (context)
        {
            using var secondReader = CoverageArtifactOperationLease.Acquire(input, exclusive: false);
            Assert.Throws<CoverageExecutionException>(() =>
                CoverageArtifactOperationLease.Acquire(input, exclusive: true));
        }

        using var replacementWriter = CoverageArtifactOperationLease.Acquire(input, exclusive: true);
    }

    [Fact]
    public async Task RecordReportsAsync_UpdatesCompatibleCurrentRegistration()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var coverage = Path.Join(output, "coverage.xml");
        var provenance = new CoverageArtifactProvenance("revision", "clean", "configuration");
        using (var session = await BeginAsync(output, repository))
        {
            await File.WriteAllTextAsync(coverage, "coverage");
            session.Inventory.RecordFile(coverage);
            await session.CompleteAsync("failed", provenance);
        }

        string runId;
        using (var initialStore = CoverageArtifactStore.Open())
            runId = initialStore.GetCurrentRun(output)!.ManifestBase.RunId;

        var jsonReport = Path.Join(output, CoverageGateArtifactNames.Json);
        var markdownReport = Path.Join(output, CoverageGateArtifactNames.Markdown);
        var patchJsonReport = Path.Join(output, CoverageGateArtifactNames.PatchTargetsJson);
        var patchMarkdownReport = Path.Join(output, CoverageGateArtifactNames.PatchTargetsMarkdown);
        await File.WriteAllTextAsync(jsonReport, "gate json");
        await File.WriteAllTextAsync(markdownReport, "gate markdown");
        await File.WriteAllTextAsync(patchJsonReport, "patch json");
        await File.WriteAllTextAsync(patchMarkdownReport, "patch markdown");
        var request = new CoverageGateRequest(coverage, output, 0, 0);
        var result = new CoverageGateResult(
            coverage,
            new CoverageMetric(1, 1, 100),
            new CoverageMetric(1, 1, 100),
            0,
            0,
            true,
            jsonReport,
            markdownReport,
            PatchTargetsJsonPath: patchJsonReport,
            PatchTargetsMarkdownPath: patchMarkdownReport);

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal(runId, current.ManifestBase.RunId);
        Assert.Equal("failed", current.ManifestBase.Outcome);
        Assert.Equal(provenance, current.ManifestBase.Provenance);
        Assert.Contains(current.Inventory, entry => entry.Path == "coverage.xml");
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.Json);
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.Markdown);
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.PatchTargetsJson);
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.PatchTargetsMarkdown);
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageArtifactSession.SidecarName);
    }

    [Fact]
    public async Task AcquireAsync_DoesNotSearchPastThirtyTwoInputParents()
    {
        using var fixture = TestDirectory.Create();
        var repository = fixture.CreateDirectory("repo");
        var ownedRoot = fixture.CreateDirectory("repo/owned-input");
        var ownedArtifact = Path.Join(ownedRoot, "coverage.xml");
        await RegisterRunAsync(ownedRoot, repository, ownedArtifact, "coverage"u8.ToArray());
        var nestedDirectory = ownedRoot;
        for (var depth = 0; depth < 32; depth++)
            nestedDirectory = Directory.CreateDirectory(Path.Join(nestedDirectory, $"nested-{depth}")).FullName;
        var nestedCoverage = Path.Join(nestedDirectory, "coverage.xml");
        await File.WriteAllTextAsync(nestedCoverage, "nested coverage");
        var output = fixture.CreateDirectory("repo/reports");
        var request = new CoverageGateRequest(nestedCoverage, output, 0, 0);

        using var context = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None);
        using var outerWriter = CoverageArtifactOperationLease.Acquire(ownedRoot, exclusive: true);
    }

    [Fact]
    public async Task AcquireAsync_WhenSecondLeaseIsBusy_ReleasesEarlierLease()
    {
        using var fixture = TestDirectory.Create();
        var repository = fixture.CreateDirectory("repo");
        var input = fixture.CreateDirectory("repo/input");
        var coverage = Path.Join(input, "coverage.xml");
        await RegisterRunAsync(input, repository, coverage, "coverage"u8.ToArray());
        var output = fixture.CreateDirectory("repo/output");
        var request = new CoverageGateRequest(coverage, output, 0, 0);

        string inputIdentity;
        using (var inputProbe = CoverageArtifactOperationLease.Acquire(input, exclusive: false))
            inputIdentity = inputProbe.PhysicalIdentity;
        string outputIdentity;
        using (var outputProbe = CoverageArtifactOperationLease.Acquire(output, exclusive: false))
            outputIdentity = outputProbe.PhysicalIdentity;
        Assert.NotEqual(inputIdentity, outputIdentity);

        var firstPath = StringComparer.Ordinal.Compare(inputIdentity, outputIdentity) < 0 ? input : output;
        var secondPath = firstPath == input ? output : input;
        using var blocker = CoverageArtifactOperationLease.Acquire(secondPath, exclusive: true);

        var error = await Assert.ThrowsAsync<CoverageExecutionException>(async () =>
            await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None));

        Assert.Equal("ASCOV202", error.Code);
        using var releasedEarlierLease = CoverageArtifactOperationLease.Acquire(firstPath, exclusive: true);
    }

    private static async Task RegisterRunAsync(string output, string repository, string artifactPath, byte[] contents)
    {
        await File.WriteAllBytesAsync(artifactPath, contents);
        using var session = await BeginAsync(output, repository);
        session.Inventory.RecordFile(artifactPath);
        await session.CompleteAsync("success",
            new CoverageArtifactProvenance("seed-revision", "seed-state", "seed-configuration"));
    }

    private static Task<CoverageArtifactSession> BeginAsync(string output, string repository) =>
        CoverageArtifactSession.BeginAsync(output, repository, clean: false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, CancellationToken.None);

    private static IDisposable ConfigureStore(string path)
    {
        const string variable = "APPSURFACE_ARTIFACT_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, Path.Join(path, "private-store"));
        return new RestoreEnvironment(variable, previous);
    }

    private sealed class RestoreEnvironment(string name, string? value) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, value);
    }
}
