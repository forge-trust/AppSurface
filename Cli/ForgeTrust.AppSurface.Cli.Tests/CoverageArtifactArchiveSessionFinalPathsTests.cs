using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactArchiveSessionFinalPathsTests
{
    [Fact]
    public async Task StageAsync_MissingStageFailsWithoutChangingSource()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var bytes = "preserve source"u8.ToArray();
        var sourceFile = WriteUnder(source, "coverage.xml", bytes);
        var missingStage = Under(temp.Path, "missing-stage");

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(
            source, missingStage, Manifest(), [Inventory("coverage.xml", bytes)], 100, 100,
            TimeProvider.System, null, default));

        Assert.Contains("stage is missing", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceFile));
        Assert.False(Directory.Exists(missingStage));
    }

    [Fact]
    public async Task StageAsync_MissingSourceFailsAndLeavesExistingStageEmpty()
    {
        using var temp = TestDirectory.Create();
        var missingSource = Under(temp.Path, "missing-source");
        var stagePath = Under(temp.Path, "stage");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(stagePath, createMissing: true, requirePrivate: true)!;

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(
            missingSource, stagePath, Manifest(), [], 100, 100, TimeProvider.System, null, default));

        Assert.Contains("source is missing", error.Message, StringComparison.Ordinal);
        Assert.Empty(stage.EnumerateArtifactEntries(4));
    }

    [Fact]
    public async Task StageAsync_MissingInputParentFailsBeforeCreatingPayload()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var sentinel = WriteUnder(source, "retained.txt", "retained"u8.ToArray());
        var stagePath = Under(temp.Path, "stage");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(stagePath, createMissing: true, requirePrivate: true)!;

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.StageAsync(
            source, stagePath, Manifest(), [new("nested/coverage.xml", 0, Hash([]))],
            100, 100, TimeProvider.System, null, default));

        Assert.Contains("input parent is missing", error.Message, StringComparison.Ordinal);
        Assert.Equal("retained", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(stage.EnumerateArtifactEntries(4));
    }

    [Fact]
    public async Task StageAsync_EnforcesStoredByteLimitWhileStreaming()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var bytes = new byte[CoverageArtifactLimits.OperationBufferMaxBytes * 2 + 128];
        new Random(42).NextBytes(bytes);
        var sourceFile = WriteUnder(source, "coverage.bin", bytes);
        var stagePath = Under(temp.Path, "stage");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(stagePath, createMissing: true, requirePrivate: true)!;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.StageAsync(
            source, stagePath, Manifest(), [Inventory("coverage.bin", bytes)], bytes.Length,
            64 * 1024, TimeProvider.System, null, default));

        Assert.Contains("staging byte limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceFile));
        Assert.True(File.Exists(Under(stagePath, "00000001.gz")));
        Assert.False(File.Exists(Under(stagePath, "manifest.json")));
    }

    [Fact]
    public async Task RestorePayloadsAsync_MissingArchiveLeavesRestoreStageEmpty()
    {
        using var temp = TestDirectory.Create();
        var missingArchive = Under(temp.Path, "missing-archive");
        var restorePath = Under(temp.Path, "restore");
        using var restore = CoverageRunOutputLease.AcquireArtifactDirectory(restorePath, createMissing: true, requirePrivate: true)!;

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.RestorePayloadsAsync(
            missingArchive, restorePath, CompleteEmptyManifest(), 100, TimeProvider.System, null, default));

        Assert.Contains("Archive root is missing", error.Message, StringComparison.Ordinal);
        Assert.Empty(restore.EnumerateArtifactEntries(4));
    }

    [Fact]
    public async Task RestorePayloadsAsync_MissingRestoreStageDoesNotCreateIt()
    {
        using var temp = TestDirectory.Create();
        var archivePath = Under(temp.Path, "archive");
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, createMissing: true, requirePrivate: true)!;
        var missingRestore = Under(temp.Path, "missing-restore");

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.RestorePayloadsAsync(
            archivePath, missingRestore, CompleteEmptyManifest(), 100, TimeProvider.System, null, default));

        Assert.Contains("Restore stage is missing", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missingRestore));
        Assert.Empty(archive.EnumerateArtifactEntries(4));
    }

    [Fact]
    public async Task VerifyPayloadsAsync_MissingArchiveIsRejected()
    {
        using var temp = TestDirectory.Create();

        var error = await Assert.ThrowsAsync<IOException>(() => CoverageArtifactArchive.VerifyPayloadsAsync(
            Under(temp.Path, "missing-archive"), CompleteEmptyManifest(), default));

        Assert.Contains("archive is unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyPayloadsAsync_RejectsStoredLengthMismatch()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var bytes = "verified payload"u8.ToArray();
        WriteUnder(source, "coverage.xml", bytes);
        var archivePath = Under(temp.Path, "archive");
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, createMissing: true, requirePrivate: true)!;
        var manifest = await StageOneAsync(source, archivePath, "coverage.xml", bytes);
        var entry = Assert.Single(manifest.Files);
        var storedBefore = await File.ReadAllBytesAsync(Under(archivePath, entry.PayloadId + ".gz"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.VerifyPayloadsAsync(
            archivePath, manifest with { Files = [entry with { StoredLength = entry.StoredLength + 1 }] }, default));

        Assert.Contains("Stored archive length mismatch", error.Message, StringComparison.Ordinal);
        Assert.Equal(storedBefore, await File.ReadAllBytesAsync(Under(archivePath, entry.PayloadId + ".gz")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Under(source, "coverage.xml")));
    }

    [Fact]
    public async Task VerifyPayloadsAsync_RejectsExpansionPastRecordedLength()
    {
        using var temp = TestDirectory.Create();
        var source = temp.CreateDirectory("source");
        var bytes = new byte[] { 1, 2, 3, 4 };
        WriteUnder(source, "coverage.xml", bytes);
        var archivePath = Under(temp.Path, "archive");
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, createMissing: true, requirePrivate: true)!;
        var manifest = await StageOneAsync(source, archivePath, "coverage.xml", bytes);
        var entry = Assert.Single(manifest.Files);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CoverageArtifactArchive.VerifyPayloadsAsync(
            archivePath, manifest with { Files = [entry with { OriginalLength = bytes.Length - 1 }] }, default));

        Assert.Contains("expands beyond its recorded length", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Under(source, "coverage.xml")));
    }

    [Fact]
    public async Task BeginAsync_CorruptPreviousArchiveWarnsAndPreservesCurrentRun()
    {
        using var temp = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configured = ConfigureStore(storeHome.Path);
        var repository = temp.CreateDirectory("repository");
        var output = temp.CreateDirectory("output");
        var coverageFile = WriteUnder(output, "coverage.xml", "previous coverage"u8.ToArray());
        var prior = await RegisterRunAsync(output, repository, coverageFile);
        var sidecarPath = Under(output, CoverageArtifactSession.SidecarName);
        var sidecarBefore = await File.ReadAllBytesAsync(sidecarPath);
        string archivePath;
        string payloadPath;
        using (var store = CoverageArtifactStore.Open())
        {
            var archived = await store.SnapshotAsync(prior, pin: false, automatic: false, CancellationToken.None);
            archivePath = TestPathUtils.PathUnder(store.RootPath, "archives", prior.ManifestBase.RunId);
            var payload = Assert.Single(archived.Files, item => item.Path == "coverage.xml");
            payloadPath = Under(archivePath, payload.PayloadId + ".gz");
        }
        await File.WriteAllBytesAsync(payloadPath, [0x5a]);
        using var errors = new StringWriter();

        using var session = await BeginAsync(output, repository, clean: true, errors: errors);
        session.VerifyBinding();

        Assert.Contains("ASCOV200 snapshot", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("previous coverage", await File.ReadAllTextAsync(coverageFile));
        Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal(new byte[] { 0x5a }, await File.ReadAllBytesAsync(payloadPath));
        using var check = CoverageArtifactStore.Open();
        Assert.Equal(prior.ManifestBase.RunId, check.GetCurrentRun(output)!.ManifestBase.RunId);
    }

    [Fact]
    public async Task UpdateRegisteredInventoryAsync_SidecarEnvelopeLimitIsOptionalAndNonMutating()
    {
        using var temp = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configured = ConfigureStore(storeHome.Path);
        var repository = temp.CreateDirectory("repository");
        var output = temp.CreateDirectory("output");
        var producer = WriteUnder(output, "coverage.xml", "producer"u8.ToArray());
        var prior = await RegisterRunAsync(output, repository, producer);
        var reportBytes = "gate report"u8.ToArray();
        var report = WriteUnder(output, "gate-report.json", reportBytes);
        var sidecarPath = Under(output, CoverageArtifactSession.SidecarName);
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath))!.AsObject();
        sidecar["manifestBase"]!["provenance"]!["configurationFingerprint"] = string.Empty;
        var compactSidecar = Encoding.UTF8.GetBytes(sidecar.ToJsonString());
        using var compactInput = new MemoryStream(compactSidecar);
        var compactRun = CoverageArtifactSession.ReadCurrent(compactInput);
        var reportEntry = new CoverageArtifactInventoryEntry("gate-report.json", reportBytes.Length, Hash(reportBytes));
        var projected = JsonSerializer.SerializeToUtf8Bytes(
            compactRun with { Inventory = compactRun.Inventory.Append(reportEntry).ToArray() },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var paddingLength = checked((int)(CoverageArtifactLimits.RunInventoryMaxBytes - projected.LongLength + 1));
        Assert.True(paddingLength > 0);
        sidecar["manifestBase"]!["provenance"]!["configurationFingerprint"] = new string('x', paddingLength);
        var oversizedPublishedSidecar = Encoding.UTF8.GetBytes(sidecar.ToJsonString());
        Assert.True(oversizedPublishedSidecar.LongLength <= CoverageArtifactLimits.RunInventoryMaxBytes);
        Assert.True(projected.LongLength + paddingLength > CoverageArtifactLimits.RunInventoryMaxBytes);
        await File.WriteAllBytesAsync(sidecarPath, oversizedPublishedSidecar);
        using var errors = new StringWriter();

        using (CoverageArtifactOperationLease.Acquire(output, exclusive: true))
            await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [report], errors);

        Assert.Contains("ASCOV201 gate-inventory", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(oversizedPublishedSidecar, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal("producer", await File.ReadAllTextAsync(producer));
        Assert.Equal("gate report", await File.ReadAllTextAsync(report));
        using var check = CoverageArtifactStore.Open();
        Assert.Equal(prior.Inventory.ToArray(), check.GetCurrentRun(output)!.Inventory.ToArray());
    }

    private static async Task<CoverageArtifactCurrentRun> RegisterRunAsync(string output, string repository, string file)
    {
        using var session = await BeginAsync(output, repository);
        session.Inventory.RecordFile(file);
        await session.CompleteAsync("failed", Provenance());
        using var store = CoverageArtifactStore.Open();
        return store.GetCurrentRun(output)!;
    }

    private static Task<CoverageArtifactSession> BeginAsync(string output, string repository, bool clean = false,
        TextWriter? errors = null) => CoverageArtifactSession.BeginAsync(output, repository, clean,
        CoverageTextWriters.Create(TextWriter.Null, errors ?? TextWriter.Null), TimeProvider.System, CancellationToken.None);

    private static async Task<CoverageArtifactRunManifest> StageOneAsync(string source, string archive, string relative, byte[] bytes)
        => await CoverageArtifactArchive.StageAsync(source, archive, Manifest(), [Inventory(relative, bytes)],
            bytes.Length, 1_000_000, TimeProvider.System, null, default);

    private static CoverageArtifactInventoryEntry Inventory(string path, byte[] bytes)
        => new(path, bytes.Length, Hash(bytes));

    private static CoverageArtifactRunManifest Manifest() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        OutputIdentity = "output-id",
        RepositoryIdentity = "repository-id",
        Version = "candidate-test",
        Outcome = "success",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static CoverageArtifactRunManifest CompleteEmptyManifest() => Manifest() with
    {
        Complete = true,
        ArchivedAtUtc = DateTimeOffset.UtcNow,
        Files = []
    };

    private static CoverageArtifactProvenance Provenance() =>
        new("revision-fingerprint", "clean-fingerprint", "configuration-fingerprint");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Under(string root, string relative)
        => TestPathUtils.PathUnder(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string WriteUnder(string root, string relative, byte[] bytes)
    {
        var path = Under(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IDisposable ConfigureStore(string parent)
    {
        const string variable = "APPSURFACE_ARTIFACT_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, Under(parent, "private-store"));
        return new RestoreEnvironment(variable, previous);
    }

    private sealed class RestoreEnvironment(string name, string? value) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, value);
    }
}
