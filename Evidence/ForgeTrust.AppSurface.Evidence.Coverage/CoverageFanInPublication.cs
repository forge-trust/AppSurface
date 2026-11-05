using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>One marker-last committed native generation and its exact retained artifact inventory.</summary>
internal sealed record CoverageFanInPublished(string Generation, IReadOnlyList<CoverageFanInEngineArtifact> Artifacts);

/// <summary>Publishes only freshly staged files under exclusive native custody and private registration.</summary>
/// <remarks>A completion marker and matching private registration are both required by readers. Registration
/// precedes commit; it cannot make a missing marker complete. Interrupted stages remain for inspection and are
/// never adopted by retries. Replacing a generation first snapshots its verified inventory under existing policy.</remarks>
internal static class CoverageFanInPublication
{
    /// <summary>Reserves one storage inventory entry for the completion marker.</summary>
    internal const int MaximumPayloadFiles = CoverageArtifactLimits.MaximumFiles - 1;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> Stages = new(["capture", "composition", "acceptance", "rejected-gates"], StringComparer.Ordinal);

    internal static async Task<CoverageFanInPublished> PublishAsync(string output, string repository, string stage,
        string operationId, string policyDigest, IReadOnlyDictionary<string, byte[]> files, bool replaceOwned,
        CoverageFanInBounds bounds, CancellationToken token, Action? onCreated = null,
        Action? beforeCommit = null, Action? afterCommit = null, Action? onEffectsStarted = null,
        CoverageArtifactOperationLease? heldOutputLease = null, bool imported = false,
        Action<long>? admitMetadata = null, Func<CancellationToken, Task>? validateBeforeCommit = null)
    {
        token.ThrowIfCancellationRequested();
        output = Path.GetFullPath(output); repository = Path.GetFullPath(repository);
        if (!Stages.Contains(stage) || files.Count is < 1 or > MaximumPayloadFiles || !Guid.TryParseExact(operationId, "N", out _)
            || policyDigest.Length != 64 || policyDigest.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Invalid publication metadata.");
        long admitted = 0;
        foreach (var (name, bytes) in files)
        {
            CoverageRunOutputLease.ValidateArtifactComponent(name);
            if (name == CoverageFanInIO.CompletionName || name.StartsWith('.')) throw new ArgumentException("Reserved output name.");
            if (bytes.LongLength > (name.EndsWith(".xml", StringComparison.Ordinal) ? bounds.XmlBytes : bounds.JsonBytes))
                throw new InvalidDataException("Publication exceeds per-file budget.");
            admitted = checked(admitted + bytes.LongLength);
            if (admitted > bounds.TotalBytes) throw new InvalidDataException("Publication exceeds total byte budget.");
        }
        var namePart = Path.GetFileName(output);
        CoverageRunOutputLease.ValidateArtifactComponent(namePart);
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(output)!, false, false)
            ?? throw new DirectoryNotFoundException("Publication parent must already exist.");
        var existed = parent.EnumerateArtifactEntries(100000).Any(x => x.Name == namePart);
        if (existed && !replaceOwned && heldOutputLease is null) throw new IOException("Fresh publication destination already exists.");
        using var root = existed ? CoverageRunOutputLease.AcquireArtifactDirectory(output, false, true)
            ?? throw new IOException("Publication output unavailable.")
            : parent.CreateArtifactDirectory(namePart, onCreated: onCreated);
        using var ownedWriter = heldOutputLease is null ? CoverageArtifactOperationLease.Acquire(output, true) : null;
        var writer = heldOutputLease ?? ownedWriter!;
        if (!writer.IsExclusive || writer.PhysicalIdentity != root.ArtifactDirectoryIdentity)
            throw new IOException("Borrowed output custody is incompatible.");
        writer.VerifyBinding();
        using var source = CoverageRunOutputLease.AcquireArtifactDirectory(repository, false, false)
            ?? throw new IOException("Publication source context unavailable.");
        using var store = CoverageArtifactStore.Open();
        CoverageArtifactCurrentRun? previous = null;
        IReadOnlyList<CoverageArtifactDirectoryEntry> priorEntries = [];
        if (existed && (heldOutputLease is null || replaceOwned))
        {
            previous = ValidatePrevious(root, output, stage, store, bounds, token);
            priorEntries = root.EnumerateArtifactEntries(MaximumPayloadFiles + 3);
        }
        else if (root.EnumerateArtifactEntries(2).Any(x => x.Name != ".appsurface-coverage-operation.lock"))
            throw new IOException("Fresh output changed before publication.");
        token.ThrowIfCancellationRequested();
        var generation = Guid.NewGuid().ToString("N");
        var stagingName = ".fanin-stage-" + generation;
        using var staging = root.CreateArtifactDirectory(stagingName, onCreated: onEffectsStarted);
        var stagingIdentity = staging.ArtifactDirectoryIdentity;
        var facts = new Dictionary<string, PayloadFact>(StringComparer.Ordinal);
        var stagedIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using (var file = staging.OpenArtifactFile(name, true))
            {
                stagedIdentities[name] = CoverageRunOutputLease.GetArtifactObjectIdentity(file.SafeFileHandle);
                for (var offset = 0; offset < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(81920, bytes.Length - offset);
                    file.Write(bytes, offset, count); offset += count;
                }
                file.Flush(flushToDisk: true);
            }
            using var verify = staging.OpenArtifactFile(name, false);
            var hash = CoverageFanInIO.HashStream(verify, bytes.LongLength, token);
            if (verify.Length != bytes.LongLength || hash != CoverageFanInIO.Hash(bytes))
                throw new IOException("Staged publication integrity differs.");
            facts.Add(name, new(bytes.LongLength, hash));
        }
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            stage,
            generation,
            operationId,
            policyDigest,
            physicalIdentity = root.ArtifactDirectoryIdentity,
            materialization = imported ? "imported" : "local",
            files = facts
        }, Json);
        if (marker.LongLength > bounds.JsonBytes || checked(admitted + marker.LongLength) > bounds.TotalBytes)
            throw new InvalidDataException("Publication metadata exceeds byte budget.");
        admitMetadata?.Invoke(marker.LongLength);
        const string markerStage = "complete-marker.json";
        string markerIdentity;
        using (var stream = staging.OpenArtifactFile(markerStage, true))
        {
            markerIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(stream.SafeFileHandle);
            stream.Write(marker); stream.Flush(flushToDisk: true);
        }
        if (previous is not null)
        {
            // Snapshot succeeds before any previous payload is retired. Capture dependencies remain pinned.
            await store.SnapshotAsync(previous, pin: stage == "capture", automatic: false, token,
                sourceLeaseHeld: true).ConfigureAwait(false);
            _ = ValidatePrevious(root, output, stage, store, bounds, token, stagingName);
            var oldMarker = priorEntries.Single(x => x.Name == CoverageFanInIO.CompletionName);
            root.DeleteArtifactEntry(oldMarker.Name, 1, oldMarker.Identity);
            foreach (var entry in priorEntries.Where(x => x.Name is not (CoverageFanInIO.CompletionName or ".appsurface-coverage-operation.lock")))
            {
                token.ThrowIfCancellationRequested();
                root.DeleteArtifactEntry(entry.Name, 1, entry.Identity);
            }
        }
        var markerTemporaryName = ".fanin-marker-" + generation + ".json";
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            staging.MoveArtifactEntry(name, root, name, expectedSourceIdentity: stagedIdentities[name]);
        }
        staging.MoveArtifactEntry(markerStage, root, markerTemporaryName, expectedSourceIdentity: markerIdentity);
        staging.Dispose();
        root.DeleteArtifactEntry(stagingName, 1, stagingIdentity);
        var inventory = facts.Select(x => new CoverageArtifactInventoryEntry(x.Key, x.Value.Length, x.Value.Sha256))
            .Append(new(CoverageFanInIO.CompletionName, marker.LongLength, CoverageFanInIO.Hash(marker))).ToArray();
        var run = new CoverageArtifactCurrentRun(new()
        {
            RunId = generation,
            OutputIdentity = root.ArtifactDirectoryIdentity,
            RepositoryIdentity = source.ArtifactDirectoryIdentity,
            Version = "coverage-fanin-v2",
            Outcome = stage == "rejected-gates" ? "failed" : "success",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Provenance = new(null, null, "coverage-fanin-v2:" + stage),
            Complete = false
        }, inventory, output, repository);
        await store.RegisterAsync(run, token).ConfigureAwait(false);
        // All required artifacts, registration, result metadata and staging cleanup precede commit.
        var artifacts = inventory.Select(x => new CoverageFanInEngineArtifact(x.Path,
            Path.Join(output, x.Path), x.Length, x.Sha256)).ToArray();
        beforeCommit?.Invoke();
        token.ThrowIfCancellationRequested();
        if (validateBeforeCommit is not null) await validateBeforeCommit(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        writer.VerifyBinding(); root.VerifyArtifactBinding(); source.VerifyArtifactBinding();
        root.MoveArtifactEntry(markerTemporaryName, root, CoverageFanInIO.CompletionName,
            expectedSourceIdentity: markerIdentity);
        // Commit is the linearization point. Postcommit test observation cannot turn it into an uncommitted failure.
        try { afterCommit?.Invoke(); } catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        return new(generation, artifacts);
    }

    private static CoverageArtifactCurrentRun ValidatePrevious(CoverageRunOutputLease root, string output, string stage,
        CoverageArtifactStore store, CoverageFanInBounds bounds, CancellationToken token, string? admittedStage = null)
    {
        token.ThrowIfCancellationRequested();
        var run = store.GetCurrentRun(output) ?? throw new InvalidDataException("Unregistered output cannot be replaced.");
        if (run.ManifestBase.OutputIdentity != root.ArtifactDirectoryIdentity
            || run.ManifestBase.Provenance?.ConfigurationFingerprint != "coverage-fanin-v2:" + stage)
            throw new InvalidDataException("Output purpose or physical registration differs.");
        var expected = run.Inventory.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        expected.Add(".appsurface-coverage-operation.lock");
        if (admittedStage is not null) expected.Add(admittedStage);
        var entries = root.EnumerateArtifactEntries(MaximumPayloadFiles + 3);
        if (entries.Any(x => !expected.Contains(x.Name) || x.IsDirectory && x.Name != admittedStage)
            || run.Inventory.Any(x => !entries.Any(e => e.Name == x.Path)))
            throw new InvalidDataException("Unknown or incomplete owned output must be preserved.");
        foreach (var item in run.Inventory)
        {
            token.ThrowIfCancellationRequested();
            if (item.Length > (item.Path.EndsWith(".xml", StringComparison.Ordinal) ? bounds.XmlBytes : bounds.JsonBytes))
                throw new InvalidDataException("Prior output exceeds declared budget.");
            using var file = root.OpenArtifactFile(item.Path, false);
            if (file.Length != item.Length || CoverageFanInIO.HashStream(file, item.Length, token) != item.Sha256)
                throw new InvalidDataException("Modified output must be preserved.");
        }
        using var markerFile = root.OpenArtifactFile(CoverageFanInIO.CompletionName, false);
        using var marker = JsonDocument.Parse(markerFile);
        if (marker.RootElement.GetProperty("schemaVersion").GetInt32() != 2
            || marker.RootElement.GetProperty("stage").GetString() != stage
            || marker.RootElement.GetProperty("generation").GetString() != run.ManifestBase.RunId
            || marker.RootElement.GetProperty("physicalIdentity").GetString() != root.ArtifactDirectoryIdentity)
            throw new InvalidDataException("Prior completion metadata differs.");
        return run;
    }
    private sealed record PayloadFact(long Length, string Sha256);
}
