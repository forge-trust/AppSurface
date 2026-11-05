using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInPublicationTests
{
    private const string PolicyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CaptureStage = "capture";
    private static readonly CoverageFanInBounds Bounds = new(
        XmlBytes: 512 * 1024,
        JsonBytes: 64 * 1024,
        TotalBytes: 2 * 1024 * 1024,
        Depth: 64,
        Lines: 100_000,
        Elements: 500_000,
        Attributes: 32,
        TokenCharacters: 64 * 1024,
        Branches: 100_000,
        TotalRecords: 250_000);

    [Theory]
    [InlineData("capture", "success")]
    [InlineData("composition", "success")]
    [InlineData("acceptance", "success")]
    [InlineData("rejected-gates", "failed")]
    public async Task FreshPublicationRegistersExactInventoryAndCommitsMatchingMarker(string stage, string outcome)
    {
        using var fixture = new Fixture();
        var files = Payloads();

        var published = await fixture.PublishAsync(stage, files: files);

        var markerPath = Path.Join(fixture.Output, CoverageFanInIO.CompletionName);
        using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
        Assert.Equal(2, marker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(stage, marker.RootElement.GetProperty("stage").GetString());
        Assert.Equal(published.Generation, marker.RootElement.GetProperty("generation").GetString());
        Assert.Equal(PolicyDigest, marker.RootElement.GetProperty("policyDigest").GetString());
        Assert.Equal(files.Count, marker.RootElement.GetProperty("files").EnumerateObject().Count());

        using var store = CoverageArtifactStore.OpenReadOnly();
        var run = Assert.IsType<CoverageArtifactCurrentRun>(store.GetCurrentRun(fixture.Output));
        Assert.Equal(published.Generation, run.ManifestBase.RunId);
        Assert.Equal(outcome, run.ManifestBase.Outcome);
        Assert.Equal("coverage-fanin-v2:" + stage, run.ManifestBase.Provenance?.ConfigurationFingerprint);
        Assert.Equal(files.Keys.Append(CoverageFanInIO.CompletionName).Order(StringComparer.Ordinal),
            run.Inventory.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.Equal(run.Inventory.OrderBy(item => item.Path, StringComparer.Ordinal),
            published.Artifacts.Select(item => new CoverageArtifactInventoryEntry(item.Role,
                item.Length, item.Sha256)).OrderBy(item => item.Path, StringComparer.Ordinal));

        using var output = CoverageRunOutputLease.AcquireArtifactDirectory(fixture.Output, false, false)!;
        var actualNames = output.EnumerateArtifactEntries(32)
            .Where(entry => entry.Name != ".appsurface-coverage-operation.lock")
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(files.Keys.Append(CoverageFanInIO.CompletionName).Order(StringComparer.Ordinal), actualNames);
    }

    [Fact]
    public async Task PublicationAcceptsMaximumNativeInventoryPayloadAndRejectsTheNextFile()
    {
        using var fixture = new Fixture();
        var maximumPayloadFiles = CoverageArtifactLimits.MaximumFiles - 1;
        var maximum = Enumerable.Range(0, maximumPayloadFiles)
            .ToDictionary(index => $"artifact-{index:D4}.json", index => new[] { (byte)index });
        await CoverageFanInPublication.PublishAsync(fixture.Output, fixture.Repository, CaptureStage,
            Guid.NewGuid().ToString("N"), PolicyDigest, maximum, false,
            Bounds with { JsonBytes = 2 * 1024 * 1024 }, CancellationToken.None);

        var otherOutput = Path.Join(fixture.OutputParent, "too-many");
        var tooMany = Enumerable.Range(0, maximumPayloadFiles + 1)
            .ToDictionary(index => $"artifact-{index:D4}.json", index => new[] { (byte)index });
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.PublishAsync(
            output: otherOutput, files: tooMany));
        Assert.False(Directory.Exists(otherOutput));
    }

    [Theory]
    [InlineData("unknown-stage")]
    [InlineData("invalid-operation-id")]
    [InlineData("invalid-policy-digest")]
    public async Task InvalidPublicationIdentityIsRejectedBeforeCreatingOutput(string defect)
    {
        using var fixture = new Fixture();
        var operationId = defect == "invalid-operation-id" ? "not-a-guid" : Guid.NewGuid().ToString("N");
        var digest = defect == "invalid-policy-digest" ? "not-a-digest" : PolicyDigest;
        var stage = defect == "unknown-stage" ? "verification" : CaptureStage;

        await Assert.ThrowsAsync<ArgumentException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, stage, operationId, digest, Payloads(), false,
            Bounds, CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Output));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(fixture.Output));
    }

    [Theory]
    [InlineData("reserved-marker")]
    [InlineData("hidden-stage-name")]
    [InlineData("component-traversal")]
    public async Task InvalidPublicationArtifactNamesAreRejectedWithoutCreatingCustody(string defect)
    {
        using var fixture = new Fixture();
        var name = defect switch
        {
            "reserved-marker" => CoverageFanInIO.CompletionName,
            "hidden-stage-name" => ".unexpected.json",
            "component-traversal" => "../outside.json",
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var publish = () => fixture.PublishAsync(files:
            new Dictionary<string, byte[]> { [name] = [1, 2, 3] });
        if (defect == "component-traversal")
            await Assert.ThrowsAsync<InvalidDataException>(publish);
        else
            await Assert.ThrowsAsync<ArgumentException>(publish);

        Assert.False(Directory.Exists(fixture.Output));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(fixture.Output));
    }

    [Theory]
    [InlineData("xml-per-file")]
    [InlineData("json-per-file")]
    [InlineData("total-bytes")]
    public async Task PublicationByteBudgetRejectionsLeaveNoOutputOrRegistration(string budget)
    {
        using var fixture = new Fixture();
        var limits = budget switch
        {
            "xml-per-file" => Bounds with { XmlBytes = 2 },
            "json-per-file" => Bounds with { JsonBytes = 2 },
            "total-bytes" => Bounds with { TotalBytes = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(budget))
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, CaptureStage, Guid.NewGuid().ToString("N"), PolicyDigest,
            Payloads(), false, limits, CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Output));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(fixture.Output));
    }

    [Fact]
    public async Task ForeignOutputIsPreservedWhenReplacementIsRequested()
    {
        using var fixture = new Fixture();
        using (var output = CoverageRunOutputLease.AcquireArtifactDirectory(fixture.Output, true, true)!)
        using (var foreignFile = output.OpenArtifactFile("foreign.txt", createNew: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("keep foreign bytes");
            foreignFile.Write(bytes);
            foreignFile.Flush(flushToDisk: true);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(replaceOwned: true));

        Assert.Equal("keep foreign bytes", File.ReadAllText(Path.Join(fixture.Output, "foreign.txt")));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("unknown")]
    public async Task ChangedOrUnknownOwnedInventoryIsPreservedOnReplacement(string mutation)
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var payload = Path.Join(fixture.Output, "coverage.xml");
        var originalMarker = File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName));
        if (mutation == "changed") File.WriteAllText(payload, "<coverage changed='true'/>");
        else File.WriteAllText(Path.Join(fixture.Output, "unknown.txt"), "keep unknown bytes");
        var preservedPayload = File.ReadAllBytes(payload);
        var preservedUnknown = mutation == "unknown"
            ? File.ReadAllBytes(Path.Join(fixture.Output, "unknown.txt"))
            : null;

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(replaceOwned: true));

        Assert.Equal(preservedPayload, File.ReadAllBytes(payload));
        Assert.Equal(originalMarker, File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        if (preservedUnknown is not null)
            Assert.Equal(preservedUnknown, File.ReadAllBytes(Path.Join(fixture.Output, "unknown.txt")));
    }

    [Fact]
    public async Task OwnedReplacementSnapshotsPriorCaptureAndReplaysExactPrivateInventory()
    {
        using var fixture = new Fixture();
        var first = await fixture.PublishAsync();

        var second = await fixture.PublishAsync(replaceOwned: true,
            files: Payloads("<coverage lines-covered='2'/>", "second receipt"));

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.True(Directory.Exists(Path.Join(fixture.StoreHome, "archives", first.Generation)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        var run = Assert.IsType<CoverageArtifactCurrentRun>(store.GetCurrentRun(fixture.Output));
        Assert.Equal(second.Generation, run.ManifestBase.RunId);
        var status = store.GetStatus();
        Assert.Equal(1, status.ArchivedRuns);
        Assert.True(status.PinnedBytes > 0);
        Assert.Equal(Payloads("<coverage lines-covered='2'/>", "second receipt").Keys
                .Append(CoverageFanInIO.CompletionName).Order(StringComparer.Ordinal),
            run.Inventory.Select(item => item.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReplacementWithDifferentStagePreservesOwnedGeneration()
    {
        using var fixture = new Fixture();
        var prior = await fixture.PublishAsync();
        var priorMarker = File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(
            stage: "composition", replaceOwned: true));

        Assert.Equal(priorMarker, File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Equal(prior.Generation, store.GetCurrentRun(fixture.Output)?.ManifestBase.RunId);
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
    }

    [Fact]
    public async Task ReaderThenWriterAndWriterThenReaderFailFastWithoutChangingGeneration()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var before = File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName));

        using (CoverageArtifactOperationLease.Acquire(fixture.Output, exclusive: false))
        {
            var busy = await Assert.ThrowsAsync<CoverageExecutionException>(() =>
                fixture.PublishAsync(replaceOwned: true));
            Assert.Contains("ASCOV202", busy.Message, StringComparison.Ordinal);
        }

        var published = await fixture.PublishAsync(replaceOwned: true, beforeCommit: () =>
        {
            var busy = Assert.Throws<CoverageExecutionException>(() =>
                CoverageArtifactOperationLease.Acquire(fixture.Output, exclusive: false));
            Assert.Contains("ASCOV202", busy.Message, StringComparison.Ordinal);
        });
        Assert.NotEmpty(published.Generation);
        Assert.NotEqual(before, File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesNoCompletionMarkerAndAfterCommitKeepsCommittedResult()
    {
        using var fixture = new Fixture();
        using var beforeCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PublishAsync(
            token: beforeCancellation.Token, beforeCommit: beforeCancellation.Cancel));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));

        var afterOutput = Path.Join(fixture.OutputParent, "after-cancel");
        using var afterCancellation = new CancellationTokenSource();
        var committed = await fixture.PublishAsync(output: afterOutput, token: afterCancellation.Token,
            afterCommit: afterCancellation.Cancel);
        Assert.True(afterCancellation.IsCancellationRequested);
        Assert.True(File.Exists(Path.Join(afterOutput, CoverageFanInIO.CompletionName)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Equal(committed.Generation, store.GetCurrentRun(afterOutput)?.ManifestBase.RunId);
    }

    [CoverageUnixTheory]
    [InlineData(0)]
    public async Task ReplacingRootAncestorBeforeCommitPreventsMarkerPublication(int _)
    {
        using var fixture = new Fixture();
        var retainedParent = fixture.OutputParent + "-retained";

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.PublishAsync(beforeCommit: () =>
        {
            Directory.Move(fixture.OutputParent, retainedParent);
            Directory.CreateDirectory(fixture.OutputParent);
        }));

        Assert.True(Directory.Exists(Path.Join(retainedParent, Path.GetFileName(fixture.Output))));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Fact]
    public async Task StagingCreationFaultLeavesInspectablePartialStageWithoutRegistrationOrMarker()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => fixture.PublishAsync(onEffectsStarted: () =>
            throw new IOException("injected staging creation fault")));

        Assert.True(Directory.Exists(fixture.Output));
        Assert.Single(Directory.GetDirectories(fixture.Output, ".fanin-stage-*"));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(fixture.Output));
    }

    [Fact]
    public async Task FinalSourceValidationFailurePreventsCompletionAfterDurableStaging()
    {
        using var fixture = new Fixture();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, "capture", Guid.NewGuid().ToString("N"), PolicyDigest,
            Payloads(), false, Bounds, CancellationToken.None,
            validateBeforeCommit: _ =>
            {
                calls++;
                throw new InvalidDataException("Source changed after staging.");
            }));
        Assert.Equal(1, calls);
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionMetadataMustFitBothPerFileAndInvocationBudgets(bool totalBudget)
    {
        using var fixture = new Fixture();
        var files = Payloads();
        var limits = totalBudget ? Bounds with { TotalBytes = files.Values.Sum(x => x.Length) }
            : Bounds with { JsonBytes = 64 };
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, CaptureStage, Guid.NewGuid().ToString("N"), PolicyDigest,
            files, false, limits, CancellationToken.None));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(fixture.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedCustodyMustBeExclusiveAndBelongToTheExactOutput(bool wrongRoot)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Output);
        var leaseRoot = wrongRoot ? Path.Join(fixture.OutputParent, "other") : fixture.Output;
        Directory.CreateDirectory(leaseRoot);
        using (CoverageArtifactOperationLease.Acquire(leaseRoot, true)) { }
        using var lease = CoverageArtifactOperationLease.Acquire(leaseRoot, wrongRoot);
        await Assert.ThrowsAsync<IOException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, CaptureStage, Guid.NewGuid().ToString("N"), PolicyDigest,
            Payloads(), false, Bounds, CancellationToken.None, heldOutputLease: lease));
        Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Fact]
    public async Task FreshBorrowedOutputRejectsUnexpectedFilesBeforeCreatingAStage()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Output);
        using var lease = CoverageArtifactOperationLease.Acquire(fixture.Output, true);
        var foreign = Path.Join(fixture.Output, "foreign.json");
        File.WriteAllText(foreign, "preserve");
        await Assert.ThrowsAsync<IOException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, CaptureStage, Guid.NewGuid().ToString("N"), PolicyDigest,
            Payloads(), false, Bounds, CancellationToken.None, heldOutputLease: lease));
        Assert.Equal("preserve", File.ReadAllText(foreign));
        Assert.Empty(Directory.GetDirectories(fixture.Output));
    }

    [Fact]
    public async Task ReplacementRefusesPriorPayloadThatExceedsTheNewBudget()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var marker = File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName));
        await Assert.ThrowsAsync<InvalidDataException>(() => CoverageFanInPublication.PublishAsync(
            fixture.Output, fixture.Repository, CaptureStage, Guid.NewGuid().ToString("N"), PolicyDigest,
            new Dictionary<string, byte[]> { ["new.xml"] = [1] }, true, Bounds with { XmlBytes = 1 }, CancellationToken.None));
        Assert.Equal(marker, File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        Assert.True(File.Exists(Path.Join(fixture.Output, "coverage.xml")));
    }

    [Theory]
    [InlineData("zero-files")]
    [InlineData("nonhex-digest")]
    [InlineData("unknown-output-name")]
    public async Task PublicationRejectsEveryUnsupportedMetadataShapeBeforeCreatingAStage(string defect)
    {
        using var fixture = new Fixture();
        var digest = defect == "nonhex-digest" ? new string('z', 64) : PolicyDigest;
        var files = defect == "zero-files" ? new Dictionary<string, byte[]>() : Payloads();
        var output = defect == "unknown-output-name" ? Path.GetPathRoot(fixture.Output)! : fixture.Output;
        var error = await Record.ExceptionAsync(() => CoverageFanInPublication.PublishAsync(output, fixture.Repository,
            CaptureStage, Guid.NewGuid().ToString("N"), digest, files, false, Bounds, CancellationToken.None));
        Assert.NotNull(error);
        Assert.True(error is ArgumentException or InvalidDataException);
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public async Task OccupiedDestinationCannotBeAdoptedByFreshPublication()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var marker = File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName));
        await Assert.ThrowsAsync<IOException>(() => fixture.PublishAsync());
        Assert.Equal(marker, File.ReadAllBytes(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    [Fact]
    public async Task ReplacementRefusesSameLengthPayloadTamperingAndMissingRegisteredFile()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var payload = Path.Join(fixture.Output, "coverage.xml");
        var original = File.ReadAllText(payload);
        File.WriteAllText(payload, original.Replace("'1'", "'0'", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(replaceOwned: true));
        Assert.Contains("'0'", File.ReadAllText(payload));
        File.WriteAllText(payload, original);
        File.Delete(Path.Join(fixture.Output, "receipt.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PublishAsync(replaceOwned: true));
        Assert.True(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
        Assert.Equal(original, File.ReadAllText(payload));
    }

    [Fact]
    public async Task BeforeCommitValidationMustFinishBeforeMarkerAndPostcommitObservationCannotUndoCommit()
    {
        using var fixture = new Fixture();
        var reached = false;
        var published = await CoverageFanInPublication.PublishAsync(fixture.Output, fixture.Repository, CaptureStage,
            Guid.NewGuid().ToString("N"), PolicyDigest, Payloads(), false, Bounds, CancellationToken.None,
            validateBeforeCommit: _ =>
            {
                reached = true;
                Assert.False(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
                return Task.CompletedTask;
            }, afterCommit: () => throw new IOException("observer-fault"));
        Assert.True(reached);
        Assert.NotEmpty(published.Generation);
        Assert.True(File.Exists(Path.Join(fixture.Output, CoverageFanInIO.CompletionName)));
    }

    private static IReadOnlyDictionary<string, byte[]> Payloads(
        string xml = "<coverage lines-covered='1'/>", string receipt = "first receipt") =>
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["coverage.xml"] = System.Text.Encoding.UTF8.GetBytes(xml),
            ["receipt.json"] = System.Text.Encoding.UTF8.GetBytes("{\"receipt\":\"" + receipt + "\"}")
        };

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly string? _previousHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");

        internal Fixture()
        {
            OutputParent = _temp.CreateDirectory("publication-parent");
            Repository = _temp.CreateDirectory("repository");
            StoreHome = Path.Join(_temp.Path, "private-store");
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", StoreHome);
        }

        internal string OutputParent { get; }
        internal string Output => Path.Join(OutputParent, "fan-in");
        internal string Repository { get; }
        internal string StoreHome { get; }

        internal Task<CoverageFanInPublished> PublishAsync(
            string stage = CaptureStage,
            string? output = null,
            IReadOnlyDictionary<string, byte[]>? files = null,
            bool replaceOwned = false,
            CancellationToken token = default,
            Action? onCreated = null,
            Action? beforeCommit = null,
            Action? afterCommit = null,
            Action? onEffectsStarted = null) =>
            CoverageFanInPublication.PublishAsync(output ?? Output, Repository, stage,
                Guid.NewGuid().ToString("N"), PolicyDigest, files ?? Payloads(), replaceOwned,
                Bounds, token, onCreated, beforeCommit, afterCommit, onEffectsStarted);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _previousHome);
            _temp.Dispose();
        }
    }
}
