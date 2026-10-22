using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Holds one compatible output mutation context across collection, gate reports and evidence hashing.</summary>
/// <remarks>Required output coordination remains authoritative even when optional private history is unavailable.</remarks>
internal sealed class CoverageArtifactSession : IDisposable
{
    /// <summary>Gets the versioned current-run sidecar filename; it never grants authority over unrelated files.</summary>
    internal const string SidecarName = ".appsurface-coverage-run.json";
    private readonly CoverageArtifactOperationLease _operation;
    private readonly CoverageArtifactStore? _store;
    private readonly CoverageTextWriters _writers;
    private readonly TimeProvider _clock;
    private readonly string _repository;
    private readonly string _repositoryIdentity;
    private readonly CoverageArtifactProvenanceReader _provenanceReader = new();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _created;
    private string? _completedOutcome;
    private CoverageArtifactProvenance? _completedProvenance;
    private int _expiredRuns;
    private bool _disposed;

    private CoverageArtifactSession(CoverageArtifactOperationLease operation, CoverageArtifactStore? store,
        string output, string repository, string repositoryIdentity, CoverageTextWriters writers, TimeProvider clock)
    {
        _operation = operation;
        _store = store;
        _writers = writers;
        _clock = clock;
        _repository = repository;
        _repositoryIdentity = repositoryIdentity;
        _created = clock.GetUtcNow();
        Inventory = new(output, clock);
        Warnings = new(repository, store, clock, writers.Error);
    }

    /// <summary>Gets this invocation's explicit writer-owned inventory.</summary>
    internal CoverageArtifactInventory Inventory { get; }

    /// <summary>Gets the shared, bounded warning context for preflight, settings, and terminal observations.</summary>
    internal CoverageArtifactWarnings Warnings { get; }

    /// <summary>Acquires output mutation authority, then attempts a prior-run snapshot at the normal clean boundary.</summary>
    /// <param name="output">Absolute safe output root, already validated by the command.</param>
    /// <param name="repository">Repository/worktree root whose outputs are accounted together.</param>
    /// <param name="clean">Whether existing canonical artifacts will undergo normal pre-run cleanup.</param>
    /// <param name="writers">Primary command's local output streams.</param>
    /// <param name="clock">Shared monotonic clock.</param>
    /// <param name="token">Original cancellation token.</param>
    /// <param name="operationLease">Optional exclusive output lease acquired with other roots in deterministic identity order.</param>
    /// <returns>A required run-long lease plus best-effort optional storage.</returns>
    internal static async Task<CoverageArtifactSession> BeginAsync(string output, string repository, bool clean,
        CoverageTextWriters writers, TimeProvider clock, CancellationToken token,
        CoverageArtifactOperationLease? operationLease = null)
    {
        token.ThrowIfCancellationRequested();
        using (var directory = CoverageRunOutputLease.AcquireArtifactDirectory(output, true, false)) { }
        var operation = operationLease ?? CoverageArtifactOperationLease.Acquire(output, exclusive: true);
        CoverageArtifactStore? store = null;
        try
        {
            if (!operation.IsExclusive) throw new ArgumentException("A coverage session requires an exclusive output lease.", nameof(operationLease));
            operation.VerifyBinding();
            using var repositoryDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(repository, false, false)
                ?? throw new IOException("Repository root is missing.");
            var routineBudget = new CoverageArtifactWorkBudget(clock,
                TimeSpan.FromMilliseconds(CoverageArtifactLimits.RoutineMaintenanceBudgetMilliseconds));
            try { store = CoverageArtifactStore.Open(clock: clock); }
            catch (Exception exception) when (IsOptionalFailure(exception))
            {
                await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV201", "storage", exception), writers.Error);
            }
            var session = new CoverageArtifactSession(operation, store, output, repository,
                repositoryDirectory.ArtifactDirectoryIdentity, writers, clock);
            await session.Warnings.ObserveAsync(terminal: false, inventory: null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (store is not null)
            {
                try
                {
                    session._expiredRuns += (await store.MaintainAsync(apply: true, token, routineBudget,
                        heldSourceLease: operation).ConfigureAwait(false)).Count;
                }
                catch (Exception exception) when (IsOptionalFailure(exception))
                {
                    await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV204", "maintenance", exception), writers.Error);
                }
            }
            if (clean && store is not null)
            {
                try
                {
                    routineBudget.Check(token);
                    var previous = store.GetCurrentRun(output);
                    if (previous is null && File.Exists(Path.Join(output, SidecarName)))
                    {
                        using var previousRoot = CoverageRunOutputLease.AcquireArtifactDirectory(output, false, false)!;
                        using var previousFile = previousRoot.OpenArtifactFile(SidecarName, false, false);
                        var candidate = ReadCurrent(previousFile);
                        if (candidate.ManifestBase.OutputIdentity != previousRoot.ArtifactDirectoryIdentity
                            || candidate.ManifestBase.RepositoryIdentity != repositoryDirectory.ArtifactDirectoryIdentity)
                            throw new InvalidDataException("A current sidecar does not match the physical output and repository.");
                        using var rawSidecar = previousRoot.OpenArtifactFile(SidecarName, false, false);
                        var sidecarHash = Convert.ToHexString(SHA256.HashData(rawSidecar)).ToLowerInvariant();
                        previous = candidate with
                        {
                            OutputPath = output,
                            RepositoryPath = repository,
                            Inventory = candidate.Inventory.Append(new CoverageArtifactInventoryEntry(SidecarName, rawSidecar.Length, sidecarHash)).ToArray()
                        };
                        await store.RegisterAsync(previous, token).ConfigureAwait(false);
                    }
                    if (previous is not null)
                    {
                        session.VerifyCurrentSidecar(previous);
                        await store.SnapshotAsync(previous, pin: false, automatic: true, token,
                            sourceLeaseHeld: true, routineBudget: routineBudget).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsOptionalFailure(exception))
                {
                    var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV200", "snapshot", exception) with
                    { SourceStillAvailable = true, SourceReplaced = false };
                    await WriteDiagnosticSafelyAsync(diagnostic, writers.Error);
                }
            }
            if (store is not null)
            {
                try
                {
                    session._expiredRuns += (await store.MaintainAsync(apply: true, token, routineBudget,
                    heldSourceLease: operation).ConfigureAwait(false)).Count;
                }
                catch (Exception exception) when (IsOptionalFailure(exception))
                {
                    await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV204", "maintenance", exception), writers.Error);
                }
            }
            return session;
        }
        catch { store?.Dispose(); operation.Dispose(); throw; }
    }

    /// <summary>Verifies the required output identity before a participating mutation or read.</summary>
    internal void VerifyBinding() => _operation.VerifyBinding();

    /// <summary>Publishes current-run registration without replacing the primary command outcome.</summary>
    /// <param name="outcome">success, failed or interrupted; never inferred from stored historical XML.</param>
    /// <param name="provenance">Bounded informational driver/configuration policy facts.</param>
    internal async Task CompleteAsync(string outcome, CoverageArtifactProvenance provenance)
    {
        _completedOutcome = outcome;
        _completedProvenance ??= provenance.GitRevisionFingerprint is not null || provenance.GitStateFingerprint is not (null or "unknown")
            ? provenance
            : await _provenanceReader.ReadAsync(_repository, provenance).ConfigureAwait(false);
        provenance = _completedProvenance;
        IReadOnlyList<CoverageArtifactInventoryEntry>? entries = null;
        try { entries = Inventory.Capture(CancellationToken.None); }
        catch (Exception exception) when (IsOptionalFailure(exception))
        {
            await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV201", "inventory", exception), _writers.Error);
        }
        try { await Warnings.ObserveAsync(terminal: true, entries, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { }
        if (_store is null || entries is null) return;
        try
        {
            VerifyBinding();
            using var output = CoverageRunOutputLease.AcquireArtifactDirectory(Inventory.OutputDirectory, false, false)
                ?? throw new IOException("Output root is missing.");
            var manifest = new CoverageArtifactRunManifest
            {
                RunId = _runId,
                OutputIdentity = output.ArtifactDirectoryIdentity,
                RepositoryIdentity = _repositoryIdentity,
                Version = CoverageArtifactInventory.ProducingVersion,
                Outcome = outcome,
                CreatedAtUtc = _created,
                Provenance = provenance,
                Complete = false
            };
            var run = new CoverageArtifactCurrentRun(manifest, entries, Inventory.OutputDirectory, _repository);
            var json = JsonSerializer.SerializeToUtf8Bytes(run, JsonOptions);
            if (json.LongLength > CoverageArtifactLimits.RunInventoryMaxBytes) throw new InvalidDataException("Current sidecar metadata exceeds its limit.");
            var staged = ".artifact-sidecar-" + Guid.NewGuid().ToString("N");
            using (var stream = output.OpenArtifactFile(staged, true, requirePrivate: false))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            output.MoveArtifactEntry(staged, output, SidecarName, replaceFile: true);
            var sidecarEntry = new CoverageArtifactInventoryEntry(SidecarName, json.LongLength,
                Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant());
            run = run with { Inventory = entries.Append(sidecarEntry).ToArray() };
            await _store.RegisterAsync(run, CancellationToken.None).ConfigureAwait(false);
            var status = _store.GetStatus();
            try
            {
                await _writers.Output.WriteLineAsync($"Coverage storage: current={entries.Sum(entry => entry.Length)} bytes; history={status.ArchivedBytes} bytes; expired={_expiredRuns} runs; protected={checked(status.CurrentBytes + status.PinnedBytes)} bytes; management=available; accounting={(status.IsComplete ? "complete" : "partial")}.").ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOptionalFailure(exception)) { }
        }
        catch (Exception exception) when (IsOptionalFailure(exception))
        {
            await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV201", "register", exception), _writers.Error);
        }
    }

    /// <summary>Records reports written under this session and republishes the same run metadata.</summary>
    /// <param name="publishedFiles">Report paths successfully written by the current gate call.</param>
    internal async Task RecordPublishedArtifactsAsync(IEnumerable<string> publishedFiles)
    {
        foreach (var path in publishedFiles) Inventory.RecordFile(path);
        if (_completedOutcome is not null && _completedProvenance is not null)
            await CompleteAsync(_completedOutcome, _completedProvenance).ConfigureAwait(false);
    }

    /// <summary>Updates an existing registered run's sidecar and registry after an independent gate writes reports.</summary>
    /// <param name="outputDirectory">The gate output directory already held under an exclusive lease.</param>
    /// <param name="publishedFiles">Report paths successfully written by the current gate call.</param>
    /// <param name="errors">Best-effort diagnostic output; failures never replace the gate result.</param>
    /// <remarks>Existing non-report files must still match their recorded lengths and hashes. A nonparticipating writer's changes are never adopted under earlier provenance.</remarks>
    internal static async Task UpdateRegisteredInventoryAsync(string outputDirectory,
        IEnumerable<string> publishedFiles, TextWriter errors)
    {
        try
        {
            using var store = CoverageArtifactStore.Open();
            var registered = store.GetCurrentRun(outputDirectory);
            if (registered is null) return;
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(outputDirectory, false, false)
                ?? throw new IOException("Gate output root is missing.");
            using var sidecarStream = root.OpenArtifactFile(SidecarName, false, requirePrivate: false);
            var sidecar = ReadCurrent(sidecarStream);
            if (sidecar.ManifestBase.RunId != registered.ManifestBase.RunId
                || sidecar.ManifestBase.OutputIdentity != root.ArtifactDirectoryIdentity
                || !sidecar.Inventory.SequenceEqual(registered.Inventory.Where(entry => entry.Path != SidecarName)))
                throw new InvalidDataException("Current sidecar does not match its registered run metadata.");

            var reportPaths = publishedFiles.Select(Path.GetFullPath).ToArray();
            var inventory = new CoverageArtifactInventory(outputDirectory, TimeProvider.System);
            foreach (var entry in sidecar.Inventory) inventory.RecordFile(Path.Join(outputDirectory, entry.Path));
            foreach (var path in reportPaths) inventory.RecordFile(path);
            var entries = inventory.Capture(CancellationToken.None);
            var reports = reportPaths.Select(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/'))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var captured = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            foreach (var previous in sidecar.Inventory.Where(entry => !reports.Contains(entry.Path)))
            {
                if (!captured.TryGetValue(previous.Path, out var current) || current.Length != previous.Length
                    || !string.Equals(current.Sha256, previous.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Producer-owned coverage bytes changed before gate inventory publication.");
            }
            sidecarStream.Dispose();
            await PublishSidecarAndRegistrationAsync(sidecar, entries, store).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOptionalFailure(exception))
        {
            await WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic.Failure("ASCOV201", "gate-inventory", exception), errors);
        }
    }

    /// <summary>Releases compatible output authority after every participating caller has finished.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _store?.Dispose(); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { }
        _operation.Dispose();
    }

    private void VerifyCurrentSidecar(CoverageArtifactCurrentRun previous)
    {
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Inventory.OutputDirectory, false, false)
            ?? throw new IOException("Previous output is missing.");
        using var input = root.OpenArtifactFile(SidecarName, false, requirePrivate: false);
        var stored = ReadCurrent(input);
        if (stored.ManifestBase.RunId != previous.ManifestBase.RunId
            || stored.ManifestBase.OutputIdentity != root.ArtifactDirectoryIdentity
            || !stored.Inventory.SequenceEqual(previous.Inventory.Where(entry => entry.Path != SidecarName)))
            throw new InvalidDataException("Current manifest does not match the registered producer inventory.");
    }

    private static async Task PublishSidecarAndRegistrationAsync(CoverageArtifactCurrentRun run,
        IReadOnlyList<CoverageArtifactInventoryEntry> entries, CoverageArtifactStore store)
    {
        var sidecarEntries = entries.Where(entry => entry.Path != SidecarName).ToArray();
        var sidecarRun = run with { Inventory = sidecarEntries };
        var json = JsonSerializer.SerializeToUtf8Bytes(sidecarRun, JsonOptions);
        if (json.LongLength > CoverageArtifactLimits.RunInventoryMaxBytes)
            throw new InvalidDataException("Current sidecar metadata exceeds its limit.");
        using var output = CoverageRunOutputLease.AcquireArtifactDirectory(run.OutputPath, false, false)
            ?? throw new IOException("Gate output root is missing.");
        var staged = ".artifact-sidecar-" + Guid.NewGuid().ToString("N");
        using (var stream = output.OpenArtifactFile(staged, true, requirePrivate: false))
        {
            stream.Write(json);
            stream.Flush(flushToDisk: true);
        }
        output.MoveArtifactEntry(staged, output, SidecarName, replaceFile: true);
        var sidecarEntry = new CoverageArtifactInventoryEntry(SidecarName, json.LongLength,
            Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant());
        var registeredRun = sidecarRun with { Inventory = sidecarEntries.Append(sidecarEntry).ToArray() };
        await store.RegisterAsync(registeredRun, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Finds a physically bound managed input root without adopting copied metadata.</summary>
    /// <param name="coveragePath">An input report whose containing directories are inspected, up to 32 levels.</param>
    /// <param name="observedRoots">Optional acquisition-local observations shared by one merge's shard discovery; never retain or reuse across commands.</param>
    /// <returns>The nearest root whose bounded sidecar names its actual physical output, or null.</returns>
    /// <remarks>Missing, malformed, inaccessible and foreign sidecars do not grant ownership or require an input lock.
    /// A copied report remains eligible for the existing arbitrary-XML numeric gate. First-party Evidence retains
    /// its explicit execution session; this observation is neither source attestation nor registration.</remarks>
    internal static string? FindOwnedInputRoot(string coveragePath, IDictionary<string, bool>? observedRoots = null)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(coveragePath))!);
        for (var depth = 0; directory is not null && depth < 32; depth++, directory = directory.Parent)
        {
            if (observedRoots is not null && observedRoots.TryGetValue(directory.FullName, out var observed))
            {
                if (observed) return directory.FullName;
                continue;
            }
            var owned = false;
            if (File.Exists(Path.Join(directory.FullName, SidecarName)))
            {
                try
                {
                    using var root = CoverageRunOutputLease.AcquireArtifactDirectory(directory.FullName, false, false);
                    if (root is not null)
                    {
                        using var sidecar = root.OpenArtifactFile(SidecarName, createNew: false, requirePrivate: false);
                        owned = ReadCurrent(sidecar).ManifestBase.OutputIdentity == root.ArtifactDirectoryIdentity;
                    }
                }
                catch (Exception exception) when (IsOptionalFailure(exception)) { }
            }
            if (observedRoots is not null) observedRoots[directory.FullName] = owned;
            if (owned) return directory.FullName;
        }
        return null;
    }

    /// <summary>Reads one bounded current sidecar, requiring compatible explicit producer inventory.</summary>
    /// <param name="input">A caller-owned no-follow regular-file stream.</param>
    internal static CoverageArtifactCurrentRun ReadCurrent(Stream input)
    {
        var run = CoverageArtifactJson.Read<CoverageArtifactCurrentRun>(input,
            CoverageArtifactLimits.RunInventoryMaxBytes, JsonOptions)
            ?? throw new InvalidDataException("Current sidecar is empty.");
        if (run.ManifestBase is null || run.Inventory is null || run.OutputPath is null || run.RepositoryPath is null
            || run.ManifestBase.SchemaVersion != 1 || !Guid.TryParseExact(run.ManifestBase.RunId, "N", out _)
            || run.Inventory.Count > CoverageArtifactLimits.MaximumFiles)
            throw new InvalidDataException("Current sidecar is incompatible.");
        return run;
    }

    private static async Task WriteDiagnosticSafelyAsync(CoverageArtifactDiagnostic diagnostic, TextWriter writer)
    {
        try { await diagnostic.WriteAsync(writer).ConfigureAwait(false); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 };

    /// <summary>Classifies expected optional store, filesystem, and metadata failures without catching programming errors broadly.</summary>
    internal static bool IsOptionalFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or PlatformNotSupportedException or TimeoutException or NotSupportedException
        or InvalidOperationException or ArgumentException or CoverageExecutionException or EntryPointNotFoundException or DllNotFoundException;
}
