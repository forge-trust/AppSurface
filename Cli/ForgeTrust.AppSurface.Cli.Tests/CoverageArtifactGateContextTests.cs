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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordReportsAsync_ChangedSidecarCannotRedirectPublicationOutsideTheLeasedOutput(bool changeLength)
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var coverage = Path.Join(output, "coverage.xml");
        await RegisterRunAsync(output, repository, coverage, "coverage"u8.ToArray());
        var foreign = fixture.CreateDirectory("evil/coverage");
        var sentinel = Path.Join(foreign, CoverageArtifactSession.SidecarName);
        await File.WriteAllTextAsync(sentinel, "preserve unrelated metadata");
        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var originalSidecar = await File.ReadAllTextAsync(sidecarPath);
        using var originalDocument = System.Text.Json.JsonDocument.Parse(originalSidecar);
        var registeredOutput = originalDocument.RootElement.GetProperty("outputPath").GetString()!;
        var changed = originalSidecar.Replace(System.Text.Json.JsonSerializer.Serialize(registeredOutput),
            System.Text.Json.JsonSerializer.Serialize(CoverageRunOutputLease.NormalizePlatformPath(foreign)), StringComparison.Ordinal);
        Assert.NotEqual(originalSidecar, changed);
        Assert.Equal(originalSidecar.Length, changed.Length);
        await File.WriteAllTextAsync(sidecarPath, changeLength ? changed + " " : changed);
        var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath);
        var registryPath = Path.Join(storeHome.Path, "private-store", "registry.json");
        var registryBefore = await File.ReadAllBytesAsync(registryPath);
        var request = new CoverageGateRequest(coverage, output, 0, 0);
        var result = await WriteGateReportsAsync(coverage, output);
        using var errors = new StringWriter();

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, errors, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.Equal("preserve unrelated metadata", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal(registryBefore, await File.ReadAllBytesAsync(registryPath));
        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordReportsAsync_RenamedOutputUpdatesOnlyTheLeasedPhysicalDirectory()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var original = fixture.CreateDirectory("repo/original");
        await RegisterRunAsync(original, repository, Path.Join(original, "coverage.xml"), "coverage"u8.ToArray());
        var moved = Path.Join(repository, "moved");
        Directory.Move(original, moved);
        Directory.CreateDirectory(original);
        var sentinel = Path.Join(original, CoverageArtifactSession.SidecarName);
        await File.WriteAllTextAsync(sentinel, "preserve replacement directory");
        var coverage = Path.Join(moved, "coverage.xml");
        var request = new CoverageGateRequest(coverage, moved, 0, 0);
        var result = await WriteGateReportsAsync(coverage, moved);
        using var errors = new StringWriter();

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, errors, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.Equal("preserve replacement directory", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(errors.ToString());
        using var store = CoverageArtifactStore.Open();
        var registered = store.GetCurrentRun(moved)!;
        Assert.Equal(moved, registered.OutputPath);
        Assert.Contains(registered.Inventory, item => item.Path == CoverageGateArtifactNames.Json);
    }

    [Fact]
    public async Task RecordReportsAsync_UnregisteredOutputDoesNotCreateAnOptionalStore()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var output = fixture.CreateDirectory("reports");
        var coverage = fixture.WriteFile("coverage.xml", "coverage");
        var request = new CoverageGateRequest(coverage, output, 0, 0);
        var result = await WriteGateReportsAsync(coverage, output);

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Join(storeHome.Path, "private-store")));
    }

    private static async Task<CoverageGateResult> WriteGateReportsAsync(string coverage, string output)
    {
        var json = Path.Join(output, CoverageGateArtifactNames.Json);
        var markdown = Path.Join(output, CoverageGateArtifactNames.Markdown);
        await File.WriteAllTextAsync(json, "gate json");
        await File.WriteAllTextAsync(markdown, "gate markdown");
        return new CoverageGateResult(coverage, new CoverageMetric(1, 1, 100),
            new CoverageMetric(1, 1, 100), 0, 0, true, json, markdown);
    }

    [CoverageUnixFact]
    public async Task RecordReportsAsync_PathReplacementCannotAdoptAnotherRegisteredOutput()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/output");
        var replacement = fixture.CreateDirectory("repo/replacement");
        await RegisterRunAsync(output, repository, Path.Join(output, "coverage.xml"), "original coverage"u8.ToArray());
        await RegisterRunAsync(replacement, repository, Path.Join(replacement, "coverage.xml"), "replacement coverage"u8.ToArray());
        var sidecarBefore = await File.ReadAllBytesAsync(Path.Join(replacement, CoverageArtifactSession.SidecarName));
        var registryPath = Path.Join(storeHome.Path, "private-store", "registry.json");
        var registryBefore = await File.ReadAllBytesAsync(registryPath);
        var request = new CoverageGateRequest(Path.Join(output, "coverage.xml"), output, 0, 0);
        using var errors = new StringWriter();
        using var context = await CoverageArtifactGateContext.AcquireAsync(request, errors, CancellationToken.None);
        Directory.Move(output, Path.Join(repository, "moved"));
        Directory.Move(replacement, output);
        var result = await WriteGateReportsAsync(request.CoveragePath, output);

        await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(Path.Join(output, CoverageArtifactSession.SidecarName)));
        Assert.Equal(registryBefore, await File.ReadAllBytesAsync(registryPath));
        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [CoverageUnixFact]
    public async Task RecordReportsAsync_LinkedSidecarDoesNotCreateAnOptionalStore()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var output = fixture.CreateDirectory("reports");
        var coverage = fixture.WriteFile("coverage.xml", "coverage");
        var outside = fixture.WriteFile("outside.json", "preserve outside metadata");
        File.CreateSymbolicLink(Path.Join(output, CoverageArtifactSession.SidecarName), outside);
        var request = new CoverageGateRequest(coverage, output, 0, 0);
        var result = await WriteGateReportsAsync(coverage, output);
        using var errors = new StringWriter();

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, errors, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Join(storeHome.Path, "private-store")));
        Assert.Equal("preserve outside metadata", await File.ReadAllTextAsync(outside));
        Assert.NotNull(new FileInfo(Path.Join(output, CoverageArtifactSession.SidecarName)).LinkTarget);
        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRegisteredInventoryAsync_SharedLeaseCannotAuthorizePublication()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var coverage = Path.Join(output, "coverage.xml");
        await RegisterRunAsync(output, repository, coverage, "coverage"u8.ToArray());
        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath);
        var registryPath = Path.Join(storeHome.Path, "private-store", "registry.json");
        var registryBefore = await File.ReadAllBytesAsync(registryPath);
        var result = await WriteGateReportsAsync(coverage, output);
        using var errors = new StringWriter();
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: false);

        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output,
            [result.JsonReportPath, result.MarkdownReportPath], errors, outputLease);

        Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal(registryBefore, await File.ReadAllBytesAsync(registryPath));
        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordReportsAsync_CopiedOutputSidecarDoesNotAdoptItsUnregisteredDestination()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var original = fixture.CreateDirectory("repo/original");
        await RegisterRunAsync(original, repository, Path.Join(original, "coverage.xml"), "coverage"u8.ToArray());
        var copied = fixture.CreateDirectory("repo/copied");
        var coverage = Path.Join(copied, "coverage.xml");
        File.Copy(Path.Join(original, "coverage.xml"), coverage);
        var sidecarPath = Path.Join(copied, CoverageArtifactSession.SidecarName);
        File.Copy(Path.Join(original, CoverageArtifactSession.SidecarName), sidecarPath);
        var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath);
        var registryPath = Path.Join(storeHome.Path, "private-store", "registry.json");
        var registryBefore = await File.ReadAllBytesAsync(registryPath);
        var request = new CoverageGateRequest(coverage, copied, 0, 0);
        var result = await WriteGateReportsAsync(coverage, copied);
        using var errors = new StringWriter();

        using (var context = await CoverageArtifactGateContext.AcquireAsync(request, errors, CancellationToken.None))
            await context.RecordReportsAsync(request, result, CancellationToken.None);

        Assert.Empty(errors.ToString());
        Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal(registryBefore, await File.ReadAllBytesAsync(registryPath));
        Assert.Equal("gate json", await File.ReadAllTextAsync(result.JsonReportPath));
        using var store = CoverageArtifactStore.Open();
        Assert.Null(store.GetCurrentRun(copied));
        Assert.NotNull(store.GetCurrentRun(original));
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
