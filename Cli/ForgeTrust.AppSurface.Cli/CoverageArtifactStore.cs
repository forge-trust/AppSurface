using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes the explicitly owned current-run files eligible for a verified snapshot.</summary>
/// <param name="ManifestBase">Bounded run metadata used as the base of the completed archive manifest.</param>
/// <param name="Inventory">The producer-supplied inventory; the store never discovers files to adopt.</param>
/// <param name="OutputPath">The existing output directory containing current artifacts.</param>
/// <param name="RepositoryPath">The repository or worktree associated with the output.</param>
internal sealed record CoverageArtifactCurrentRun(
    CoverageArtifactRunManifest ManifestBase,
    IReadOnlyList<CoverageArtifactInventoryEntry> Inventory,
    string OutputPath,
    string RepositoryPath);

/// <summary>A bounded point-in-time view of the private coverage artifact domain.</summary>
internal sealed record CoverageArtifactStoreStatus(
    string RootPath,
    CoverageArtifactPolicy Policy,
    int RegisteredRuns,
    int CurrentRuns,
    int ArchivedRuns,
    long CurrentBytes,
    long ArchivedBytes,
    bool IsComplete,
    string? IncompleteReason)
{
    /// <summary>Gets archived bytes protected by explicit pins, separate from current output bytes.</summary>
    internal long PinnedBytes { get; init; }
    /// <summary>Gets bytes reserved for active or unreconciled staging; not a measurement of reclaimable bytes.</summary>
    internal long StagingReservedBytes { get; init; }
    /// <summary>Gets bounded metadata bytes read for this observation.</summary>
    internal long MetadataBytes { get; init; }
    /// <summary>Gets bounded expiry receipts retained for the last seven days.</summary>
    internal int ExpiryReceiptCount { get; init; }
    /// <summary>Gets recent overflow observations; repeated observations are never added to exact current totals.</summary>
    internal IReadOnlyList<CoverageArtifactOverflowObservation> OverflowObservations { get; init; } = [];
}

/// <summary>A recent capacity observation, never ownership authority or an exact domain total.</summary>
internal sealed record CoverageArtifactOverflowObservation(string RunId, long Bytes, DateTimeOffset ObservedAtUtc);

/// <summary>Coordinates private coverage artifact metadata and verified historical snapshots.</summary>
/// <remarks>
/// The store manages only producer-registered inventories and its own complete archives. Current
/// output remains at its existing path. Metadata is versioned and bounded; unknown or invalid state
/// fails closed and is never treated as permission to adopt or delete files.
/// </remarks>
internal sealed class CoverageArtifactStore : IDisposable
{
    private const string MarkerName = "store.json";
    private const string RegistryName = "registry.json";
    private const string PolicyName = "policy.json";
    private const string ArchivesName = "archives";
    private const string StagingName = "staging";
    private const string RegistryStageName = ".registry-write.json";
    private readonly TimeProvider _clock;
    private readonly bool _readOnly;
    private readonly bool _initialized;
    private bool _disposed;

    private CoverageArtifactStore(string rootPath, TimeProvider clock, CoverageArtifactPolicy policy, bool readOnly = false, bool initialized = true)
    {
        RootPath = rootPath;
        _clock = clock;
        Policy = policy;
        _readOnly = readOnly;
        _initialized = initialized;
    }

    /// <summary>Gets the canonical private domain root.</summary>
    internal string RootPath { get; }

    /// <summary>Gets the fully validated effective retention policy.</summary>
    internal CoverageArtifactPolicy Policy { get; }

    /// <summary>Opens or creates the current user's private artifact domain.</summary>
    /// <param name="overrideHome">Optional isolated domain root, primarily for explicit local configuration and tests.</param>
    /// <param name="clock">Optional clock for deterministic timestamps and maintenance budgets.</param>
    /// <exception cref="IOException">The root is broad, linked, unsupported, or contains unknown store state.</exception>
    /// <exception cref="InvalidDataException">The marker, registry, or policy has an unsupported or invalid schema.</exception>
    internal static CoverageArtifactStore Open(string? overrideHome = null, TimeProvider? clock = null)
    {
        var configuredOverride = overrideHome ?? Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        var root = string.IsNullOrWhiteSpace(configuredOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeTrust", "AppSurface", "artifacts", "v1")
            : Path.GetFullPath(configuredOverride);
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("The current user's local application-data directory is unavailable.");
        root = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(root));
        RejectBroadStoreRoot(root);

        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)
            ?? throw new IOException("Unable to open the private artifact domain.");
        var firstEntries = directory.EnumerateArtifactEntries(16);
        var alreadyInitialized = firstEntries.Any(entry => entry.Name == MarkerName)
            && firstEntries.Any(entry => entry.Name == RegistryName);
        using var initialization = CoverageArtifactOperationLease.Acquire(root, exclusive: !alreadyInitialized);
        initialization.VerifyBinding();
        var entries = directory.EnumerateArtifactEntries(16);
        var allowed = new HashSet<string>([MarkerName, RegistryName, PolicyName, ArchivesName, StagingName, ".appsurface-coverage-operation.lock"], StringComparer.Ordinal);
        if (entries.Any(entry => !allowed.Contains(entry.Name)))
            throw new IOException("The artifact domain contains unknown entries; preserve it for inspection.");

        var identity = directory.ArtifactDirectoryIdentity;
        var marker = ReadOptionalJson<StoreMarker>(directory, MarkerName, 4096);
        var initializing = marker is null;
        if (marker is null)
        {
            if (entries.Any(entry => entry.Name != ".appsurface-coverage-operation.lock"))
                throw new IOException("An unmarked non-empty artifact domain is not owned by AppSurface.");
            WriteNewJson(directory, MarkerName, new StoreMarker(1, identity));
        }
        else if (marker.SchemaVersion != 1 || !string.Equals(marker.PhysicalIdentity, identity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The artifact domain marker is invalid or belongs to a different directory identity.");
        }

        EnsurePrivateChild(directory, ArchivesName);
        EnsurePrivateChild(directory, StagingName);

        _ = CoverageArtifactCapability.Verify(Path.Join(root, StagingName));
        var policy = ReadOptionalPolicy(directory) ?? DefaultPolicy();
        if (ReadOptionalJson<StoreRegistry>(directory, RegistryName, CoverageArtifactLimits.RegistryMaxBytes) is null)
        {
            if (!initializing) throw new InvalidDataException("The owned store registry is missing; preserve the domain for reconciliation.");
            WriteNewJson(directory, RegistryName, StoreRegistry.Empty);
        }
        _ = ReadRegistry(directory);
        return new CoverageArtifactStore(root, clock ?? TimeProvider.System, policy);
    }

    /// <summary>Opens an existing private domain for status reads without creating or repairing any filesystem entry.</summary>
    /// <param name="overrideHome">Optional isolated domain root used by explicit configuration and tests.</param>
    /// <param name="clock">Optional clock retained for API symmetry; reads do not use it.</param>
    /// <returns>A read-only store; an absent domain is represented by an empty in-memory status view.</returns>
    internal static CoverageArtifactStore OpenReadOnly(string? overrideHome = null, TimeProvider? clock = null)
    {
        var configuredOverride = overrideHome ?? Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        var root = string.IsNullOrWhiteSpace(configuredOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeTrust", "AppSurface", "artifacts", "v1")
            : Path.GetFullPath(configuredOverride);
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("The current user's local application-data directory is unavailable.");
        root = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(root));
        RejectBroadStoreRoot(root);
        var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: false, requirePrivate: true);
        if (directory is null)
            return new CoverageArtifactStore(root, clock ?? TimeProvider.System, DefaultPolicy(), readOnly: true, initialized: false);
        using (directory)
        {
            var allowed = new HashSet<string>([MarkerName, RegistryName, PolicyName, ArchivesName, StagingName, ".appsurface-coverage-operation.lock"], StringComparer.Ordinal);
            if (directory.EnumerateArtifactEntries(16).Any(entry => !allowed.Contains(entry.Name)))
                throw new IOException("The artifact domain contains unknown entries; preserve it for inspection.");
            var identity = directory.ArtifactDirectoryIdentity;
            var marker = ReadOptionalJson<StoreMarker>(directory, MarkerName, 4096)
                ?? throw new InvalidDataException("The existing artifact domain has no valid ownership marker.");
            if (marker.SchemaVersion != 1 || !string.Equals(marker.PhysicalIdentity, identity, StringComparison.Ordinal))
                throw new InvalidDataException("The artifact domain marker is invalid or belongs to a different directory identity.");
            foreach (var name in new[] { ArchivesName, StagingName })
            {
                using var child = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root, name), false, true)
                    ?? throw new InvalidDataException("The marked artifact domain is missing a required private directory.");
            }
            var registry = ReadOptionalJson<StoreRegistry>(directory, RegistryName, CoverageArtifactLimits.RegistryMaxBytes)
                ?? throw new InvalidDataException("The existing artifact domain has no registry.");
            ValidateRegistry(registry);
            var policy = ReadOptionalPolicy(directory) ?? DefaultPolicy();
            return new CoverageArtifactStore(root, clock ?? TimeProvider.System, policy, readOnly: true);
        }
    }

    /// <summary>Registers one producer-owned current output and inventory.</summary>
    /// <param name="run">The explicit current run supplied by the producer.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal Task RegisterAsync(CoverageArtifactCurrentRun run, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateRun(run);
        using var operation = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true);
        operation.VerifyBinding();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
        var registry = ReadRegistry(root);
        var outputIdentity = GetDirectoryIdentity(run.OutputPath);
        var repositoryIdentity = GetDirectoryIdentity(run.RepositoryPath);
        var now = _clock.GetUtcNow();
        var prior = registry.Registrations.SingleOrDefault(item => item.RunId == run.ManifestBase.RunId);
        var item = new StoreRegistration(
            run.ManifestBase.RunId,
            outputIdentity,
            repositoryIdentity,
            Path.GetFullPath(run.OutputPath),
            Path.GetFullPath(run.RepositoryPath),
            run.ManifestBase.Outcome,
            run.ManifestBase.CreatedAtUtc,
            now,
            run.Inventory.Sum(file => file.Length),
            run.ManifestBase.Version,
            run.ManifestBase.Provenance,
            run.Inventory.ToArray(),
            Archived: prior?.Archived ?? false,
            Current: true,
            Pinned: prior?.Pinned ?? false,
            ArchiveBytes: prior?.ArchiveBytes ?? 0)
        {
            ArchivedAtUtc = prior?.ArchivedAtUtc
        };
        var registrations = registry.Registrations
            .Where(existing => existing.Current || existing.Pinned || existing.Archived
                || now - existing.ObservedAtUtc <= TimeSpan.FromDays(CoverageArtifactLimits.ExpiryReceiptMaxAgeDays))
            .Where(existing => existing.RunId != item.RunId)
            .Select(existing => existing.OutputIdentity == outputIdentity ? existing with { Current = false } : existing)
            .Append(item).ToArray();
        var overflow = registry.OverflowObservations;
        var complete = registry.IsComplete;
        var reason = registry.IncompleteReason;
        var proposedRegistry = registry with { Registrations = registrations };
        if (registrations.Length > CoverageArtifactLimits.MaximumRegistrations
            || JsonSerializer.SerializeToUtf8Bytes(proposedRegistry, JsonOptions).Length
                > CoverageArtifactLimits.RegistryMaxBytes - CoverageArtifactLimits.OverflowObservationReserveBytes)
        {
            registrations = registry.Registrations
                .Select(existing => existing.OutputIdentity == outputIdentity ? existing with { Current = false } : existing)
                .ToArray();
            overflow = overflow.Append(new OverflowObservation(item.RunId, outputIdentity, item.CurrentBytes, now))
                .TakeLast(CoverageArtifactLimits.MaximumOverflowObservations).ToArray();
            complete = false;
            reason = "registration limit reached; retained overflow observations are lower bounds";
        }
        PersistRegistry(root, registry with
        {
            Registrations = registrations,
            OverflowObservations = overflow,
            IsComplete = complete,
            IncompleteReason = reason
        });
        if (registrations.All(existing => existing.RunId != item.RunId))
            throw new InvalidDataException("The artifact registry registration limit was reached; status is a lower bound.");
        return Task.CompletedTask;
    }

    /// <summary>Creates a complete, verified archive of the supplied registered current run.</summary>
    /// <param name="run">The current output and explicit inventory to snapshot.</param>
    /// <param name="pin">Whether to protect the resulting archive from automatic expiry.</param>
    /// <param name="automatic">Whether automatic-work byte and time caps apply.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="sourceLeaseHeld">Whether the caller already holds exclusive source-output authority.</param>
    /// <param name="routineBudget">Optional invocation-wide cooperative budget shared with maintenance.</param>
    /// <returns>The complete manifest published in the archive.</returns>
    internal async Task<CoverageArtifactRunManifest> SnapshotAsync(
        CoverageArtifactCurrentRun run,
        bool pin,
        bool automatic,
        CancellationToken cancellationToken,
        bool sourceLeaseHeld = false,
        CoverageArtifactWorkBudget? routineBudget = null)
    {
        ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateRun(run);
        var work = routineBudget ?? new CoverageArtifactWorkBudget(_clock, automatic
            ? TimeSpan.FromMilliseconds(CoverageArtifactLimits.RoutineMaintenanceBudgetMilliseconds) : null);
        work.Check(cancellationToken);
        var total = run.Inventory.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        var cap = automatic ? CoverageArtifactLimits.AutomaticSnapshotMaxOriginalBytes : CoverageArtifactLimits.ExplicitExpandedMaxBytes;
        if (total > cap) throw new InvalidDataException("The run inventory exceeds the snapshot byte limit.");
        if (automatic && Policy.HistoryMaxBytes == 0)
            throw new InvalidDataException("Automatic history publication is disabled by the user policy.");
        using var sourceLease = sourceLeaseHeld ? null : CoverageArtifactOperationLease.Acquire(run.OutputPath, exclusive: false);
        sourceLease?.VerifyBinding();
        StoreRegistration registration;
        string reservationId;
        using (var operation = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true))
        {
            operation.VerifyBinding();
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
            var registry = ReadRegistry(root);
            registration = registry.Registrations.SingleOrDefault(item => item.RunId == run.ManifestBase.RunId)
                ?? throw new InvalidDataException("The current run must be registered before snapshot publication.");
            if (!string.Equals(registration.OutputIdentity, GetDirectoryIdentity(run.OutputPath), StringComparison.Ordinal)
                || !string.Equals(registration.RepositoryIdentity, GetDirectoryIdentity(run.RepositoryPath), StringComparison.Ordinal))
                throw new IOException("The registered output or repository identity changed.");
            if (registration.Archived)
            {
                // Release metadata before waiting for the candidate or hashing payloads.
                root.Dispose();
                operation.Dispose();
                var existingPath = Path.Join(RootPath, ArchivesName, run.ManifestBase.RunId);
                using var candidate = CoverageArtifactOperationLease.Acquire(existingPath, exclusive: pin);
                candidate.VerifyBinding();
                using var existing = CoverageRunOutputLease.AcquireArtifactDirectory(existingPath, false, true)
                    ?? throw new IOException("The archive registration has no verified archive directory.");
                var existingManifest = ReadManifest(existing);
                if (!existingManifest.Complete || existingManifest.RunId != run.ManifestBase.RunId)
                    throw new InvalidDataException("An existing archive is incomplete or mismatched.");
                await CoverageArtifactArchive.VerifyPayloadsAsync(existingPath, existingManifest, cancellationToken, work).ConfigureAwait(false);
                candidate.VerifyBinding();
                if (pin)
                {
                    using var metadata = CoverageArtifactOperationLease.Acquire(RootPath, true);
                    metadata.VerifyBinding();
                    using var latestRoot = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                    var latest = ReadRegistry(latestRoot);
                    if (!latest.Registrations.Any(item => item.RunId == run.ManifestBase.RunId && item.Archived))
                        throw new InvalidDataException("The verified archive is no longer registered.");
                    PersistRegistry(latestRoot, latest with { Registrations = UpdatePin(latest.Registrations, run.ManifestBase.RunId, true) });
                }
                return existingManifest;
            }
            reservationId = Guid.NewGuid().ToString("N");
            var reserveBytes = automatic ? CoverageArtifactLimits.AutomaticSnapshotMaxStagingBytes : CoverageArtifactLimits.DomainStagingMaxBytes;
            if (registry.Reservations.Sum(reservation => reservation.Bytes) + reserveBytes > CoverageArtifactLimits.DomainStagingMaxBytes)
                throw new IOException("The private artifact staging reservation limit is exhausted.");
            PersistRegistry(root, registry with
            {
                Reservations = registry.Reservations.Append(new StoreReservation(
                    reservationId, run.ManifestBase.RunId, reserveBytes, _clock.GetUtcNow(), pin)).ToArray()
            });
        }
        var stageName = "run-" + reservationId;
        var published = false;
        try
        {
            var archivePath = Path.Join(RootPath, ArchivesName, run.ManifestBase.RunId);
            if (Directory.Exists(archivePath) || File.Exists(archivePath))
                throw new IOException("An unregistered archive occupies the requested run identifier; preserve it for inspection.");

            var stagingPath = Path.Join(RootPath, StagingName);
            using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(stagingPath, false, true)!;
            staging.CreateArtifactDirectory(stageName).Dispose();
            using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(staging.ArtifactDirectoryPath, stageName), false, true, allowWindowsRenameSharing: true)!;
            using var stageOperation = CoverageArtifactOperationLease.Acquire(stage.ArtifactDirectoryPath, exclusive: true);
            var budget = work.Remaining;
            var manifestBase = run.ManifestBase with
            {
                OutputIdentity = registration.OutputIdentity,
                RepositoryIdentity = registration.RepositoryIdentity,
                Provenance = run.ManifestBase.Provenance,
                Version = string.IsNullOrWhiteSpace(run.ManifestBase.Version) ? CoverageArtifactInventory.ProducingVersion : run.ManifestBase.Version,
                Complete = false,
            };
            var complete = await CoverageArtifactArchive.StageAsync(
                run.OutputPath, stage.ArtifactDirectoryPath, manifestBase, run.Inventory, cap,
                automatic ? CoverageArtifactLimits.AutomaticSnapshotMaxStagingBytes : CoverageArtifactLimits.DomainStagingMaxBytes,
                _clock, budget, cancellationToken).ConfigureAwait(false);
            using (var manifestFile = stage.OpenArtifactFile("manifest.json", createNew: true))
                CoverageArtifactArchive.WriteManifest(manifestFile, complete with { Complete = true });
            cancellationToken.ThrowIfCancellationRequested();
            var completedManifest = complete with { Complete = true };
            using var archives = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(RootPath, ArchivesName), false, true)!;
            var identity = stage.ArtifactDirectoryIdentity;
            stageOperation.Dispose();
            stage.Dispose();
            work.Check(cancellationToken);
            staging.MoveArtifactEntry(stageName, archives, run.ManifestBase.RunId, expectedSourceIdentity: identity);
            published = true;
            var archiveBytes = DirectoryBytes(Path.Join(RootPath, ArchivesName, run.ManifestBase.RunId));
            using (var operation = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true))
            {
                operation.VerifyBinding();
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                var registry = ReadRegistry(root);
                var updated = registry.Registrations.Select(item => item.RunId == run.ManifestBase.RunId
                    ? item with { Archived = true, Pinned = pin, ArchiveBytes = archiveBytes, ArchivedAtUtc = completedManifest.ArchivedAtUtc, ObservedAtUtc = _clock.GetUtcNow() }
                    : item).ToArray();
                PersistRegistry(root, registry with
                {
                    Registrations = updated,
                    Reservations = registry.Reservations.Where(reservation => reservation.Id != reservationId).ToArray()
                });
            }
            return completedManifest;
        }
        finally
        {
            // Only this invocation's retained, exclusively leased stage may be reclaimed here.
            // A published but unregistered archive remains preserved with its reservation.
            if (!published) ReleaseFailedSnapshot(reservationId, stageName);
        }
    }

    private void ReleaseFailedSnapshot(string reservationId, string stageName)
    {
        try
        {
            using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(RootPath, StagingName), false, true)!;
            var candidate = staging.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumOverflowObservations + 2)
                .SingleOrDefault(entry => entry.Name == stageName);
            if (candidate is not null)
            {
                using var stageLease = CoverageArtifactOperationLease.Acquire(Path.Join(staging.ArtifactDirectoryPath, stageName), true);
                stageLease.VerifyBinding();
                staging.DeleteArtifactEntry(stageName, CoverageArtifactLimits.MaximumFiles + 3, candidate.Identity);
            }
            using var operation = CoverageArtifactOperationLease.Acquire(RootPath, true);
            operation.VerifyBinding();
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
            var registry = ReadRegistry(root);
            PersistRegistry(root, registry with { Reservations = registry.Reservations.Where(item => item.Id != reservationId).ToArray() });
        }
        catch (Exception exception) when (IsOptionalFailure(exception))
        {
            // Unknown/active remnants and their accounting are preserved. Recovery never masks the primary error.
        }
    }

    /// <summary>Returns the registered current run for the same physical output directory, if present.</summary>
    /// <param name="outputPath">The output directory whose recorded physical identity is checked.</param>
    internal CoverageArtifactCurrentRun? GetCurrentRun(string outputPath)
    {
        ThrowIfDisposed();
        if (!_initialized) return null;
        using var operation = _readOnly ? null : CoverageArtifactOperationLease.Acquire(RootPath, exclusive: false);
        operation?.VerifyBinding();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
        var identity = GetDirectoryIdentity(outputPath);
        var latest = ReadRegistry(root).Registrations
            .Where(item => item.OutputIdentity == identity && item.Current)
            .OrderByDescending(item => item.ObservedAtUtc)
            .FirstOrDefault();
        if (latest is null) return null;
        return ToCurrentRun(latest);
    }

    /// <summary>Checks whether a registered current output already has a complete, integrity-verified archive.</summary>
    /// <param name="outputPath">Existing output whose current registration is inspected without adoption.</param>
    /// <param name="token">Caller cancellation while verifying payloads.</param>
    internal async Task<bool> HasVerifiedArchiveAsync(string outputPath, CancellationToken token)
    {
        var current = GetCurrentRun(outputPath);
        if (current is null) return false;
        var path = Path.Join(RootPath, ArchivesName, current.ManifestBase.RunId);
        if (!Directory.Exists(path)) return false;
        using var candidate = CoverageArtifactOperationLease.Acquire(path, false);
        candidate.VerifyBinding();
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)!;
        var manifest = ReadManifest(archive);
        if (manifest.RunId != current.ManifestBase.RunId) return false;
        await CoverageArtifactArchive.VerifyPayloadsAsync(path, manifest, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>Reads a bounded status snapshot without changing current outputs or archives.</summary>
    internal CoverageArtifactStoreStatus GetStatus()
    {
        ThrowIfDisposed();
        if (!_initialized)
            return new(RootPath, Policy, 0, 0, 0, 0, 0, true, null);
        using var operation = _readOnly ? null : CoverageArtifactOperationLease.Acquire(RootPath, exclusive: false);
        operation?.VerifyBinding();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
        var registry = ReadRegistry(root);
        return new(RootPath, Policy, registry.Registrations.Length,
            registry.Registrations.Count(item => item.Current),
            registry.Registrations.Count(item => item.Archived),
            registry.Registrations.Where(item => item.Current).Sum(item => item.CurrentBytes),
            registry.Registrations.Where(item => item.Archived).Sum(item => item.ArchiveBytes),
            registry.IsComplete, registry.IncompleteReason)
        {
            ExpiryReceiptCount = registry.ExpiryReceipts.Length,
            PinnedBytes = registry.Registrations.Where(item => item.Archived && item.Pinned).Sum(item => item.ArchiveBytes),
            StagingReservedBytes = registry.Reservations.Sum(item => item.Bytes),
            MetadataBytes = MetadataBytes(root),
            OverflowObservations = registry.OverflowObservations.Select(item => new CoverageArtifactOverflowObservation(
                item.RunId, item.CurrentBytes, item.ObservedAtUtc)).ToArray()
        };
    }

    /// <summary>Applies or previews eligible archive expiry under the shared retention policy.</summary>
    /// <param name="routineBudget">Optional invocation-wide maintenance budget; explicit maintenance has no deadline.</param>
    /// <param name="apply">False returns candidates without deleting them.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="heldSourceLease">Optional live exclusive source lease owned by the calling session; reused only for its matching physical output, never disposed by maintenance.</param>
    /// <param name="onArchiveDeleted">Receives each run identifier immediately after physical deletion, even if later metadata persistence fails. The callback must not throw.</param>
    internal async Task<IReadOnlyList<string>> MaintainAsync(bool apply, CancellationToken cancellationToken, CoverageArtifactWorkBudget? routineBudget = null,
        Action<string>? onArchiveDeleted = null, CoverageArtifactOperationLease? heldSourceLease = null)
    {
        if (heldSourceLease is not null)
        {
            heldSourceLease.VerifyBinding();
            if (!heldSourceLease.IsExclusive) throw new ArgumentException("Recovery requires an exclusive source lease.", nameof(heldSourceLease));
        }
        if (apply) ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!_initialized) return [];
        if (apply)
        {
            using (var metadata = CoverageArtifactOperationLease.Acquire(RootPath, true))
            {
                metadata.VerifyBinding();
                routineBudget?.Check(cancellationToken);
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                RemoveAbandonedRegistryStage(root);
            }
            await RecoverAbandonedSnapshotsAsync(cancellationToken, routineBudget, heldSourceLease).ConfigureAwait(false);
        }
        StoreRegistration[] registrations;
        using (var readLease = _readOnly ? null : CoverageArtifactOperationLease.Acquire(RootPath, exclusive: false))
        {
            readLease?.VerifyBinding();
            using var readRoot = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
            registrations = ReadRegistry(readRoot).Registrations;
        }
        var eligible = registrations.Where(item => item.Archived && !item.Current && !item.Pinned)
            .OrderBy(item => string.Equals(item.Outcome, "success", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.ArchivedAtUtc ?? item.ObservedAtUtc).ToList();
        // Current snapshots and explicit pins are protected categories, not pressure on the
        // eligible-history target. Registry metadata still consumes the shared history allowance.
        long metadataBytes;
        using (var metadataRoot = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!)
            metadataBytes = MetadataBytes(metadataRoot);
        var total = checked(eligible.Sum(item => item.ArchiveBytes) + metadataBytes);
        var remove = new List<StoreRegistration>();
        foreach (var candidate in eligible)
        {
            routineBudget?.Check(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var expired = _clock.GetUtcNow() - (candidate.ArchivedAtUtc ?? candidate.ObservedAtUtc) > TimeSpan.FromDays(Policy.HistoryMaxAgeDays);
            if (!expired && total <= Policy.HistoryMaxBytes) continue;
            remove.Add(candidate);
            total -= candidate.ArchiveBytes;
        }
        var removed = new List<string>();
        if (apply)
        {
            foreach (var candidate in remove)
            {
                routineBudget?.Check(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var archivePath = Path.Join(RootPath, ArchivesName, candidate.RunId);
                using var archiveLease = CoverageArtifactOperationLease.Acquire(archivePath, exclusive: true);
                archiveLease.VerifyBinding();
                using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, false, true, allowWindowsRenameSharing: true)
                    ?? throw new IOException("The archive candidate disappeared before revalidation.");
                using (var metadataLease = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true))
                {
                    metadataLease.VerifyBinding();
                    using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                    var current = ReadRegistry(root).Registrations.SingleOrDefault(item => item.RunId == candidate.RunId);
                    if (current is null || !current.Archived || current.Current || current.Pinned) continue;
                }
                var manifest = ReadManifest(archive);
                if (!manifest.Complete || manifest.RunId != candidate.RunId) continue;
                var expectedNames = manifest.Files.Select(file => file.PayloadId + ".gz")
                    .Append("manifest.json").Append(".appsurface-coverage-operation.lock").ToHashSet(StringComparer.Ordinal);
                if (archive.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumFiles + 3)
                    .Any(entry => entry.IsDirectory || !expectedNames.Contains(entry.Name)))
                    throw new InvalidDataException("An archive contains unknown entries; preserve it for inspection.");
                // Keep an expiry receipt in bounded registration metadata. The directory helper owns deletion authority.
                using var archives = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(RootPath, ArchivesName), false, true)!;
                var archiveIdentity = archive.ArtifactDirectoryIdentity;
                archive.Dispose();
                archives.DeleteArtifactEntry(candidate.RunId, CoverageArtifactLimits.MaximumFiles + 3, archiveIdentity);
                onArchiveDeleted?.Invoke(candidate.RunId);
                using (var metadataLease = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true))
                {
                    metadataLease.VerifyBinding();
                    using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                    var latest = ReadRegistry(root);
                    var updated = latest.Registrations.Select(item => item.RunId == candidate.RunId
                        ? item with { Archived = false, ArchiveBytes = 0, ObservedAtUtc = _clock.GetUtcNow() }
                        : item).ToArray();
                    var receipts = latest.ExpiryReceipts.Append(new ExpiryReceipt(candidate.RunId, candidate.Outcome,
                        candidate.ArchiveBytes, _clock.GetUtcNow(), "age-or-byte-policy"))
                        .Where(item => _clock.GetUtcNow() - item.ExpiredAtUtc <= TimeSpan.FromDays(CoverageArtifactLimits.ExpiryReceiptMaxAgeDays))
                        .OrderBy(item => item.ExpiredAtUtc).TakeLast(CoverageArtifactLimits.ExpiryReceiptsMaxEntries).ToArray();
                    while (JsonSerializer.SerializeToUtf8Bytes(receipts, JsonOptions).LongLength > CoverageArtifactLimits.ExpiryReceiptsMaxBytes)
                        receipts = receipts.Skip(1).ToArray();
                    PersistRegistry(root, latest with { Registrations = updated, ExpiryReceipts = receipts });
                }
                removed.Add(candidate.RunId);
            }
        }
        return apply ? removed : remove.Select(item => item.RunId).ToArray();
    }

    private async Task RecoverAbandonedSnapshotsAsync(CancellationToken token, CoverageArtifactWorkBudget? budget,
        CoverageArtifactOperationLease? heldSourceLease)
    {
        StoreReservation[] reservations;
        StoreRegistration[] registrations;
        using (var metadata = CoverageArtifactOperationLease.Acquire(RootPath, false))
        {
            metadata.VerifyBinding();
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
            var state = ReadRegistry(root);
            reservations = state.Reservations;
            registrations = state.Registrations;
        }
        foreach (var reservation in reservations.OrderBy(item => item.RunId, StringComparer.Ordinal))
        {
            budget?.Check(token);
            token.ThrowIfCancellationRequested();
            var stageName = "run-" + reservation.Id;
            var stagePath = Path.Join(RootPath, StagingName, stageName);
            var archivePath = Path.Join(RootPath, ArchivesName, reservation.RunId);
            var registration = registrations.SingleOrDefault(item => item.RunId == reservation.RunId);
            if (registration is null) continue;

            if (Directory.Exists(archivePath) || File.Exists(archivePath))
            {
                await RecoverPublishedArchiveAsync(reservation, registration, archivePath, token, budget, heldSourceLease).ConfigureAwait(false);
                continue;
            }

            CoverageArtifactOperationLease? acquiredSource;
            try { acquiredSource = AcquireRecoverySource(registration, heldSourceLease); }
            catch (Exception exception) when (IsOptionalFailure(exception)) { continue; }
            using var ownedSource = acquiredSource;
            var sourceLease = acquiredSource ?? heldSourceLease!;
            sourceLease.VerifyBinding();
            if (sourceLease.PhysicalIdentity != registration.OutputIdentity) continue;
            if (GetDirectoryIdentity(registration.RepositoryPath) != registration.RepositoryIdentity) continue;
            if (!Directory.Exists(stagePath))
            {
                // The exclusive source lease excludes a live snapshot before stage creation.
                // Recheck all facts under metadata authority before releasing its reservation.
                using var metadata = CoverageArtifactOperationLease.Acquire(RootPath, true);
                metadata.VerifyBinding();
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                var latest = ReadRegistry(root);
                var current = latest.Registrations.SingleOrDefault(item => item.RunId == reservation.RunId);
                if (current is null || current.OutputIdentity != sourceLease.PhysicalIdentity
                    || current.RepositoryIdentity != registration.RepositoryIdentity
                    || !latest.Reservations.Any(item => item == reservation)
                    || Directory.Exists(stagePath) || File.Exists(stagePath)
                    || Directory.Exists(archivePath) || File.Exists(archivePath)) continue;
                budget?.Check(token);
                token.ThrowIfCancellationRequested();
                PersistRegistry(root, latest with { Reservations = latest.Reservations.Where(item => item != reservation).ToArray() });
                continue;
            }
            CoverageArtifactOperationLease candidate;
            try { candidate = CoverageArtifactOperationLease.Acquire(stagePath, true); }
            catch (CoverageExecutionException) { continue; }
            using (candidate)
            using (var stage = CoverageRunOutputLease.AcquireArtifactDirectory(stagePath, false, true)!)
            {
                candidate.VerifyBinding();
                var entries = stage.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumFiles + 3);
                if (entries.Any(entry => entry.IsDirectory || entry.Name != "manifest.json"
                    && entry.Name != ".appsurface-coverage-operation.lock"
                    && !(entry.Name.Length == 11 && entry.Name.EndsWith(".gz", StringComparison.Ordinal)
                        && entry.Name[..8].All(char.IsAsciiDigit)))) continue;
                var identity = stage.ArtifactDirectoryIdentity;
                stage.Dispose();
                using var metadata = CoverageArtifactOperationLease.Acquire(RootPath, true);
                metadata.VerifyBinding();
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                var registry = ReadRegistry(root);
                var matchingReservations = registry.Reservations
                    .Where(item => item.Id == reservation.Id && item.RunId == reservation.RunId).ToArray();
                if (matchingReservations.Length != 1 || matchingReservations[0] != reservation) continue;
                budget?.Check(token);
                using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(RootPath, StagingName), false, true)!;
                staging.DeleteArtifactEntry(stageName, CoverageArtifactLimits.MaximumFiles + 3, identity);
                PersistRegistry(root, registry with
                {
                    Reservations = registry.Reservations
                        .Where(item => item.Id != reservation.Id || item.RunId != reservation.RunId).ToArray()
                });
            }
        }
    }

    private async Task RecoverPublishedArchiveAsync(
        StoreReservation reservation,
        StoreRegistration registration,
        string archivePath,
        CancellationToken token,
        CoverageArtifactWorkBudget? budget,
        CoverageArtifactOperationLease? heldSourceLease)
    {
        CoverageArtifactOperationLease? acquiredSource;
        try { acquiredSource = AcquireRecoverySource(registration, heldSourceLease); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { return; }
        using (acquiredSource)
        {
            var sourceLease = acquiredSource ?? heldSourceLease!;
            sourceLease.VerifyBinding();
            if (sourceLease.PhysicalIdentity != registration.OutputIdentity) return;
            try
            {
                if (GetDirectoryIdentity(registration.RepositoryPath) != registration.RepositoryIdentity) return;
            }
            catch (Exception exception) when (IsOptionalFailure(exception)) { return; }

            CoverageArtifactOperationLease archiveLease;
            try { archiveLease = CoverageArtifactOperationLease.Acquire(archivePath, exclusive: true); }
            catch (Exception exception) when (IsOptionalFailure(exception)) { return; }
            using (archiveLease)
            using (var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, false, true))
            {
                if (archive is null || archive.ArtifactDirectoryIdentity != archiveLease.PhysicalIdentity) return;
                try
                {
                    archiveLease.VerifyBinding();
                    var manifest = ReadManifest(archive);
                    if (manifest.RunId != reservation.RunId || !RegistrationMatchesManifest(registration, manifest)
                        || !ArchiveEntriesMatchManifest(archive, manifest)) return;

                    // Payload verification runs while only the source and archive candidate are leased.
                    // The store-wide metadata lock is acquired only after all archive bytes are verified.
                    await CoverageArtifactArchive.VerifyPayloadsAsync(archivePath, manifest, token, budget).ConfigureAwait(false);
                    archiveLease.VerifyBinding();
                    archive.VerifyArtifactBinding();
                    budget?.Check(token);
                    token.ThrowIfCancellationRequested();
                    var archiveBytes = DirectoryBytes(archivePath);

                    using var metadata = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true);
                    metadata.VerifyBinding();
                    using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                    var registry = ReadRegistry(root);
                    var matchingReservations = registry.Reservations
                        .Where(item => item.Id == reservation.Id && item.RunId == reservation.RunId).ToArray();
                    if (matchingReservations.Length != 1 || matchingReservations[0] != reservation) return;
                    var current = registry.Registrations.SingleOrDefault(item => item.RunId == reservation.RunId);
                    if (current is null || current.Archived || !RegistrationMatchesManifest(current, manifest)
                        || current.OutputIdentity != sourceLease.PhysicalIdentity)
                        return;

                    var repositoryIdentity = GetDirectoryIdentity(current.RepositoryPath);
                    if (repositoryIdentity != current.RepositoryIdentity) return;
                    var updated = registry.Registrations.Select(item => item.RunId == reservation.RunId
                        ? item with
                        {
                            Archived = true,
                            Pinned = reservation.Pin ?? true,
                            ArchiveBytes = archiveBytes,
                            ArchivedAtUtc = manifest.ArchivedAtUtc,
                            ObservedAtUtc = _clock.GetUtcNow()
                        }
                        : item).ToArray();
                    PersistRegistry(root, registry with
                    {
                        Registrations = updated,
                        Reservations = registry.Reservations
                            .Where(item => item.Id != reservation.Id || item.RunId != reservation.RunId).ToArray()
                    });
                }
                catch (TimeoutException) when (budget is not null)
                {
                    throw;
                }
                catch (Exception exception) when (IsOptionalFailure(exception))
                {
                    // Corrupt, unknown, changed, or busy archives keep their reservation and bytes for inspection.
                }
            }
        }
    }

    private static CoverageArtifactOperationLease? AcquireRecoverySource(StoreRegistration registration,
        CoverageArtifactOperationLease? heldSourceLease)
    {
        if (heldSourceLease is not null && heldSourceLease.PhysicalIdentity == registration.OutputIdentity)
        {
            // MaintainAsync validates the immutable exclusive mode before recovery starts.
            // Recheck the live binding here, immediately before borrowing the authority.
            heldSourceLease.VerifyBinding();
            return null;
        }
        return CoverageArtifactOperationLease.Acquire(registration.OutputPath, exclusive: true);
    }

    private static bool RegistrationMatchesManifest(StoreRegistration registration, CoverageArtifactRunManifest manifest)
    {
        if (registration.RunId != manifest.RunId || registration.OutputIdentity != manifest.OutputIdentity
            || registration.RepositoryIdentity != manifest.RepositoryIdentity || registration.Outcome != manifest.Outcome
            || registration.CreatedAtUtc != manifest.CreatedAtUtc || registration.Version != manifest.Version
            || registration.Provenance != manifest.Provenance || registration.Inventory.Length != manifest.Files.Count)
            return false;
        for (var index = 0; index < registration.Inventory.Length; index++)
        {
            var original = registration.Inventory[index];
            var archived = manifest.Files[index];
            if (original.Path != archived.Path || original.Length != archived.OriginalLength
                || !original.Sha256.Equals(archived.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool ArchiveEntriesMatchManifest(CoverageRunOutputLease archive, CoverageArtifactRunManifest manifest)
    {
        var expectedNames = manifest.Files.Select(file => file.PayloadId + ".gz")
            .Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        var entries = archive.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumFiles + 3);
        return entries.Count(entry => entry.Name != ".appsurface-coverage-operation.lock") == expectedNames.Count
            && entries.All(entry => !entry.IsDirectory
                && (expectedNames.Contains(entry.Name) || entry.Name == ".appsurface-coverage-operation.lock"));
    }

    /// <summary>Changes pin state for a verified archived run.</summary>
    /// <param name="runId">The opaque N-format run identifier.</param>
    /// <param name="pin">True to pin; false to permit normal expiry.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal async Task PinAsync(string runId, bool pin, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        ValidateRunId(runId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (pin)
        {
            CoverageArtifactCurrentRun? current;
            using (var readLease = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: false))
            {
                readLease.VerifyBinding();
                using var readRoot = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
                var registration = ReadRegistry(readRoot).Registrations.SingleOrDefault(candidate => candidate.RunId == runId)
                    ?? throw new FileNotFoundException("The registered run does not exist.");
                current = registration.Current && !registration.Archived
                    ? ToCurrentRun(registration)
                    : null;
            }
            if (current is not null)
            {
                await PinCurrentAsync(current, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var archivePath = Path.Join(RootPath, ArchivesName, runId);
        using var archiveLease = CoverageArtifactOperationLease.Acquire(archivePath, exclusive: true);
        archiveLease.VerifyBinding();
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, false, true)
            ?? throw new FileNotFoundException("The verified archive does not exist.");
        var manifest = ReadManifest(archive);
        if (!manifest.Complete || manifest.RunId != runId) throw new InvalidDataException("The archive cannot be pinned because its manifest is incomplete.");
        await CoverageArtifactArchive.VerifyPayloadsAsync(archivePath, manifest, cancellationToken).ConfigureAwait(false);
        archiveLease.VerifyBinding();
        using var writeLease = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: true);
        writeLease.VerifyBinding();
        using var writeRoot = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
        var registry = ReadRegistry(writeRoot);
        var item = registry.Registrations.SingleOrDefault(candidate => candidate.RunId == runId)
            ?? throw new FileNotFoundException("The registered run does not exist.");
        if (!item.Archived) throw new InvalidOperationException("A current run must be snapshotted successfully before it can be pinned.");
        PersistRegistry(writeRoot, registry with { Registrations = UpdatePin(registry.Registrations, runId, pin) });
        return;
    }

    private async Task PinCurrentAsync(CoverageArtifactCurrentRun current, CancellationToken cancellationToken)
    {
        await SnapshotAsync(current, pin: true, automatic: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores a verified archive into a fresh private output directory.</summary>
    /// <param name="runId">The opaque N-format run identifier.</param>
    /// <param name="freshOutputPath">A new destination path that must not already exist.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal async Task RestoreAsync(string runId, string freshOutputPath, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        ValidateRunId(runId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (Directory.Exists(freshOutputPath) || File.Exists(freshOutputPath)) throw new IOException("Restore destination must not already exist.");
        var parentPath = Path.GetDirectoryName(Path.GetFullPath(freshOutputPath))!;
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(parentPath, false, false)
            ?? throw new IOException("Restore requires an existing safe parent directory.");
        var archivePath = Path.Join(RootPath, ArchivesName, runId);
        using var archiveLease = CoverageArtifactOperationLease.Acquire(archivePath, exclusive: false);
        archiveLease.VerifyBinding();
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archivePath, false, true)
            ?? throw new FileNotFoundException("The verified archive does not exist.");
        using (var operation = CoverageArtifactOperationLease.Acquire(RootPath, exclusive: false))
        {
            operation.VerifyBinding();
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(RootPath, false, true)!;
            if (!ReadRegistry(root).Registrations.Any(item => item.RunId == runId && item.Archived))
                throw new InvalidDataException("The archive is not registered as complete.");
        }
        var manifest = ReadManifest(archive);
        if (!manifest.Complete || manifest.RunId != runId) throw new InvalidDataException("The archive is incomplete or mismatched.");
        var stageName = "restore-" + Guid.NewGuid().ToString("N");
        using var stage = parent.CreateArtifactDirectory(stageName);
        var stageIdentity = stage.ArtifactDirectoryIdentity;
        var published = false;
        try
        {
            await CoverageArtifactArchive.RestorePayloadsAsync(archive.ArtifactDirectoryPath, stage.ArtifactDirectoryPath,
                manifest, CoverageArtifactLimits.ExplicitExpandedMaxBytes, _clock, null, cancellationToken).ConfigureAwait(false);
            using (var receipt = stage.OpenArtifactFile("coverage-history.json", createNew: true))
                JsonSerializer.Serialize(receipt, new
                {
                    schemaVersion = 1,
                    runId,
                    historical = true,
                    restoredAtUtc = _clock.GetUtcNow(),
                    manifest.Provenance,
                    manifest.CreatedAtUtc,
                    manifest.Version,
                    manifest.Outcome,
                    manifest.Files
                });
            cancellationToken.ThrowIfCancellationRequested();
            stage.Dispose();
            archiveLease.VerifyBinding();
            parent.MoveArtifactEntry(stageName, parent, Path.GetFileName(freshOutputPath), expectedSourceIdentity: stageIdentity);
            published = true;
        }
        finally
        {
            stage.Dispose();
            if (!published)
            {
                try { parent.DeleteArtifactEntry(stageName, CoverageArtifactLimits.MaximumFiles + 64, stageIdentity); }
                catch (Exception exception) when (IsOptionalFailure(exception))
                {
                    // Preserve a changed or unknown stage rather than following it during failure recovery.
                }
            }
        }
    }

    /// <summary>Releases store resources.</summary>
    public void Dispose() => _disposed = true;

    private static bool IsOptionalFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or InvalidDataException or JsonException or PlatformNotSupportedException or TimeoutException or NotSupportedException
        or CoverageExecutionException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void ThrowIfReadOnly()
    {
        ThrowIfDisposed();
        if (_readOnly) throw new InvalidOperationException("This artifact store was opened read-only.");
    }

    private sealed record StoreMarker(int SchemaVersion, string PhysicalIdentity);
    private sealed record ExpiryReceipt(string RunId, string Outcome, long StoredBytes, DateTimeOffset ExpiredAtUtc, string Reason);
    private sealed record StoreRegistry(int SchemaVersion, StoreRegistration[] Registrations, StoreReservation[] Reservations,
        OverflowObservation[] OverflowObservations, bool IsComplete = true, string? IncompleteReason = null)
    {
        public ExpiryReceipt[] ExpiryReceipts { get; init; } = [];
        internal static StoreRegistry Empty { get; } = new(1, [], [], []);
    }
    private sealed record StoreReservation(string Id, string RunId, long Bytes, DateTimeOffset StartedAtUtc, bool? Pin = null);
    private sealed record OverflowObservation(string RunId, string OutputIdentity, long CurrentBytes, DateTimeOffset ObservedAtUtc);
    private sealed record StoreRegistration(
        string RunId, string OutputIdentity, string RepositoryIdentity, string OutputPath, string RepositoryPath,
        string Outcome, DateTimeOffset CreatedAtUtc, DateTimeOffset ObservedAtUtc, long CurrentBytes,
        string Version, CoverageArtifactProvenance? Provenance, CoverageArtifactInventoryEntry[] Inventory,
        bool Archived, bool Current, bool Pinned, long ArchiveBytes)
    {
        /// <summary>Gets the verified archive publication time, independent of later current-output observations.</summary>
        public DateTimeOffset? ArchivedAtUtc { get; init; }
    }

    private static void RejectBroadStoreRoot(string root)
    {
        var filesystemRoot = Path.GetPathRoot(root);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.Equals(filesystemRoot, comparison) || !string.IsNullOrWhiteSpace(home) && root.Equals(Path.GetFullPath(home), comparison))
            throw new IOException("A filesystem or user-home root cannot be used as an artifact domain.");
    }

    private static void EnsurePrivateChild(CoverageRunOutputLease root, string name)
    {
        var childPath = Path.Join(root.ArtifactDirectoryPath, name);
        if (Directory.Exists(childPath) || File.Exists(childPath))
        {
            using var child = CoverageRunOutputLease.AcquireArtifactDirectory(childPath, false, true)
                ?? throw new IOException("The private artifact child is unavailable.");
            return;
        }
        root.CreateArtifactDirectory(name).Dispose();
    }

    private static CoverageArtifactPolicy? ReadOptionalPolicy(CoverageRunOutputLease root)
    {
        using var stream = TryOpen(root, PolicyName);
        return stream is null ? null : CoverageArtifactPolicy.Read(stream);
    }

    private static T? ReadOptionalJson<T>(CoverageRunOutputLease root, string name, long maximumBytes) where T : class
    {
        using var stream = TryOpen(root, name);
        if (stream is null) return null;
        return CoverageArtifactJson.Read<T>(stream, maximumBytes, JsonOptions);
    }

    private static FileStream? TryOpen(CoverageRunOutputLease root, string name)
    {
        try { return root.OpenArtifactFile(name, createNew: false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WriteNewJson<T>(CoverageRunOutputLease root, string name, T value)
    {
        using var stream = root.OpenArtifactFile(name, createNew: true);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        stream.Flush(true);
    }

    private static StoreRegistry ReadRegistry(CoverageRunOutputLease root)
    {
        var registry = ReadOptionalJson<StoreRegistry>(root, RegistryName, CoverageArtifactLimits.RegistryMaxBytes)
            ?? throw new InvalidDataException("The artifact registry is missing; preserve the store for inspection.");
        ValidateRegistry(registry);
        return registry;
    }

    private static void ValidateRegistry(StoreRegistry registry)
    {
        if (registry.SchemaVersion != 1 || registry.Registrations is null || registry.Reservations is null
            || registry.OverflowObservations is null || registry.ExpiryReceipts is null
            || registry.ExpiryReceipts.Length > CoverageArtifactLimits.ExpiryReceiptsMaxEntries
            || JsonSerializer.SerializeToUtf8Bytes(registry.ExpiryReceipts, JsonOptions).LongLength > CoverageArtifactLimits.ExpiryReceiptsMaxBytes
            || registry.Registrations.Length > CoverageArtifactLimits.MaximumRegistrations
            || registry.Reservations.Length > CoverageArtifactLimits.MaximumOverflowObservations
            || registry.OverflowObservations.Length > CoverageArtifactLimits.MaximumOverflowObservations
            || registry.Reservations.Any(item => item is null || item.Bytes < 0 || item.Bytes > CoverageArtifactLimits.DomainStagingMaxBytes))
            throw new InvalidDataException("The artifact registry uses an unsupported schema or exceeds its registration bound.");
        if (registry.OverflowObservations.Any(item => item is null
                || !Guid.TryParseExact(item.RunId, "N", out _)
                || string.IsNullOrWhiteSpace(item.OutputIdentity) || item.CurrentBytes < 0)
            || registry.ExpiryReceipts.Any(item => item is null
                || !Guid.TryParseExact(item.RunId, "N", out _)
                || item.Outcome is not ("success" or "failed" or "interrupted")
                || item.StoredBytes < 0 || string.IsNullOrWhiteSpace(item.Reason)))
            throw new InvalidDataException("The artifact registry contains invalid observation or expiry receipt facts.");
        if (registry.Registrations.Any(item => item is null)
            || registry.Registrations.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() != registry.Registrations.Length)
            throw new InvalidDataException("The artifact registry contains duplicate run identifiers.");
        foreach (var reservation in registry.Reservations)
        {
            if (!Guid.TryParseExact(reservation.Id, "N", out _) || !Guid.TryParseExact(reservation.RunId, "N", out _))
                throw new InvalidDataException("The artifact registry contains an invalid staging reservation.");
        }
        foreach (var item in registry.Registrations)
        {
            if (item is null || item.Inventory is null || item.OutputIdentity is null || item.RepositoryIdentity is null
                || item.OutputPath is null || item.RepositoryPath is null || item.Version is null
                || item.CurrentBytes < 0 || item.ArchiveBytes < 0
                || item.Outcome is not ("success" or "failed" or "interrupted"))
                throw new InvalidDataException("The artifact registry contains invalid registration facts.");
            ValidateRun(ToCurrentRun(item));
        }
    }

    private static CoverageArtifactPolicy DefaultPolicy()
        => CoverageArtifactPolicy.Read(new MemoryStream("{\"schemaVersion\":1}"u8.ToArray(), writable: false));

    private static void PersistRegistry(CoverageRunOutputLease root, StoreRegistry registry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(registry, JsonOptions);
        if (bytes.Length > CoverageArtifactLimits.RegistryMaxBytes)
            throw new InvalidDataException("The artifact registry reached its bounded metadata capacity.");
        using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root.ArtifactDirectoryPath, StagingName), false, true)!;
        // Every caller holds the store's exclusive metadata lease. A single fixed slot
        // bounds crash remnants and cannot belong to a concurrently active publisher.
        RemoveAbandonedRegistryStage(root);
        string? stagedIdentity = null;
        try
        {
            using (var stream = staging.OpenArtifactFile(RegistryStageName, createNew: true))
            {
                stagedIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(stream.SafeFileHandle);
                stream.Write(bytes);
                stream.Flush(true);
            }
            staging.MoveArtifactEntry(RegistryStageName, root, RegistryName, replaceFile: true, expectedSourceIdentity: stagedIdentity);
            stagedIdentity = null;
        }
        finally
        {
            if (stagedIdentity is not null)
            {
                try { staging.DeleteArtifactEntry(RegistryStageName, 1, stagedIdentity); }
                catch (Exception exception) when (IsOptionalFailure(exception)) { }
            }
        }
    }

    private static void RemoveAbandonedRegistryStage(CoverageRunOutputLease root)
    {
        using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root.ArtifactDirectoryPath, StagingName), false, true)!;
        using var file = TryOpen(staging, RegistryStageName);
        if (file is null) return;
        if (file.Length > CoverageArtifactLimits.RegistryMaxBytes)
            throw new InvalidDataException("Registry staging exceeds its bounded publication slot; preserve it for inspection.");
        var identity = CoverageRunOutputLease.GetArtifactObjectIdentity(file.SafeFileHandle);
        file.Dispose();
        staging.DeleteArtifactEntry(RegistryStageName, 1, identity);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static CoverageArtifactRunManifest ReadManifest(CoverageRunOutputLease archive)
    {
        using var stream = archive.OpenArtifactFile("manifest.json", false);
        return CoverageArtifactArchive.ReadManifest(stream);
    }

    private static string GetDirectoryIdentity(string path)
    {
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetFullPath(path), false, false)
            ?? throw new DirectoryNotFoundException("A registered artifact directory is unavailable.");
        return directory.ArtifactDirectoryIdentity;
    }

    private static void ValidateRun(CoverageArtifactCurrentRun run)
    {
        ArgumentNullException.ThrowIfNull(run.ManifestBase);
        ArgumentNullException.ThrowIfNull(run.Inventory);
        if (run.ManifestBase.SchemaVersion != 1 || run.ManifestBase.Outcome is not ("success" or "failed" or "interrupted")
            || run.ManifestBase.Version is null || run.ManifestBase.Version.Length > 256)
            throw new InvalidDataException("Current-run metadata is unsupported or incomplete.");
        ValidateRunId(run.ManifestBase.RunId);
        if (run.Inventory.Count > CoverageArtifactLimits.MaximumFiles)
            throw new InvalidDataException("The current-run inventory exceeds its file limit.");
        if (JsonSerializer.SerializeToUtf8Bytes(run.Inventory, JsonOptions).LongLength > CoverageArtifactLimits.RunInventoryMaxBytes)
            throw new InvalidDataException("The current-run inventory exceeds its encoded size limit.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in run.Inventory)
        {
            if (item is null || item.Path is null || item.Sha256 is null || item.Length < 0
                || item.Sha256.Length != 64 || item.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The current-run inventory contains invalid file facts.");
            if (item.Path.Contains('\\')) throw new InvalidDataException("The current-run inventory path must be portable.");
            var relative = item.Path;
            if (relative.StartsWith('/') || relative.Split('/').Any(component => component is "" or "." or ".."))
                throw new InvalidDataException("The current-run inventory contains an unsafe relative path.");
            foreach (var component in relative.Split('/')) CoverageRunOutputLease.ValidateArtifactComponent(component);
            if (!paths.Add(relative)) throw new InvalidDataException("The current-run inventory contains duplicate paths.");
        }
    }

    private static void ValidateRunId(string runId)
    {
        if (runId is null || runId.Length != 32 || !Guid.TryParseExact(runId, "N", out _))
            throw new InvalidDataException("The run identifier must be a 32-character N-format GUID.");
    }

    private static StoreRegistration[] UpdatePin(StoreRegistration[] registrations, string runId, bool pin)
        => registrations.Select(item => item.RunId == runId ? item with { Pinned = pin } : item).ToArray();

    private static CoverageArtifactCurrentRun ToCurrentRun(StoreRegistration registration)
        => new(new CoverageArtifactRunManifest
        {
            RunId = registration.RunId,
            OutputIdentity = registration.OutputIdentity,
            RepositoryIdentity = registration.RepositoryIdentity,
            Outcome = registration.Outcome,
            CreatedAtUtc = registration.CreatedAtUtc,
            Version = registration.Version,
            Provenance = registration.Provenance,
        }, registration.Inventory, registration.OutputPath, registration.RepositoryPath);

    private static long DirectoryBytes(string path)
    {
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)
            ?? throw new DirectoryNotFoundException("The published archive disappeared.");
        long total = 0;
        foreach (var entry in directory.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumFiles + 2))
        {
            if (entry.IsDirectory) throw new InvalidDataException("Unexpected nested archive directory.");
            if (entry.Name == ".appsurface-coverage-operation.lock") continue;
            using var stream = directory.OpenArtifactFile(entry.Name, false);
            total = checked(total + stream.Length);
        }
        return total;
    }

    /// <summary>Counts bounded domain metadata, including the non-authoritative warning and inspection observations.</summary>
    private static long MetadataBytes(CoverageRunOutputLease root)
    {
        var total = root.EnumerateArtifactEntries(16)
            .Where(entry => !entry.IsDirectory && entry.Name != ".appsurface-coverage-operation.lock")
            .Sum(entry => { using var stream = root.OpenArtifactFile(entry.Name, false); return stream.Length; });
        using (var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root.ArtifactDirectoryPath, StagingName), false, true)!)
        using (var registryStage = TryOpen(staging, RegistryStageName))
        {
            if (registryStage is not null)
            {
                if (registryStage.Length > CoverageArtifactLimits.RegistryMaxBytes)
                    throw new InvalidDataException("Registry staging exceeds its bounded publication slot; preserve it for inspection.");
                total = checked(total + registryStage.Length);
            }
        }
        foreach (var name in new[] { "warnings", "inspection" })
        {
            using var state = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root.ArtifactDirectoryPath, StagingName, name), false, true);
            if (state is null) continue;
            foreach (var entry in state.EnumerateArtifactEntries(4))
            {
                if (entry.IsDirectory || entry.Name == ".appsurface-coverage-operation.lock") continue;
                if (entry.Name != "state.json" && entry.Name != CoverageArtifactMetadataPublication.StagingName)
                    throw new InvalidDataException("Unknown observation metadata requires reconciliation.");
                using var stream = state.OpenArtifactFile(entry.Name, false);
                var maximum = name == "inspection" ? 64L * 1024 : CoverageArtifactLimits.WarningStateMaxBytes;
                if (stream.Length > maximum)
                    throw new InvalidDataException("Observation metadata exceeds its category byte limit.");
                total = checked(total + stream.Length);
            }
        }
        return total;
    }
}
