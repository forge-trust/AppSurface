using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Coordinates bounded receipt metadata and payload capacity for one store root.</summary>
/// <remarks>
/// The index is protected by a short native lease on the store root. No caller holds that lease while a producer
/// runs. Maintenance holds the index lease and only attempts per-key leases nonblocking, preserving the key-then-index
/// order used by execution paths without creating a lock cycle. Serialized-index pressure evicts only validated,
/// settled LRU receipts after their exclusive key lease is acquired. It durably publishes an authority-free eviction
/// tombstone before deleting tracked payloads, then publishes the reclaimed quota. Payload reservations are
/// reconciled only from persisted reservation-to-stage/payload bindings while that same exclusive key lease is held;
/// legacy unbound reservations and unresolved execution authority remain conservative.
/// Readers and writers validate the same identities, directory components and custody aliases. Mutations reject
/// invalid candidate entries with <see cref="InvalidDataException"/> before maintenance or publication, preserving the previous index.
/// Capacity and serialized-size decisions remain separate so valid over-capacity candidates can use safe eviction.
/// </remarks>
internal static class EvidenceReceiptQuota
{
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    internal const int EssentialReservationBytes = 64 * 1024;
    internal const int IndexMaximumBytes = 16 * 1024 * 1024;
    private const int IndexSchemaVersion = 4;
    private const int MaximumRetiredPayloadsPerKey = 64;
    private const int MaximumEssentialReservationsPerKey = 64;
    private const int MaximumPayloadReservationsPerKey = 64;
    private static readonly TimeSpan MaximumIndexWait = TimeSpan.FromSeconds(30);

    /// <summary>Reserves essential authority capacity for an ordered batch of keys.</summary>
    /// <param name="rootPath">The private receipt store root.</param>
    /// <param name="repositoryNamespace">The validated repository namespace.</param>
    /// <param name="keys">The requested producer keys.</param>
    /// <param name="now">The current time used by ordinary quota maintenance.</param>
    /// <param name="waitTimeout">Remaining caller-owned wait budget; this root-index wait is capped at 30 seconds.</param>
    /// <param name="cancellationToken">Cancels a bounded wait for the root-index native lease.</param>
    /// <param name="unixDirectoryFsyncForTests">Optional directory-sync failure seam; production omits it.</param>
    /// <param name="onIndexContentionForTests">Optional fixture signal after an unsuccessful native lock attempt.</param>
    /// <remarks>
    /// A null timeout preserves the historical 30-second wait. Larger or infinite timeouts are capped at 30 seconds;
    /// zero permits one immediate acquisition attempt but does not wait if the index is busy, and other negative
    /// timeouts are rejected. Cancellation is checked while waiting and again after acquiring the native lease before
    /// index authority is read or changed.
    /// </remarks>
    internal static string ReserveEssential(string rootPath, string repositoryNamespace, IReadOnlyList<string> keys,
        DateTimeOffset now, TimeSpan? waitTimeout = null, CancellationToken cancellationToken = default,
        Func<int, int>? unixDirectoryFsyncForTests = null, Action? onIndexContentionForTests = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservationId = Guid.NewGuid().ToString("N");
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath, waitTimeout, cancellationToken, onIndexContentionForTests);
        cancellationToken.ThrowIfCancellationRequested();
        var index = ReadIndex(root);
        if (index.Entries.Any(static entry => entry.EvictionPending))
        {
            // Re-publish under the index lease before tombstone cleanup. A previous rename may be visible
            // even though its directory sync failed; cleanup must not treat that ambiguous state as durable.
            PublishIndex(root, index, unixDirectoryFsyncForTests);
            if (ReclaimPublishedEvictionsLocked(rootPath, index))
                PublishIndex(root, index, unixDirectoryFsyncForTests);
        }
        var protectedKeys = keys.Select(key => EntryIdentity(repositoryNamespace, key)).ToHashSet(StringComparer.Ordinal);
        var missingKeyCount = keys.Distinct(StringComparer.Ordinal)
            .Count(key => Find(index, repositoryNamespace, key) is null);
        var requiresEntryCapacity = index.Entries.Count + missingKeyCount > MaximumEntries;
        MaintainQuotaLocked(root, rootPath, index, requiredBytes: checked((long)keys.Count * EssentialReservationBytes),
            now, requiredEntryCount: requiresEntryCapacity ? missingKeyCount : 0, protectedKeys: protectedKeys);
        if (requiresEntryCapacity && index.Entries.Count + missingKeyCount > MaximumEntries)
        {
            WriteIndex(root, rootPath, index, protectedKeys, unixDirectoryFsyncForTests);
            missingKeyCount = keys.Distinct(StringComparer.Ordinal)
                .Count(key => Find(index, repositoryNamespace, key) is null);
            if (index.Entries.Count + missingKeyCount > MaximumEntries)
                throw new EvidenceReceiptQuotaUnavailableException("essential-entry-capacity-unavailable");
        }
        foreach (var key in keys)
        {
            var entry = FindForMutation(index, repositoryNamespace, key);
            AddEssentialReservation(index, entry, repositoryNamespace, key, reservationId, EssentialReservationBytes);
        }
        using var namespaceRoot = EnsureChildDirectory(root, repositoryNamespace);
        foreach (var key in keys) using (EnsureChildDirectory(namespaceRoot, key)) { }
        WriteIndex(root, rootPath, index, protectedKeys, unixDirectoryFsyncForTests);
        return reservationId;
    }

    /// <summary>Confirms one essential reservation while its caller owns the exclusive producer-key lease.</summary>
    /// <param name="rootPath">The private receipt store root.</param>
    /// <param name="repositoryNamespace">The validated repository namespace.</param>
    /// <param name="key">The producer key whose exclusive native lease is held.</param>
    /// <param name="reservationId">The reservation ID returned by <see cref="ReserveEssential"/>.</param>
    /// <param name="now">The current time used by ordinary quota maintenance.</param>
    /// <param name="waitTimeout">Remaining caller-owned wait budget; this root-index wait is capped at 30 seconds.</param>
    /// <param name="cancellationToken">Cancels a bounded wait for the root-index native lease.</param>
    /// <param name="onIndexContentionForTests">Optional fixture signal after an unsuccessful native lock attempt.</param>
    /// <remarks>
    /// A null timeout preserves the historical 30-second wait. Larger or infinite timeouts are capped at 30 seconds;
    /// zero permits one immediate acquisition attempt but does not wait if the index is busy, and other negative
    /// timeouts are rejected. Cancellation is checked while waiting and again after acquiring the native lease before
    /// index authority is read or changed. If eviction won before the key lease was acquired, confirmation
    /// re-publishes the tombstone and finishes its cleanup under that lease before recreating the reservation.
    /// </remarks>
    internal static void ConfirmEssentialReservation(string rootPath, string repositoryNamespace, string key,
        string reservationId, DateTimeOffset now, TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default, Action? onIndexContentionForTests = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath, waitTimeout, cancellationToken, onIndexContentionForTests);
        cancellationToken.ThrowIfCancellationRequested();
        var index = ReadIndex(root);
        var entry = Find(index, repositoryNamespace, key);
        if (entry is { EvictionPending: true })
        {
            // An earlier publish may have made the tombstone visible without flushing its directory entry.
            PublishIndex(root, index);
            using var keyRoot = OpenRoot(Path.Combine(rootPath, repositoryNamespace, key));
            if (!TryReclaimEvictedPayloads(keyRoot, entry))
                throw new EvidenceReceiptQuotaUnavailableException("receipt-key-eviction-pending");
            index.Entries.Remove(entry);
            entry = null;
        }
        if (entry?.EssentialReservationIds?.Contains(reservationId, StringComparer.Ordinal) == true) return;
        var protectedKeys = new HashSet<string>([EntryIdentity(repositoryNamespace, key)], StringComparer.Ordinal);
        MaintainQuotaLocked(root, rootPath, index, EssentialReservationBytes, now,
            requiredEntryCount: entry is null ? 1 : 0,
            protectedKeys: protectedKeys);
        entry = FindForMutation(index, repositoryNamespace, key);
        if (entry is null && index.Entries.Count >= MaximumEntries)
        {
            WriteIndex(root, rootPath, index, protectedKeys);
            entry = FindForMutation(index, repositoryNamespace, key);
        }
        var minimumMetadataBytes = EssentialReservationBytes;
        if (entry is not null)
        {
            var stateBytes = entry.AuthorityState is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(entry.AuthorityState).Length;
            minimumMetadataBytes = Math.Max(0, EssentialReservationBytes - stateBytes);
        }
        AddEssentialReservation(index, entry, repositoryNamespace, key, reservationId, minimumMetadataBytes);
        WriteIndex(root, rootPath, index, protectedKeys);
    }

    private static void AddEssentialReservation(EvidenceReceiptQuotaIndex index,
        EvidenceReceiptQuotaEntry? entry, string repositoryNamespace, string key, string reservationId,
        long minimumMetadataBytes)
    {
        if (entry is null)
        {
            if (index.Entries.Count >= MaximumEntries)
                throw new EvidenceReceiptQuotaUnavailableException("essential-entry-capacity-unavailable");
            index.Entries.Add(new EvidenceReceiptQuotaEntry(repositoryNamespace, key, EssentialReservationBytes, 0, 0,
                EssentialReservationBytes, true, false, false, null, null, null, false, RetiredPayloads: [],
                EssentialReservationIds: [reservationId]));
            return;
        }

        var ids = entry.EssentialReservationIds ?? [];
        var alreadyReserved = ids.Contains(reservationId, StringComparer.Ordinal);
        if (ids.Count >= MaximumEssentialReservationsPerKey && !alreadyReserved)
            throw new EvidenceReceiptQuotaUnavailableException("essential-reservation-capacity-unavailable");
        Replace(index, entry, entry with
        {
            EssentialReservationIds = alreadyReserved ? ids : ids.Append(reservationId).ToArray(),
            EssentialReserved = true,
            MetadataBytes = Math.Max(entry.MetadataBytes, minimumMetadataBytes)
        });
    }

    /// <summary>Releases one speculative hold only while its caller still owns the native key lease.</summary>
    /// <param name="rootPath">Private receipt root.</param>
    /// <param name="repositoryNamespace">Repository namespace owning the key.</param>
    /// <param name="key">Key held by the caller's native lease.</param>
    /// <param name="reservationId">Exact speculative reservation to release.</param>
    /// <param name="waitTimeout">Optional root-index wait; zero makes failed-acquisition cleanup nonblocking.</param>
    internal static void ReleaseEssentialReservation(string rootPath, string repositoryNamespace, string key, string reservationId,
        TimeSpan? waitTimeout = null)
    {
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath, waitTimeout);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, repositoryNamespace, key);
        if (entry is null || entry.EssentialReservationIds?.Contains(reservationId, StringComparer.Ordinal) != true) return;
        var state = entry.AuthorityState;
        var authority = AuthorityFlags.FromState(state);
        if (!authority.Matches(entry)) return;
        var remaining = entry.EssentialReservationIds.Where(id => id != reservationId).ToArray();
        if (state is null && remaining.Length == 0 && entry.PayloadDirectoryName is null && entry.PayloadBytes == 0
            && entry.ReservedPayloadBytes == 0 && entry.RetiredPayloads.Count == 0 && entry.ProducerId is null
            && entry.InputIdentityDigest is null)
        {
            index.Entries.Remove(entry);
        }
        else
        {
            var stateBytes = state is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(state).Length;
            var essential = remaining.Length != 0 || authority.RequiresEssentialHold;
            Replace(index, entry, entry with
            {
                EssentialReservationIds = remaining,
                EssentialReserved = essential,
                MetadataBytes = essential ? Math.Max(0, EssentialReservationBytes - stateBytes) : 0
            });
        }
        WriteIndex(root, rootPath, index);
    }

    /// <summary>Persists a bounded payload reservation with its intended stage and final payload names.</summary>
    /// <param name="rootPath">The private receipt store root.</param>
    /// <param name="repositoryNamespace">The validated repository namespace.</param>
    /// <param name="key">The producer key whose exclusive native lease the caller holds.</param>
    /// <param name="now">The current time used by ordinary age/quota maintenance.</param>
    /// <param name="reservation">The binding created by <see cref="EvidenceReceiptPayloadReservation.Create"/>,
    /// including its byte charge and the exact stage and final names.</param>
    /// <returns><see langword="true"/> when the reservation was durably added or already exists identically.</returns>
    /// <remarks>
    /// Persist this binding before creating either directory and retain it for cleanup. A later owner can then
    /// reconcile these exact names after acquiring the exclusive key lease. A binding does not authorize execution
    /// or settle <c>InFlightExecutionId</c>; callers must preserve
    /// unresolved authority even when the reservation is reclaimed. An identical retry republishes the index so a
    /// prior directory-sync failure after rename cannot be mistaken for confirmed durable publication. Publication
    /// fails closed on filesystems without the required native directory-entry durability operation; if it throws,
    /// do not create the stage and retry with the same binding only after safely reacquiring the key lease.
    /// </remarks>
    internal static bool TryReservePayload(string rootPath, string repositoryNamespace, string key, DateTimeOffset now,
        EvidenceReceiptPayloadReservation reservation)
    {
        if (!IsValidPayloadReservation(reservation))
            throw new ArgumentException("Payload reservation binding must use a 32-character hexadecimal id and its generated stage/payload names.");
        var bytes = reservation.ReservedBytes;
        if (bytes <= 0 || bytes > MaximumBytes) return false;
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, repositoryNamespace, key)
            ?? throw new InvalidOperationException("Essential receipt capacity was not reserved before payload staging.");
        var reservations = entry.PayloadReservations ?? [];
        var existing = reservations.SingleOrDefault(item => item.ReservationId == reservation.ReservationId);
        if (existing is not null)
        {
            if (existing != reservation)
                throw new InvalidDataException("A payload reservation identifier is already bound to another stage.");
            // The prior publication may have thrown after rename but before its directory sync succeeded.
            WriteIndex(root, rootPath, index);
            return true;
        }
        if (reservations.Count >= MaximumPayloadReservationsPerKey)
            throw new EvidenceReceiptQuotaUnavailableException("payload-reservation-capacity-unavailable");
        var maintenanceChanged = MaintainQuotaLocked(root, rootPath, index, requiredBytes: bytes, now);
        if (maintenanceChanged) WriteIndex(root, rootPath, index);
        entry = FindForMutation(index, repositoryNamespace, key)
            ?? throw new InvalidOperationException("Essential receipt capacity was not reserved before payload staging.");
        if (UsedBytes(index, root) > MaximumBytes - bytes)
        {
            WriteIndex(root, rootPath, index);
            return false;
        }
        Replace(index, entry, entry with
        {
            ReservedPayloadBytes = checked(entry.ReservedPayloadBytes + bytes),
            PayloadReservations = (entry.PayloadReservations ?? []).Append(reservation).ToArray()
        });
        WriteIndex(root, rootPath, index);
        return true;
    }

    /// <summary>Releases one exact reservation while its caller holds the corresponding exclusive key lease.</summary>
    /// <param name="rootPath">The private receipt store root.</param>
    /// <param name="repositoryNamespace">The validated repository namespace.</param>
    /// <param name="key">The producer key.</param>
    /// <param name="reservationId">The identifier of the bound reservation.</param>
    /// <remarks>
    /// Call only after cleanup of both bound directories is confirmed while the corresponding exclusive key lease is
    /// held. If cleanup is ambiguous, leave the accounting row for reconciliation. Byte-only release cannot
    /// disambiguate equal-sized reservations.
    /// </remarks>
    internal static void ReleasePayloadReservation(string rootPath, string repositoryNamespace, string key, string reservationId)
    {
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, repositoryNamespace, key);
        if (entry is null) return;
        var reservations = entry.PayloadReservations ?? [];
        var reservation = reservations.SingleOrDefault(item => item.ReservationId == reservationId);
        if (reservation is null) return;
        Replace(index, entry, entry with
        {
            ReservedPayloadBytes = Math.Max(0, entry.ReservedPayloadBytes - reservation.ReservedBytes),
            PayloadReservations = reservations.Where(item => item.ReservationId != reservationId).ToArray()
        });
        WriteIndex(root, rootPath, index);
    }

    /// <summary>Reconciles speculative essential holds and bound orphan payload reservations under a live key lease.</summary>
    /// <param name="keyLease">The non-disposed exclusive key lease acquired by the current caller.</param>
    /// <param name="waitTimeout">Remaining caller-owned acquisition budget, capped at 30 seconds for the root-index wait.</param>
    /// <param name="cancellationToken">Cancels the root-index wait before reconciliation changes authority.</param>
    /// <returns>
    /// The number of speculative payload bytes released because their bound directories are absent or smaller than
    /// reserved. Zero does not mean the index was unchanged: essential reservation IDs may still have been pruned.
    /// </returns>
    /// <remarks>
    /// Call immediately after acquiring the key lease and confirming its essential reservation, before creating a
    /// new payload reservation or stage. Reconciliation retains this actual owner's essential hold and drops other
    /// preparers' speculative IDs. Each waiter must re-confirm its own essential reservation and capacity after
    /// acquiring the key, before reconciliation or execution; a prior preparation reservation alone is insufficient.
    /// The method checks only the exact persisted stage/final names, securely measures a single recognized directory,
    /// and retains its actual bytes. Ambiguous, foreign, linked, malformed, over-bound, or legacy unbound state is
    /// left unchanged; reservation age and process liveness are never used to reclaim it. It never deletes payloads or
    /// changes authoritative execution/audit/quarantine state; in particular, an unresolved InFlight record continues
    /// to block execution after quota recovery.
    /// </remarks>
    internal static long ReconcilePayloadReservations(EvidenceReceiptStore.EvidenceReceiptKeyLease keyLease,
        TimeSpan? waitTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyLease);
        keyLease.VerifyBinding(); // Avoid an unbudgeted authority read before acquiring the index lease.
        using var root = OpenRoot(keyLease.StoreRoot);
        using var indexLease = AcquireIndexLease(keyLease.StoreRoot, waitTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var index = ReadIndex(root);
        var entry = FindForMutation(index, keyLease.RepositoryNamespace, keyLease.Key);
        var releasedSpeculativeHolds = entry?.EssentialReservationIds is { Count: > 1 } ids
            && ids.Contains(keyLease.EssentialReservationId, StringComparer.Ordinal);
        if (releasedSpeculativeHolds)
        {
            // Only this actual key owner can have advanced beyond preparation. Waiting preparers confirm
            // their own ID after they obtain the key; retain this owner's hold and every authority/payload byte.
            Replace(index, entry!, entry! with { EssentialReservationIds = [keyLease.EssentialReservationId] });
            entry = FindForMutation(index, keyLease.RepositoryNamespace, keyLease.Key);
        }
        if (entry?.PayloadReservations is not { Count: > 0 })
        {
            if (releasedSpeculativeHolds) WriteIndex(root, keyLease.StoreRoot, index);
            return 0;
        }
        using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyLease.DirectoryPath, false, true)
            ?? throw new IOException("Receipt key directory is unavailable during reservation reconciliation.");
        var released = ReconcilePayloadReservationsLocked(index, entry, keyRoot);
        if (releasedSpeculativeHolds || released != 0 || Find(index, keyLease.RepositoryNamespace, keyLease.Key) != entry)
            WriteIndex(root, keyLease.StoreRoot, index);
        return released;
    }

    /// <summary>Publishes authority for a live key lease and accounts for its serialized size.</summary>
    /// <param name="keyLease">The exclusive lease for the authority key.</param>
    /// <param name="state">The replacement authority snapshot for that key.</param>
    /// <remarks>Verifies the key binding before measuring the snapshot and again before opening the quota index.</remarks>
    /// <exception cref="IOException">The serialized snapshot exceeds the authority capacity.</exception>
    internal static void RecordState(EvidenceReceiptStore.EvidenceReceiptKeyLease keyLease, EvidenceReceiptState state)
    {
        keyLease.VerifyBinding();
        var stateBytes = MeasureAuthorityState(state);
        keyLease.VerifyBinding();
        RecordStateCore(keyLease.StoreRoot, keyLease.RepositoryNamespace, keyLease.Key, state, stateBytes);
    }

    /// <summary>Publishes one key's authority snapshot and derives its metadata charge from the encoded state.</summary>
    /// <param name="rootPath">Private receipt root containing the caller's essential reservation.</param>
    /// <param name="repositoryNamespace">Canonical repository identity owning the key.</param>
    /// <param name="key">Canonical producer content key whose native lease the caller holds.</param>
    /// <param name="state">Non-null authority snapshot for the same key, with valid final payload components.</param>
    /// <remarks>A null state payload name retains the entry's existing payload binding. The caller retains its key lease
    /// throughout publication; this method acquires only the short index lease. Invalid candidate entries are refused
    /// before quota maintenance or publication, preserving the previous index.</remarks>
    /// <exception cref="InvalidDataException">The existing index or candidate violates persisted entry invariants.</exception>
    /// <exception cref="IOException">The serialized snapshot exceeds the authority capacity.</exception>
    internal static void RecordState(string rootPath, string repositoryNamespace, string key, EvidenceReceiptState state)
        => RecordStateCore(rootPath, repositoryNamespace, key, state, MeasureAuthorityState(state));

    private static int MeasureAuthorityState(EvidenceReceiptState state)
    {
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state).Length;
        if (stateBytes > EvidenceReceiptState.MaximumSerializedBytes)
            throw new IOException("Essential receipt authority capacity is unavailable.");
        return stateBytes;
    }

    private static void RecordStateCore(string rootPath, string repositoryNamespace, string key, EvidenceReceiptState state,
        int stateBytes)
    {
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, repositoryNamespace, key)
            ?? throw new EvidenceReceiptQuotaUnavailableException("essential-authority-reservation-missing");
        var reservationIds = entry.EssentialReservationIds ?? [];
        var authority = AuthorityFlags.FromState(state);
        var essential = authority.RequiresEssentialHold || reservationIds.Count != 0;
        Replace(index, entry, entry with
        {
            MetadataBytes = essential ? Math.Max(0, EssentialReservationBytes - stateBytes) : 0,
            EssentialReserved = essential,
            HasInFlight = authority.HasInFlight,
            HasAuditIntent = authority.HasAuditIntent,
            Quarantined = authority.Quarantined,
            OriginCompletedAt = state.OriginCompletedAt ?? entry.OriginCompletedAt,
            LastSuccessfulUse = state.LastSuccessfulUse,
            PayloadDirectoryName = state.PayloadDirectoryName ?? entry.PayloadDirectoryName,
            AuthorityState = state
        });
        WriteIndex(root, rootPath, index);
    }

    internal static EvidenceReceiptState? ReadAuthority(string rootPath, string repositoryNamespace, string key,
        bool readOnly = false)
    {
        using var root = readOnly ? OpenExistingRootOrNull(rootPath) : OpenRoot(rootPath);
        if (root is null) return null;
        if (readOnly) return Find(ReadIndex(root), repositoryNamespace, key)?.AuthorityState;
        using var indexLease = AcquireIndexLease(rootPath);
        return Find(ReadIndex(root), repositoryNamespace, key)?.AuthorityState;
    }

    /// <summary>Returns settled authorities in quota-index order from a single index snapshot.</summary>
    /// <remarks>The caller must still verify each payload. The index lease, when requested, ends before payload inspection.</remarks>
    /// <param name="rootPath">The private receipt store root.</param>
    /// <param name="repositoryNamespace">The exact repository namespace to search.</param>
    /// <param name="producerId">The producer whose published origins can be compared.</param>
    /// <param name="now">The time used to screen indexed origin ages.</param>
    /// <param name="readOnly">When true, reads the published index without creating a root or acquiring its lease.</param>
    internal static IReadOnlyList<EvidenceReceiptState> ReadComparable(string rootPath,
        string repositoryNamespace, string producerId, DateTimeOffset now, bool readOnly = false)
    {
        using var root = readOnly ? OpenExistingRootOrNull(rootPath) : OpenRoot(rootPath);
        if (root is null) return Array.Empty<EvidenceReceiptState>();
        CoverageArtifactOperationLease? indexLease = readOnly ? null : AcquireIndexLease(rootPath);
        using (indexLease)
        {
            return ReadIndex(root).Entries
                .Where(entry => entry.RepositoryNamespace == repositoryNamespace && entry.ProducerId == producerId
                    && entry.Key.All(char.IsAsciiHexDigitLower)
                    && entry.PayloadDirectoryName is not null && entry.InputIdentityDigest is not null
                    && !entry.EssentialReserved && !entry.HasInFlight && !entry.HasAuditIntent && !entry.Quarantined
                    && entry.OriginCompletedAt is { } completedAt
                    && EvidenceReceiptRetention.GetAgeMissReason(completedAt, now) is null
                    && entry.AuthorityState is { IsSettled: true } state
                    && state.PayloadDirectoryName == entry.PayloadDirectoryName && state.InputIdentityDigest == entry.InputIdentityDigest)
                .OrderByDescending(static entry => entry.OriginCompletedAt)
                .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => entry.AuthorityState!)
                .Take(MaximumEntries).ToArray();
        }
    }

    internal static void CommitInvocation(string rootPath, IReadOnlyList<EvidenceReceiptBatchCommit> commits)
    {
        if (commits.Count == 0) return;
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var identity = commit.RepositoryNamespace + ":" + commit.Key;
            if (!seen.Add(identity)) throw new InvalidDataException("Receipt batch contains a duplicate key.");
            var entry = FindForMutation(index, commit.RepositoryNamespace, commit.Key)
                ?? throw new EvidenceReceiptQuotaUnavailableException("receipt-batch-reservation-missing");
            var prior = entry.AuthorityState;
            if (prior is null || prior.InFlightExecutionId != commit.ExecutionId
                || (prior.AuditIntentExecutionId is not null && prior.AuditIntentExecutionId != commit.ExecutionId))
                throw new InvalidOperationException("Receipt batch no longer owns every selected authority row.");
            if (commit.State.SchemaVersion != EvidenceReceiptState.CurrentSchemaVersion || commit.State.Key != commit.Key
                || !commit.State.IsSettled || commit.State.PayloadDirectoryName != commit.PayloadDirectoryName
                || commit.State.OriginCompletedAt != commit.CompletedAt)
                throw new InvalidDataException("Receipt batch state does not describe the committed settled payload.");
            if (commit.State.PassedProducerRecord is { Length: > EvidenceReceiptState.MaximumPassedProducerBase64Characters })
                throw new EvidenceReceiptQuotaUnavailableException("receipt-passed-producer-record-capacity-unavailable");
            if (JsonSerializer.SerializeToUtf8Bytes(commit.State).Length > EvidenceReceiptState.MaximumSerializedBytes)
                throw new EvidenceReceiptQuotaUnavailableException("receipt-state-capacity-unavailable");
            var payloadReservation = (entry.PayloadReservations ?? [])
                .SingleOrDefault(item => item.PayloadDirectoryName == commit.PayloadDirectoryName);
            if (payloadReservation is null && entry.PayloadReservations is { Count: > 0 })
                throw new EvidenceReceiptQuotaUnavailableException("receipt-batch-reservation-binding-missing");
            var reservationBytes = payloadReservation?.ReservedBytes ?? commit.ReservationBytes;
            if (reservationBytes < 0 || reservationBytes > entry.ReservedPayloadBytes
                || commit.PayloadBytes < 0 || commit.PayloadBytes > reservationBytes
                || (entry.PayloadDirectoryName is not null && entry.PayloadBytes > 0
                    && entry.RetiredPayloads.Count >= MaximumRetiredPayloadsPerKey))
                throw new EvidenceReceiptQuotaUnavailableException("receipt-batch-retention-capacity-unavailable");
            var committedState = commit.State;
            var essentialReservationIds = (entry.EssentialReservationIds ?? [])
                .Where(id => !StringComparer.Ordinal.Equals(id, commit.EssentialReservationId)).ToArray();
            var payloadReservations = payloadReservation is null
                ? entry.PayloadReservations
                : (entry.PayloadReservations ?? []).Where(item => item.ReservationId != payloadReservation.ReservationId).ToArray();
            Replace(index, entry, entry with
            {
                ReservedPayloadBytes = entry.ReservedPayloadBytes - reservationBytes,
                PayloadReservations = payloadReservations,
                EssentialReservationIds = essentialReservationIds,
                RetiredPayloads = entry.PayloadDirectoryName is null || entry.PayloadBytes == 0
                    ? entry.RetiredPayloads
                    : entry.RetiredPayloads.Append(new EvidenceReceiptRetiredPayload(entry.PayloadDirectoryName, entry.PayloadBytes)).ToArray(),
                PayloadBytes = commit.PayloadBytes,
                MetadataBytes = essentialReservationIds.Length == 0 ? 0 : EssentialReservationBytes,
                PayloadDirectoryName = commit.PayloadDirectoryName,
                OriginCompletedAt = commit.CompletedAt,
                LastSuccessfulUse = null,
                EssentialReserved = essentialReservationIds.Length != 0,
                HasInFlight = false,
                HasAuditIntent = false,
                Quarantined = false,
                AuthorityState = committedState,
                ProducerId = commit.ProducerId,
                InputIdentityDigest = committedState.InputIdentityDigest
            });
        }
        WriteIndex(root, rootPath, index);
    }

    /// <summary>Releases retired-payload accounting only after the caller confirms deletion under the key lease.</summary>
    internal static void ReleaseRetiredPayload(string rootPath, string repositoryNamespace, string key,
        string payloadName, bool deletionConfirmed)
    {
        if (!deletionConfirmed) return;
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, repositoryNamespace, key);
        if (entry is null || !entry.RetiredPayloads.Any(payload => payload.DirectoryName == payloadName)) return;
        Replace(index, entry, entry with
        {
            RetiredPayloads = entry.RetiredPayloads.Where(payload => payload.DirectoryName != payloadName).ToArray()
        });
        WriteIndex(root, rootPath, index);
    }

    internal static IReadOnlyList<string> ReadRetiredPayloadNames(string rootPath, string repositoryNamespace, string key)
    {
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        return (Find(ReadIndex(root), repositoryNamespace, key)?.RetiredPayloads ?? [])
            .Select(static payload => payload.DirectoryName).ToArray();
    }

    /// <summary>Atomically invalidates one settled payload authority and transfers its indexed payload charge to retired custody.</summary>
    /// <param name="keyLease">The live exclusive key lease for the receipt being invalidated.</param>
    /// <param name="expectedState">The exact authority snapshot whose payload failed validation.</param>
    /// <param name="invalidatedState">The replacement authority with payload provenance cleared.</param>
    /// <remarks>
    /// The state and quota index are one durable document, so this publishes the invalidated authority, clears its active
    /// payload name and bytes, and records the exact previous name and byte charge as retired in one index replacement.
    /// It refuses unknown, active, quarantined, mismatched, or over-capacity state without deleting files. The caller may
    /// delete the retired payload while still holding <paramref name="keyLease"/> and release its charge only after confirmed absence.
    /// </remarks>
    internal static void InvalidatePayload(EvidenceReceiptStore.EvidenceReceiptKeyLease keyLease,
        EvidenceReceiptState expectedState, EvidenceReceiptState invalidatedState)
    {
        ArgumentNullException.ThrowIfNull(keyLease);
        ArgumentNullException.ThrowIfNull(expectedState);
        ArgumentNullException.ThrowIfNull(invalidatedState);
        keyLease.VerifyBinding();
        using var root = OpenRoot(keyLease.StoreRoot);
        using var indexLease = AcquireIndexLease(keyLease.StoreRoot);
        var index = ReadIndex(root);
        var entry = FindForMutation(index, keyLease.RepositoryNamespace, keyLease.Key);
        if (entry is null || entry.AuthorityState != expectedState || !HasSettledPayloadBinding(entry, expectedState)
            || invalidatedState.Key != expectedState.Key
            || !invalidatedState.IsSettled || invalidatedState.PayloadDirectoryName is not null
            || invalidatedState.OriginManifestDigest is not null || invalidatedState.OriginCompletedAt is not null
            || invalidatedState.PassedProducerRecord is not null || invalidatedState.InputIdentityDigest is not null)
            return;
        if (entry.PayloadDirectoryName is not null && entry.RetiredPayloads.Count >= MaximumRetiredPayloadsPerKey)
            throw new EvidenceReceiptQuotaUnavailableException("receipt-retired-payload-capacity-unavailable");
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(invalidatedState).Length;
        Replace(index, entry, entry with
        {
            MetadataBytes = (entry.EssentialReservationIds?.Count ?? 0) != 0
                ? Math.Max(0, EssentialReservationBytes - stateBytes) : 0,
            EssentialReserved = (entry.EssentialReservationIds?.Count ?? 0) != 0,
            HasInFlight = false,
            HasAuditIntent = false,
            Quarantined = false,
            PayloadDirectoryName = null,
            PayloadBytes = 0,
            OriginCompletedAt = null,
            LastSuccessfulUse = null,
            ProducerId = null,
            InputIdentityDigest = null,
            AuthorityState = invalidatedState,
            RetiredPayloads = entry.PayloadDirectoryName is null
                ? entry.RetiredPayloads
                : entry.RetiredPayloads.Append(new EvidenceReceiptRetiredPayload(entry.PayloadDirectoryName, entry.PayloadBytes)).ToArray()
        });
        WriteIndex(root, keyLease.StoreRoot, index);
    }

    /// <summary>Expires and evicts only indexed owned settled receipts; locked, unknown and quarantined entries are preserved.</summary>
    internal static void Maintain(string rootPath, DateTimeOffset now)
    {
        using var root = OpenRoot(rootPath);
        using var indexLease = AcquireIndexLease(rootPath);
        var index = ReadIndex(root);
        MaintainQuotaLocked(root, rootPath, index, requiredBytes: 0, now);
        WriteIndex(root, rootPath, index);
    }

    internal static long EstimatePayloadReservation(long artifactBytes, int planBytes, int manifestBytes,
        int producerBytes, int identityBytes, int artifactCount)
    {
        if (artifactBytes < 0 || planBytes < 0 || manifestBytes < 0 || producerBytes < 0 || identityBytes < 0
            || artifactCount < 0 || artifactCount > EvidenceReceiptPayload.MaximumPayloadFiles)
            throw new ArgumentOutOfRangeException(nameof(artifactBytes));
        return checked(artifactBytes + planBytes + manifestBytes + producerBytes + identityBytes
            + artifactCount * 2048L + 16 * 1024L);
    }

    internal static long MeasurePayload(CoverageRunOutputLease payloadRoot)
    {
        long total = 0;
        var pending = new Stack<CoverageRunOutputLease>();
        var owned = new List<CoverageRunOutputLease>();
        pending.Push(payloadRoot);
        try
        {
            var count = 0;
            while (pending.TryPop(out var current))
            {
                foreach (var entry in current.EnumerateArtifactEntries(EvidenceReceiptPayload.MaximumQuotaTreeEntries))
                {
                    if (++count > EvidenceReceiptPayload.MaximumQuotaTreeEntries) throw new InvalidDataException("Receipt payload exceeds its quota scan bound.");
                    if (entry.IsDirectory)
                    {
                        var child = CoverageRunOutputLease.AcquireArtifactDirectory(
                            Path.Combine(current.ArtifactDirectoryPath, entry.Name), false, true)
                            ?? throw new DirectoryNotFoundException("Receipt payload child disappeared.");
                        owned.Add(child);
                        pending.Push(child);
                    }
                    else
                    {
                        using var file = current.OpenArtifactFile(entry.Name, false);
                        total = checked(total + file.Length);
                    }
                }
            }
            return total;
        }
        finally { foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose(); }
    }

    // Persisted flags mirror the authority snapshot. Compare both before releasing capacity.
    private readonly record struct AuthorityFlags(bool HasInFlight, bool HasAuditIntent, bool Quarantined)
    {
        internal bool RequiresEssentialHold => HasInFlight || HasAuditIntent || Quarantined;

        internal static AuthorityFlags FromState(EvidenceReceiptState? state)
        {
            return new(state?.InFlightExecutionId is not null, state?.AuditIntentExecutionId is not null,
                state?.IsQuarantined == true);
        }

        internal bool Matches(EvidenceReceiptQuotaEntry entry) =>
            HasInFlight == entry.HasInFlight && HasAuditIntent == entry.HasAuditIntent && Quarantined == entry.Quarantined;
    }

    private static bool HasSettledPayloadBinding(EvidenceReceiptQuotaEntry entry, EvidenceReceiptState state) =>
        state.SchemaVersion == EvidenceReceiptState.CurrentSchemaVersion && state.Key == entry.Key
        && state.IsSettled && AuthorityFlags.FromState(state).Matches(entry)
        && state.PayloadDirectoryName == entry.PayloadDirectoryName
        && state.OriginCompletedAt == entry.OriginCompletedAt
        && (entry.PayloadDirectoryName is not null || entry.PayloadBytes == 0);

    private static bool MaintainQuotaLocked(CoverageRunOutputLease root, string rootPath, EvidenceReceiptQuotaIndex index,
        long requiredBytes, DateTimeOffset now, int requiredEntryCount = 0, IReadOnlySet<string>? protectedKeys = null)
    {
        var selectedEntryCapacityTombstones = 0;
        long selectedPayloadReclaimBytes = 0;
        return MaintainEntriesLocked(rootPath, index, protectedKeys,
            entry => ShouldEvict(entry) || entry.EssentialReservationIds is { Count: > 0 }
                || entry.PayloadReservations is { Count: > 0 } || entry.RetiredPayloads.Count != 0,
            ShouldEvict, AccountForTombstone);

        bool ShouldEvict(EvidenceReceiptQuotaEntry entry)
        {
            var expired = IsExpired(entry.OriginCompletedAt, now);
            var overCapacity = requiredBytes > 0
                && UsedBytes(index, root) - selectedPayloadReclaimBytes > MaximumBytes - requiredBytes;
            var overEntryCapacity = requiredEntryCount > 0
                && index.Entries.Count - selectedEntryCapacityTombstones + requiredEntryCount > MaximumEntries;
            return expired || overCapacity || overEntryCapacity;
        }

        void AccountForTombstone(EvidenceReceiptQuotaEntry? tombstoned)
        {
            if (tombstoned is null) return;

            // Actual bytes remain charged until the durable tombstone is published and cleanup succeeds.
            selectedPayloadReclaimBytes = checked(selectedPayloadReclaimBytes + tombstoned.PayloadBytes
                + tombstoned.RetiredPayloads.Sum(static payload => payload.Bytes));
            if (requiredEntryCount > 0) selectedEntryCapacityTombstones++;
        }
    }

    private static IndexPublication MakeIndexPublicationRoomLocked(CoverageRunOutputLease root, string rootPath,
        EvidenceReceiptQuotaIndex index, IReadOnlySet<string>? protectedKeys, IndexPublication publication)
    {
        MaintainEntriesLocked(rootPath, index, protectedKeys, NeedsCapacity, NeedsCapacity,
            _ => publication = PrepareIndexPublication(root, index));
        return publication;

        bool NeedsCapacity(EvidenceReceiptQuotaEntry _) => !publication.Fits;
    }

    // Recheck eviction eligibility after reconciling a key. Report reclaimable capacity changes
    // to the caller, including the previous entry when a tombstone replaces it.
    private static bool MaintainEntriesLocked(string rootPath, EvidenceReceiptQuotaIndex index,
        IReadOnlySet<string>? protectedKeys, Func<EvidenceReceiptQuotaEntry, bool> needsWork,
        Func<EvidenceReceiptQuotaEntry, bool> shouldEvict, Action<EvidenceReceiptQuotaEntry?> capacityChanged)
    {
        var changed = false;
        foreach (var snapshot in MaintenanceCandidates(index, protectedKeys))
        {
            if (snapshot.EvictionPending || !needsWork(snapshot)) continue;

            var hasPayloadReservation = (snapshot.PayloadReservations?.Count ?? 0) != 0;
            if (snapshot.ReservedPayloadBytes != 0 && !hasPayloadReservation)
                continue; // Legacy/unbound bytes are unknown.
            if (!hasPayloadReservation && (snapshot.HasInFlight || snapshot.HasAuditIntent || snapshot.Quarantined))
                continue;

            var keyPath = Path.Combine(rootPath, snapshot.RepositoryNamespace, snapshot.Key);
            var keyLease = TryAcquireMaintenanceKeyLease(keyPath);
            if (keyLease is null) continue;
            using (keyLease)
            using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyPath, false, true))
            {
                if (keyRoot is null) continue;
                var current = Find(index, snapshot.RepositoryNamespace, snapshot.Key);
                if (current is null || current.EvictionPending) continue;
                var hadPayloadReservation = (current.PayloadReservations?.Count ?? 0) != 0;
                if (hadPayloadReservation)
                {
                    var previous = current;
                    ReconcilePayloadReservationsLocked(index, current, keyRoot);
                    current = Find(index, snapshot.RepositoryNamespace, snapshot.Key);
                    if (current is null) continue;
                    if (!ReferenceEquals(previous, current))
                        ReportCapacityChange();
                }

                // Reconciliation may reduce a speculative charge, but it never clears authority or an ambiguous charge.
                var state = current.AuthorityState;
                if (current.ReservedPayloadBytes != 0 || (current.PayloadReservations?.Count ?? 0) != 0)
                    continue;

                if (state is null)
                {
                    if (!AuthorityFlags.FromState(null).Matches(current)
                        || current.PayloadDirectoryName is not null || current.PayloadBytes != 0
                        || current.RetiredPayloads.Count != 0 || current.ProducerId is not null
                        || current.InputIdentityDigest is not null || current.OriginCompletedAt is not null)
                        continue;
                    index.Entries.Remove(current);
                    ReportCapacityChange();
                    continue;
                }
                if (!HasSettledPayloadBinding(current, state))
                    continue;

                if (current.RetiredPayloads.Count != 0)
                {
                    var remainingRetired = current.RetiredPayloads.Where(retired =>
                        !EvidenceReceiptPayload.TryReclaimCommittedDirectory(keyRoot, retired.DirectoryName)).ToArray();
                    if (remainingRetired.Length != current.RetiredPayloads.Count)
                    {
                        Replace(index, current, current with { RetiredPayloads = remainingRetired });
                        current = Find(index, snapshot.RepositoryNamespace, snapshot.Key)!;
                        ReportCapacityChange();
                    }
                }

                if ((current.EssentialReservationIds?.Count ?? 0) != 0)
                {
                    // The real exclusive key lease proves that no preparer has reached key ownership.
                    // A preparer still waiting for this key must confirm its own reservation after acquisition.
                    Replace(index, current, current with
                    { EssentialReservationIds = [], EssentialReserved = false, MetadataBytes = 0 });
                    current = Find(index, snapshot.RepositoryNamespace, snapshot.Key)!;
                    ReportCapacityChange();
                }

                if (!shouldEvict(current)) continue;

                EvidenceReceiptQuotaEntry? tombstoned = null;
                if (current.PayloadDirectoryName is null && current.RetiredPayloads.Count == 0)
                {
                    index.Entries.Remove(current);
                }
                else
                {
                    Replace(index, current, current with
                    {
                        MetadataBytes = 0,
                        ReservedPayloadBytes = 0,
                        EssentialReservationBytes = 0,
                        EssentialReserved = false,
                        HasInFlight = false,
                        HasAuditIntent = false,
                        Quarantined = false,
                        OriginCompletedAt = null,
                        LastSuccessfulUse = null,
                        AuthorityState = null,
                        ProducerId = null,
                        InputIdentityDigest = null,
                        EssentialReservationIds = [],
                        PayloadReservations = [],
                        EvictionPending = true
                    });
                    tombstoned = current;
                }
                ReportCapacityChange(tombstoned);
            }
        }
        return changed;

        void ReportCapacityChange(EvidenceReceiptQuotaEntry? tombstoned = null)
        {
            changed = true;
            capacityChanged(tombstoned);
        }
    }

    // Call only after successfully publishing the tombstones while holding the index lease.
    private static bool ReclaimPublishedEvictionsLocked(string rootPath, EvidenceReceiptQuotaIndex index,
        IReadOnlySet<string>? protectedKeys = null)
    {
        var changed = false;
        foreach (var snapshot in MaintenanceCandidates(index, protectedKeys))
        {
            if (!snapshot.EvictionPending) continue;
            var keyPath = Path.Combine(rootPath, snapshot.RepositoryNamespace, snapshot.Key);
            using var keyLease = TryAcquireMaintenanceKeyLease(keyPath);
            if (keyLease is null) continue;
            using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyPath, false, true);
            if (keyRoot is null) continue;
            var current = Find(index, snapshot.RepositoryNamespace, snapshot.Key);
            if (current is not { EvictionPending: true } || !TryReclaimEvictedPayloads(keyRoot, current)) continue;
            index.Entries.Remove(current);
            changed = true;
        }
        return changed;
    }

    private static EvidenceReceiptQuotaEntry[] MaintenanceCandidates(EvidenceReceiptQuotaIndex index,
        IReadOnlySet<string>? protectedKeys) =>
        index.Entries.Where(entry => protectedKeys?.Contains(EntryIdentity(entry.RepositoryNamespace, entry.Key)) != true)
            .OrderBy(static entry => entry.LastSuccessfulUse ?? entry.OriginCompletedAt ?? DateTimeOffset.MinValue)
            .ThenBy(static entry => entry.RepositoryNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal).ToArray();

    private static CoverageArtifactOperationLease? TryAcquireMaintenanceKeyLease(string keyPath)
    {
        if (!Directory.Exists(keyPath)) return null;
        try { return CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true); }
        catch (Exception exception) when (CoverageArtifactOperationLease.IsContention(exception)
            || exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static long ReconcilePayloadReservationsLocked(EvidenceReceiptQuotaIndex index,
        EvidenceReceiptQuotaEntry entry, CoverageRunOutputLease keyRoot)
    {
        var reservations = entry.PayloadReservations ?? [];
        if (reservations.Count == 0) return 0;
        var entries = keyRoot.EnumerateArtifactEntries(EvidenceReceiptPayload.MaximumQuotaTreeEntries);
        var remaining = new List<EvidenceReceiptPayloadReservation>(reservations.Count);
        long releasedBytes = 0;
        var changed = false;
        foreach (var reservation in reservations)
        {
            var stageEntry = entries.SingleOrDefault(item => item.Name == reservation.StageDirectoryName);
            var payloadEntry = entries.SingleOrDefault(item => item.Name == reservation.PayloadDirectoryName);
            if (stageEntry is not null && payloadEntry is not null)
            {
                remaining.Add(reservation); // The rename state is ambiguous; preserve the full charge.
                continue;
            }

            var actualEntry = stageEntry ?? payloadEntry;
            if (actualEntry is null)
            {
                releasedBytes = checked(releasedBytes + reservation.ReservedBytes);
                changed = true;
                continue;
            }
            if (!actualEntry.IsDirectory || !TryMeasureBoundPayload(keyRoot, actualEntry.Name, out var actualBytes)
                || actualBytes > reservation.ReservedBytes)
            {
                remaining.Add(reservation); // Foreign, unreadable, or larger-than-reserved state is not reclaimable.
                continue;
            }

            if (actualBytes < reservation.ReservedBytes)
            {
                releasedBytes = checked(releasedBytes + reservation.ReservedBytes - actualBytes);
                remaining.Add(reservation with { ReservedBytes = actualBytes });
                changed = true;
            }
            else
            {
                remaining.Add(reservation);
            }
        }

        if (changed)
            Replace(index, entry, entry with
            {
                ReservedPayloadBytes = entry.ReservedPayloadBytes - releasedBytes,
                PayloadReservations = remaining
            });
        return releasedBytes;
    }

    private static bool TryMeasureBoundPayload(CoverageRunOutputLease keyRoot, string directoryName, out long bytes)
    {
        bytes = 0;
        try
        {
            using var payload = CoverageRunOutputLease.AcquireArtifactDirectory(
                Path.Combine(keyRoot.ArtifactDirectoryPath, directoryName), false, true);
            if (payload is null) return false;
            bytes = MeasurePayload(payload);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException or ArgumentException)
        { return false; }
    }

    private static bool IsExpired(DateTimeOffset? completedAt, DateTimeOffset now)
        => completedAt is { } completed && EvidenceReceiptRetention.GetAgeMissReason(completed, now) is not null;

    /// <summary>Reclaims the payloads of a durably published eviction tombstone under its exclusive key lease.</summary>
    /// <remarks>A failed removal leaves the tombstone and its charges available for a later retry.</remarks>
    private static bool TryReclaimEvictedPayloads(CoverageRunOutputLease keyRoot, EvidenceReceiptQuotaEntry tombstone)
    {
        var deleted = EvidenceReceiptPayload.TryReclaimCommittedDirectory(keyRoot, tombstone.PayloadDirectoryName);
        foreach (var retired in tombstone.RetiredPayloads)
            deleted &= EvidenceReceiptPayload.TryReclaimCommittedDirectory(keyRoot, retired.DirectoryName);
        return deleted;
    }

    private static CoverageRunOutputLease OpenRoot(string path)
        => CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)
            ?? throw new DirectoryNotFoundException("Receipt store root is unavailable.");

    private static CoverageRunOutputLease? OpenExistingRootOrNull(string path)
    {
        var root = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true);
        if (root is not null) return root;
        if (HasOnlyMissingSuffixAndNoLinks(path)) return null;
        throw new IOException("Receipt store root could not be safely opened.");
    }

    private static bool HasOnlyMissingSuffixAndNoLinks(string path)
    {
        var fullPath = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(path));
        var current = Path.GetPathRoot(fullPath) ?? throw new IOException("Receipt store root has no filesystem root.");
        foreach (var component in Path.GetRelativePath(current, fullPath).Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Receipt store root contains a link or reparse point.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("Receipt store root contains a non-directory component.");
        }
        return false;
    }

    /// <summary>Acquires the root-index native lease with a cancellable caller budget capped at 30 seconds.</summary>
    private static CoverageArtifactOperationLease AcquireIndexLease(string rootPath, TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default, Action? onIndexContentionForTests = null)
    {
        var timeout = waitTimeout ?? MaximumIndexWait;
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        if (timeout == Timeout.InfiniteTimeSpan || timeout > MaximumIndexWait) timeout = MaximumIndexWait;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lease = CoverageArtifactOperationLease.Acquire(rootPath, exclusive: true);
                if (cancellationToken.IsCancellationRequested)
                {
                    lease.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return lease;
            }
            catch (CoverageExecutionException exception) when (CoverageArtifactOperationLease.IsContention(exception))
            {
                onIndexContentionForTests?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new EvidenceReceiptQuotaUnavailableException("receipt-index-busy");
                if (cancellationToken.WaitHandle.WaitOne(delay < remaining ? delay : remaining))
                    cancellationToken.ThrowIfCancellationRequested();
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 250));
            }
        }
    }

    private static CoverageRunOutputLease EnsureChildDirectory(CoverageRunOutputLease parent, string name)
    {
        CoverageRunOutputLease.ValidateArtifactComponent(name);
        var path = Path.Combine(parent.ArtifactDirectoryPath, name);
        return CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)
            ?? parent.CreateArtifactDirectory(name);
    }

    private static EvidenceReceiptQuotaIndex ReadIndex(CoverageRunOutputLease root)
    {
        try
        {
            using var stream = root.OpenArtifactFile("quota-index.json", false);
            if (stream.Length > IndexMaximumBytes) throw new InvalidDataException("Receipt quota index exceeds its bound.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var index = JsonSerializer.Deserialize<EvidenceReceiptQuotaIndex>(memory.ToArray());
            if (index is null || index.SchemaVersion != IndexSchemaVersion || index.Entries is null || index.Entries.Count > MaximumEntries)
                throw new InvalidDataException("Receipt quota index is unsupported or invalid.");
            ValidateEntries(index);
            return index;
        }
        catch (FileNotFoundException) { return new EvidenceReceiptQuotaIndex(IndexSchemaVersion, []); }
    }

    private static void ValidateEntries(EvidenceReceiptQuotaIndex index)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.Entries)
        {
            if (entry is null || !IsHash(entry.RepositoryNamespace) || !IsHash(entry.Key)
                || !identities.Add(EntryIdentity(entry.RepositoryNamespace, entry.Key))
                || (entry.PayloadDirectoryName is { } activePayload && !EvidenceReceiptPayloadNames.IsFinalDirectoryName(activePayload))
                || (entry.InputIdentityDigest is not null && !IsHash(entry.InputIdentityDigest))
                || (entry.ProducerId is not null && (entry.ProducerId.Length is 0 or > 256 || entry.ProducerId.Any(char.IsControl)))
                || entry.MetadataBytes < 0 || entry.PayloadBytes < 0 || entry.ReservedPayloadBytes < 0
                || entry.RetiredPayloads is null || entry.RetiredPayloads.Count > MaximumRetiredPayloadsPerKey
                || entry.EssentialReservationIds is not { } essentialReservationIds
                || HasInvalidReservationBindings(entry, essentialReservationIds)
                || HasInvalidRetiredPayloads(entry)
                || HasInvalidAuthority(entry, essentialReservationIds.Count))
                throw new InvalidDataException("Receipt quota index contains unsupported or invalid authority.");
        }
    }

    private static bool HasInvalidReservationBindings(EvidenceReceiptQuotaEntry entry, IReadOnlyList<string> essentialReservationIds)
    {
        if (essentialReservationIds.Count > MaximumEssentialReservationsPerKey
            || essentialReservationIds.Any(static id => id is not { Length: 32 } || !id.All(Uri.IsHexDigit))
            || essentialReservationIds.Distinct(StringComparer.Ordinal).Count() != essentialReservationIds.Count)
            return true;

        // Legacy rows can carry aggregate reservations without exact payload bindings.
        return entry.PayloadReservations is { } reservations
            && (reservations.Count > MaximumPayloadReservationsPerKey
                || reservations.Any(static item => !IsValidPayloadReservation(item))
                || reservations.Select(static item => item.ReservationId).Distinct(StringComparer.Ordinal).Count() != reservations.Count
                || reservations.Select(static item => item.StageDirectoryName).Distinct(StringComparer.Ordinal).Count() != reservations.Count
                || reservations.Select(static item => item.PayloadDirectoryName).Distinct(StringComparer.Ordinal).Count() != reservations.Count
                || reservations.Sum(static item => item.ReservedBytes) > entry.ReservedPayloadBytes
                || reservations.Any(item => string.Equals(item.PayloadDirectoryName,
                    entry.PayloadDirectoryName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasInvalidRetiredPayloads(EvidenceReceiptQuotaEntry entry)
        => entry.RetiredPayloads.Any(retired => retired is null || retired.Bytes < 0
            || !EvidenceReceiptPayloadNames.IsFinalDirectoryName(retired.DirectoryName)
            || string.Equals(retired.DirectoryName, entry.PayloadDirectoryName, StringComparison.OrdinalIgnoreCase)
            || (entry.PayloadReservations is { } bindings && bindings.Any(binding =>
                string.Equals(retired.DirectoryName, binding.PayloadDirectoryName, StringComparison.OrdinalIgnoreCase))));

    private static bool HasInvalidAuthority(EvidenceReceiptQuotaEntry entry, int essentialReservationCount)
    {
        if (entry.EvictionPending && (entry.AuthorityState is not null || entry.MetadataBytes != 0
            || entry.ReservedPayloadBytes != 0 || entry.EssentialReservationBytes != 0
            || entry.EssentialReserved || entry.HasInFlight || entry.HasAuditIntent || entry.Quarantined
            || entry.OriginCompletedAt is not null || entry.LastSuccessfulUse is not null
            || entry.ProducerId is not null || entry.InputIdentityDigest is not null
            || essentialReservationCount != 0 || entry.PayloadReservations is { Count: > 0 }))
            return true;

        if (entry.AuthorityState is not { } state)
            return entry.HasInFlight || entry.HasAuditIntent || entry.Quarantined;

        return state.SchemaVersion != EvidenceReceiptState.CurrentSchemaVersion || state.Key != entry.Key
            || (state.PayloadDirectoryName is { } authorityPayload && !EvidenceReceiptPayloadNames.IsFinalDirectoryName(authorityPayload))
            || !EvidenceReceiptState.AreAuditAssertionsWithinLimit(state.AuditExpectedAssertions);
    }

    private static void WriteIndex(CoverageRunOutputLease root, string rootPath, EvidenceReceiptQuotaIndex index,
        IReadOnlySet<string>? protectedKeys = null, Func<int, int>? unixDirectoryFsyncForTests = null)
    {
        var publication = PrepareIndexPublication(root, index);
        if (!publication.Fits)
            publication = MakeIndexPublicationRoomLocked(root, rootPath, index, protectedKeys, publication);
        PublishPreparedIndex(root, publication, unixDirectoryFsyncForTests);
        if (ReclaimPublishedEvictionsLocked(rootPath, index, protectedKeys))
            PublishIndex(root, index, unixDirectoryFsyncForTests);
    }

    private static void PublishIndex(CoverageRunOutputLease root, EvidenceReceiptQuotaIndex index,
        Func<int, int>? unixDirectoryFsyncForTests = null)
        => PublishPreparedIndex(root, PrepareIndexPublication(root, index), unixDirectoryFsyncForTests);

    private static void PublishPreparedIndex(CoverageRunOutputLease root, IndexPublication publication,
        Func<int, int>? unixDirectoryFsyncForTests)
    {
        if (!publication.Fits)
            throw new EvidenceReceiptQuotaUnavailableException("receipt-quota-index-capacity-unavailable");
        CoverageArtifactMetadataPublication.Write(root, "quota-index.json", publication.Bytes, IndexMaximumBytes,
            durableDirectoryEntry: true, unixDirectoryFsync: unixDirectoryFsyncForTests);
    }

    // Keep the validated bytes tied to their peak-space check. Reprepare after any mutation under the index lease.
    private static IndexPublication PrepareIndexPublication(CoverageRunOutputLease root, EvidenceReceiptQuotaIndex index)
    {
        ValidateEntries(index);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(index);
        var fits = index.Entries.Count <= MaximumEntries && bytes.LongLength <= IndexMaximumBytes
            && IndexPublicationFits(UsedEntryBytes(index), ExistingIndexBytes(root), bytes.LongLength,
                ExistingIndexStagingBytes(root));
        return new(bytes, fits);
    }

    private readonly record struct IndexPublication(byte[] Bytes, bool Fits);

    /// <summary>Checks peak quota while the previous index and replacement staging file coexist.</summary>
    internal static bool IndexPublicationFits(long indexedBytes, long previousIndexBytes, long replacementIndexBytes,
        long abandonedStagingBytes = 0)
    {
        if (indexedBytes < 0 || previousIndexBytes < 0 || replacementIndexBytes < 0 || abandonedStagingBytes < 0) return false;
        var peakStagingBytes = Math.Max(replacementIndexBytes, abandonedStagingBytes);
        return indexedBytes <= MaximumBytes - previousIndexBytes
            && indexedBytes + previousIndexBytes <= MaximumBytes - peakStagingBytes;
    }

    private static long ExistingIndexBytes(CoverageRunOutputLease root)
    {
        try
        {
            using var stream = root.OpenArtifactFile("quota-index.json", false);
            return stream.Length;
        }
        catch (FileNotFoundException) { return 0; }
    }

    private static long ExistingIndexStagingBytes(CoverageRunOutputLease root)
    {
        try
        {
            using var stream = root.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, false);
            return stream.Length;
        }
        catch (FileNotFoundException) { return 0; }
    }

    private static long UsedEntryBytes(EvidenceReceiptQuotaIndex index)
    {
        long total = 0;
        foreach (var entry in index.Entries)
            total = checked(total + entry.MetadataBytes + entry.PayloadBytes + entry.ReservedPayloadBytes
                + entry.RetiredPayloads.Sum(static payload => payload.Bytes));
        return total;
    }

    private static long UsedBytes(EvidenceReceiptQuotaIndex index, CoverageRunOutputLease root)
        => checked(UsedEntryBytes(index) + ExistingIndexBytes(root));

    private static EvidenceReceiptQuotaEntry? Find(EvidenceReceiptQuotaIndex index, string repositoryNamespace, string key)
        => index.Entries.SingleOrDefault(entry => entry.RepositoryNamespace == repositoryNamespace && entry.Key == key);

    private static EvidenceReceiptQuotaEntry? FindForMutation(EvidenceReceiptQuotaIndex index,
        string repositoryNamespace, string key)
    {
        var entry = Find(index, repositoryNamespace, key);
        if (entry?.EvictionPending == true)
            throw new EvidenceReceiptQuotaUnavailableException("receipt-key-eviction-pending");
        return entry;
    }

    private static string EntryIdentity(string repositoryNamespace, string key) => repositoryNamespace + ":" + key;

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsValidPayloadReservation(EvidenceReceiptPayloadReservation reservation)
        => reservation is not null
            && reservation.ReservedBytes >= 0
            && EvidenceReceiptPayloadNames.MatchesReservation(reservation.ReservationId,
                reservation.StageDirectoryName, reservation.PayloadDirectoryName);

    private static void Replace(EvidenceReceiptQuotaIndex index, EvidenceReceiptQuotaEntry oldEntry, EvidenceReceiptQuotaEntry newEntry)
    {
        var position = index.Entries.IndexOf(oldEntry);
        if (position < 0) throw new InvalidOperationException("Receipt quota index entry disappeared.");
        index.Entries[position] = newEntry;
    }
}

/// <summary>Bounded index for private receipt metadata, immutable payloads, and in-progress reservations.</summary>
internal sealed record EvidenceReceiptQuotaIndex(int SchemaVersion, List<EvidenceReceiptQuotaEntry> Entries);

/// <summary>
/// One owned receipt key's metadata and byte reservations. <c>PayloadReservations</c> holds bounded bindings to the
/// only stage and final names eligible for reconciliation. A missing or null value from an older serialized index
/// means its aggregate reservation is legacy and unknown; maintenance must not infer or release those bytes.
/// </summary>
internal sealed record EvidenceReceiptQuotaEntry(string RepositoryNamespace, string Key, long MetadataBytes,
    long PayloadBytes, long ReservedPayloadBytes, long EssentialReservationBytes, bool EssentialReserved,
    bool HasInFlight, bool HasAuditIntent, string? PayloadDirectoryName, DateTimeOffset? OriginCompletedAt,
    DateTimeOffset? LastSuccessfulUse, bool Quarantined, EvidenceReceiptState? AuthorityState = null,
    IReadOnlyList<EvidenceReceiptRetiredPayload> RetiredPayloads = null!, string? ProducerId = null,
    string? InputIdentityDigest = null, IReadOnlyList<string>? EssentialReservationIds = null,
    IReadOnlyList<EvidenceReceiptPayloadReservation>? PayloadReservations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool EvictionPending = false);

internal sealed record EvidenceReceiptRetiredPayload(string DirectoryName, long Bytes);

/// <summary>A bounded reservation identity paired with the only stage and final payload names it may account for.</summary>
/// <param name="ReservationId">Fresh 32-character hexadecimal identifier retained through exact cleanup.</param>
/// <param name="StageDirectoryName">The sole generated staging directory name eligible for reconciliation.</param>
/// <param name="PayloadDirectoryName">The sole generated final directory name eligible for reconciliation and commit matching.</param>
/// <param name="ReservedBytes">Remaining conservative byte charge; reconciliation may reduce it to measured size.</param>
internal sealed record EvidenceReceiptPayloadReservation(string ReservationId, string StageDirectoryName,
    string PayloadDirectoryName, long ReservedBytes)
{
    /// <summary>Derives the stage and final names from the chosen reservation ID.</summary>
    /// <param name="reservationId">Identifier used for both generated directory names and reconciliation.</param>
    /// <param name="reservedBytes">Conservative byte estimate for staging the receipt payload.</param>
    /// <returns>The complete binding to submit to quota admission and retain through cleanup.</returns>
    /// <remarks>Quota admission validates the ID and charge before allowing either directory to be created.</remarks>
    internal static EvidenceReceiptPayloadReservation Create(string reservationId, long reservedBytes)
        => new(reservationId, EvidenceReceiptPayloadNames.Stage(reservationId),
            EvidenceReceiptPayloadNames.Final(reservationId), reservedBytes);
}

internal sealed record EvidenceReceiptBatchCommit(string RepositoryNamespace, string Key, string ProducerId, string ExecutionId,
    EvidenceReceiptState State, string PayloadDirectoryName, DateTimeOffset CompletedAt,
    long PayloadBytes, long ReservationBytes, string EssentialReservationId);

/// <summary>Signals that safe essential receipt authority could not be reserved.</summary>
internal sealed class EvidenceReceiptQuotaUnavailableException(string message) : IOException(message);
