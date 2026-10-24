using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactCallerFinalPathsTests
{
    private const string Coverage = "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPreviewDescribesVerifiedHistoryWithoutMutatingCurrentOrArchive(bool archived)
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("output");
        var domain = Path.Join(temp.Path, "domain");
        using var configured = new StoreEnvironment(domain);
        CoverageRunOutputGuard.Prepare(output, temp.Path, [], false);
        var path = Path.Join(output, "coverage.cobertura.xml");
        using (var session = await CoverageArtifactSession.BeginAsync(output, temp.Path, false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, default))
        {
            File.WriteAllText(path, Coverage);
            session.Inventory.RecordFile(path);
            await session.CompleteAsync("success", new("revision", "clean", "policy"));
        }
        using (var store = CoverageArtifactStore.Open())
        {
            if (archived) await store.SnapshotAsync(store.GetCurrentRun(output)!, false, false, default);
        }
        using var console = new FakeInMemoryConsole();
        await new CoverageCleanCommand(new TestResultsCleanupWorkflow()) { OutputDirectory = output }.ExecuteAsync(console, default);
        Assert.Contains(archived ? "A verified historical archive already exists" : "No verified historical archive was established", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Equal(Coverage, File.ReadAllText(path));
        using var after = CoverageArtifactStore.OpenReadOnly();
        Assert.Equal(archived, await after.HasVerifiedArchiveAsync(output, default));
        Assert.Equal(after.RootPath, after.GetStatus().RootPath);
    }

    [Fact]
    public async Task BroadCleanupPreviewExplainsNonparticipatingWritersAndPreservesTestResults()
    {
        using var temp = TestDirectory.Create();
        var results = temp.CreateDirectory("TestResults");
        var report = Path.Join(results, "coverage.cobertura.xml");
        File.WriteAllText(report, Coverage);
        using var console = new FakeInMemoryConsole();
        await new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        { RootDirectory = temp.Path, All = true }.ExecuteAsync(console, default);
        Assert.Contains("Older or nonparticipating writers", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("Pinned history archives remain outside this sweep", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Equal(Coverage, File.ReadAllText(report));
    }

    [Fact]
    public async Task MergeWithoutCleanRecordsNewProducerSummaryAndPreservesUnownedFile()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "domain"));
        var source = temp.CreateDirectory("source");
        File.WriteAllText(Path.Join(source, "coverage.cobertura.xml"), Coverage);
        var output = temp.CreateDirectory("output");
        CoverageRunOutputGuard.Prepare(output, temp.Path, [], false);
        var unrelated = Path.Join(output, "keep.txt");
        File.WriteAllText(unrelated, "preserve");
        using var console = new FakeInMemoryConsole();
        await new CoverageMergeWorkflow(new Generator(), TimeProvider.System).MergeAsync(new(source, output, false), console, default);
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        using var store = CoverageArtifactStore.OpenReadOnly();
        var current = store.GetCurrentRun(output)!;
        Assert.Contains(current.Inventory, entry => entry.Path == "reportgenerator/Summary.txt");
        Assert.DoesNotContain(current.Inventory, entry => entry.Path == "keep.txt");
        Assert.Equal("success", current.ManifestBase.Outcome);
    }

    [Fact]
    public async Task GateWithIdenticalInputAndOutputUsesOneExclusiveLease()
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("output");
        File.WriteAllText(Path.Join(output, CoverageArtifactSession.SidecarName), "ownership marker");
        var request = new CoverageGateRequest(Path.Join(output, "coverage.xml"), output, 0, 0);
        using (await CoverageArtifactGateContext.AcquireAsync(request, TextWriter.Null, default))
            Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(output, false));
        using var released = CoverageArtifactOperationLease.Acquire(output, true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CursorPublicationPreservesDataWhenItsRequiredParentIsMissing(bool missingDomain)
    {
        using var temp = TestDirectory.Create();
        var domain = Path.Join(temp.Path, "domain");
        using var store = CoverageArtifactStore.Open(domain);
        var missing = missingDomain ? domain : Path.Join(domain, "staging");
        Directory.Move(missing, missing + "-retained");
        if (missingDomain)
            Assert.Throws<DirectoryNotFoundException>(() => CoverageArtifactInspectionCursorStore.Write(store, new string('a', 64), ["pending"], TimeProvider.System));
        else
            Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(store, new string('a', 64), ["pending"], TimeProvider.System));
        Assert.False(Directory.Exists(missing));
        Assert.True(Directory.Exists(missing + "-retained"));
    }

    [Fact]
    public void ObservationPublicationRejectsItsReservedSlotBeforeChangingExistingBytes()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        var reserved = Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName);
        File.WriteAllText(reserved, "keep");
        Assert.Throws<ArgumentException>(() => CoverageArtifactMetadataPublication.Write(directory,
            CoverageArtifactMetadataPublication.StagingName, "new"u8.ToArray(), 32));
        Assert.Equal("keep", File.ReadAllText(reserved));
    }

    [CoverageUnixTheory]
    [InlineData(17)]
    [InlineData(CoverageArtifactLimits.OperationBufferMaxBytes + 128)]
    public void InventoryRejectsLengthChangesEvenWhenItsFinalReadMatchesTheOriginalLength(int size)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This test requires Unix file-mode or file-sharing semantics.");
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "report.xml");
        File.WriteAllBytes(path, new byte[size]);
        var clock = new MutationClock(() =>
        {
            using var changed = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            changed.SetLength(1);
        });
        var inventory = new CoverageArtifactInventory(temp.Path, clock);
        inventory.RecordFile(path);
        var error = Assert.Throws<IOException>(() => inventory.Capture(default));
        Assert.Contains("changed during inventory", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, new FileInfo(path).Length);
    }

    [Fact]
    public void FileObservationsCompareCapturedFactsAndExposeTheirValues()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "report.xml");
        File.WriteAllText(path, "prior");
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        var original = inventory.ObserveBeforeWrite(path)!.Value;
        var (length, ticks) = original;
        Assert.Equal(5, length);
        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, ticks);
        File.WriteAllText(path, "replacement bytes");
        var changed = inventory.ObserveBeforeWrite(path)!.Value;
        Assert.NotEqual(original, changed);
        inventory.RecordAfterWrite(path, original);
        Assert.Throws<InvalidDataException>(() => inventory.Capture(default));
        Assert.Equal("replacement bytes", File.ReadAllText(path));
    }

    private sealed class Generator : ICoverageRunReportGenerator
    {
        public Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> reports, string output, CancellationToken token)
        {
            var report = Path.Join(output, "Cobertura.xml");
            var summary = Path.Join(output, "Summary.txt");
            File.WriteAllText(report, Coverage);
            File.WriteAllText(summary, "producer summary");
            return Task.FromResult(new CoverageRunMergeResult(0, report, summary));
        }
    }

    private sealed class MutationClock(Action mutate) : TimeProvider
    {
        private int _calls;
        public override long GetTimestamp()
        {
            if (++_calls == 3) mutate();
            return 0;
        }
    }

    private sealed class StoreEnvironment : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal StoreEnvironment(string domain)
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", domain);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _previous);
    }
}
