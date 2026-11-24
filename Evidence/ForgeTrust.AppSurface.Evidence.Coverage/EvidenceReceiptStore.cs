using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Configures the private, local producer receipt store.</summary>
/// <remarks>
/// The root precedence is explicit <see cref="RootDirectory"/>, then
/// <c>APPSURFACE_EVIDENCE_RECEIPT_STORE</c>, then the user-local application-data default.
/// Opening a store for explanation is read-only and never creates directories, locks, or index state.
/// A repository namespace is an opaque caller-supplied 64-character SHA-256 hex identity.
/// </remarks>
internal sealed record EvidenceReceiptStoreOptions
{
    /// <summary>Gets an optional explicit store root.</summary>
    internal string? RootDirectory { get; init; }

    /// <summary>Gets the isolated repository namespace as 64 hexadecimal characters.</summary>
    internal required string RepositoryNamespace { get; init; }

    /// <summary>Gets roots the store must not overlap, such as declared inputs and the run artifact output.</summary>
    internal IReadOnlyList<string> ForbiddenRoots { get; init; } = [];

    /// <summary>Gets the total native lease wait budget. The default is thirty minutes.</summary>
    internal TimeSpan WaitTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets the clock used for origin age and last-successful-use decisions.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>Owns private per-producer receipt state and native key leases.</summary>
/// <remarks>
/// Keys and namespaces are opaque SHA-256 hex strings. Acquire key leases before admitting producer
/// or resource side effects. A native lease release does not clear durable InFlight or AuditIntent
/// authority; only the matching execution owner may settle it. This store is separate from coverage
/// archive formats and never stores raw identity components, commands, or environment values.
/// </remarks>
internal sealed class EvidenceReceiptStore
{
    private const int SchemaVersion = 1;

    private readonly string _root;
    private readonly string _namespace;
    private readonly bool _readOnly;
    private readonly TimeSpan _waitTimeout;
    private readonly string[] _forbiddenRoots;
    private readonly TimeProvider _clock;

    private EvidenceReceiptStore(string root, string repositoryNamespace, bool readOnly, TimeSpan waitTimeout,
        string[] forbiddenRoots, TimeProvider clock)
    {
        (_root, _namespace, _readOnly, _waitTimeout, _forbiddenRoots) =
                (root, repositoryNamespace.ToLowerInvariant(), readOnly, waitTimeout, forbiddenRoots);
        _clock = clock;
    }

    /// <summary>Resolves a store without creating or modifying any filesystem state.</summary>
    /// <param name="options">Root and repository namespace configuration.</param>
    /// <returns>A read-only view suitable for explain and inspection.</returns>
    internal static EvidenceReceiptStore OpenReadOnly(EvidenceReceiptStoreOptions options)
        => Open(options, readOnly: true);

    /// <summary>Opens a writable store; actual creation is deferred until key acquisition.</summary>
    /// <param name="options">Root and repository namespace configuration.</param>
    /// <returns>A store ready to acquire exclusive per-key coordination.</returns>
    internal static EvidenceReceiptStore Open(EvidenceReceiptStoreOptions options)
        => Open(options, readOnly: false);

    private static EvidenceReceiptStore Open(EvidenceReceiptStoreOptions options, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateHash(options.RepositoryNamespace, nameof(options.RepositoryNamespace));
        if (options.WaitTimeout <= TimeSpan.Zero || options.WaitTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "Receipt wait timeout must be finite and positive.");
        ArgumentNullException.ThrowIfNull(options.Clock);
        var root = ResolveRoot(options.RootDirectory,
            Environment.GetEnvironmentVariable("APPSURFACE_EVIDENCE_RECEIPT_STORE"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var forbiddenRoots = options.ForbiddenRoots.Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator).Distinct(PathComparer).ToArray();
        if (forbiddenRoots.Any(path => PathsOverlap(fullRoot, path)))
            throw new InvalidOperationException("Receipt store root overlaps a declared input or output root.");
        return new EvidenceReceiptStore(fullRoot, options.RepositoryNamespace, readOnly, options.WaitTimeout, forbiddenRoots, options.Clock);
    }

    /// <summary>Resolves explicit, environment, and user-local roots without allowing an empty base to become relative.</summary>
    /// <param name="explicitRoot">An explicit override; when supplied it must contain a non-whitespace path.</param>
    /// <param name="environmentRoot">An optional environment override; empty values are treated as unset.</param>
    /// <param name="localApplicationData">The platform user-local application-data directory.</param>
    /// <returns>The selected root before canonicalization.</returns>
    internal static string ResolveRoot(string? explicitRoot, string? environmentRoot, string? localApplicationData)
    {
        if (explicitRoot is not null)
        {
            if (string.IsNullOrWhiteSpace(explicitRoot))
                throw new ArgumentException("An explicit receipt root cannot be empty or whitespace.", nameof(explicitRoot));
            return explicitRoot;
        }
        if (!string.IsNullOrWhiteSpace(environmentRoot)) return environmentRoot;
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new InvalidOperationException("User-local application data is unavailable.");
        return Path.Combine(localApplicationData, "ForgeTrust", "AppSurface", "evidence-receipts", "v1");
    }

    /// <summary>Acquires all requested producer keys in ordinal order under one cumulative deadline.</summary>
    /// <param name="keys">Unique producer content keys, each encoded as SHA-256 hex.</param>
    /// <param name="cancellationToken">Cancels waiting and unwinds partial acquisitions.</param>
    /// <param name="progress">Optional bounded wait progress in key, elapsed, and remaining terms.</param>
    /// <returns>Owned key leases in canonical order; each lease remains held until disposed.</returns>
    /// <remarks>
    /// Acquisition retries the existing native fail-fast coverage operation lease with bounded backoff.
    /// The deadline covers every key, so a long wait on one key cannot multiply across a multi-key run.
    /// </remarks>
    internal async Task<IReadOnlyList<EvidenceReceiptKeyLease>> AcquireAsync(IEnumerable<string> keys,
        CancellationToken cancellationToken, Action<string, TimeSpan, TimeSpan>? progress = null)
        => await AcquireAsync(keys, _waitTimeout, cancellationToken, progress).ConfigureAwait(false);

    /// <summary>Acquires keys using a caller-owned remaining invocation budget.</summary>
    /// <remarks>Pass the same monotonic invocation budget across reprepare attempts; this method never replenishes it.</remarks>
    internal async Task<IReadOnlyList<EvidenceReceiptKeyLease>> AcquireAsync(IEnumerable<string> keys,
        TimeSpan remainingWaitBudget, CancellationToken cancellationToken, Action<string, TimeSpan, TimeSpan>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_readOnly, this);
        ArgumentNullException.ThrowIfNull(keys);
        if (remainingWaitBudget <= TimeSpan.Zero || remainingWaitBudget == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(remainingWaitBudget), "Receipt wait budget must be finite and positive.");
        var waitBudget = remainingWaitBudget < _waitTimeout ? remainingWaitBudget : _waitTimeout;
        var ordered = keys.Select(key => { ValidateHash(key, nameof(keys)); return key.ToLowerInvariant(); })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) return Array.Empty<EvidenceReceiptKeyLease>();
        var acquired = new List<EvidenceReceiptKeyLease>(ordered.Length);
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? essentialReservationId = null;
        try
        {
            EnsureStoreRoot();
            essentialReservationId = EvidenceReceiptQuota.ReserveEssential(_root, _namespace, ordered, _clock.GetUtcNow(),
                RemainingWait(), cancellationToken);
            var namespaceDirectory = EnsureChild(_root, _namespace);
            foreach (var key in ordered)
            {
                var directory = EnsureChild(namespaceDirectory, key);
                var delay = TimeSpan.FromMilliseconds(25);
                var nextProgress = TimeSpan.FromSeconds(5);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var nativeLease = CoverageArtifactOperationLease.Acquire(directory, exclusive: true);
                        var keyLease = new EvidenceReceiptKeyLease(key, directory, _root, _namespace,
                            nativeLease, _clock, essentialReservationId);
                        try
                        {
                            EvidenceReceiptQuota.ConfirmEssentialReservation(_root, _namespace, key,
                                essentialReservationId, _clock.GetUtcNow(), RemainingWait(), cancellationToken);
                            EvidenceReceiptQuota.ReconcilePayloadReservations(keyLease, RemainingWait(), cancellationToken);
                            acquired.Add(keyLease);
                        }
                        catch
                        {
                            keyLease.DisposeAfterFailedAcquisition();
                            throw;
                        }
                        break;
                    }
                    catch (Exception exception) when (IsBusy(exception))
                    {
                        var remaining = waitBudget - started.Elapsed;
                        if (remaining <= TimeSpan.Zero) throw new TimeoutException($"Receipt key {key[..12]} remained busy until the cumulative wait deadline.", exception);
                        if (started.Elapsed >= nextProgress)
                        {
                            progress?.Invoke(key[..12], started.Elapsed, remaining);
                            nextProgress = started.Elapsed + TimeSpan.FromSeconds(5);
                        }
                        await Task.Delay(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
                        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
                    }
                }
            }
            return acquired;
        }
        catch
        {
            foreach (var lease in acquired.AsEnumerable().Reverse()) lease.DisposeAfterFailedAcquisition();
            if (essentialReservationId is not null)
                foreach (var key in ordered)
                    TryReleaseReservationWithFreeKey(_root, _namespace, key, essentialReservationId);
            throw;
        }

        TimeSpan RemainingWait()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = waitBudget - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Receipt acquisition exceeded the cumulative invocation wait deadline.");
            return remaining;
        }
    }

    private static void TryReleaseReservationWithFreeKey(string root, string repositoryNamespace, string key, string reservationId)
    {
        var keyDirectory = Path.Combine(root, repositoryNamespace, key);
        try
        {
            using var keyLease = CoverageArtifactOperationLease.Acquire(keyDirectory, exclusive: true);
            EvidenceReceiptQuota.ReleaseEssentialReservation(root, repositoryNamespace, key, reservationId, TimeSpan.Zero);
        }
        catch (Exception exception) when (IsBusy(exception) || exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException
            or NotSupportedException)
        {
            // A busy or unsafe key keeps its reservation for later lease-verified maintenance.
        }
    }

    /// <summary>Stages all eligible producer payloads, then atomically admits their authority rows together.</summary>
    /// <remarks>Any audit target makes the whole requested batch all-or-none; ordinary quota refusals may be excluded.</remarks>
    internal async Task<IReadOnlyDictionary<string, EvidenceReceiptPublicationResult>> PublishInvocationAsync(
        string executionId, EvidencePlan originPlan, EvidenceManifest originManifest,
        IReadOnlyList<EvidenceReceiptPublicationRequest> requests, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_readOnly, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(originPlan);
        ArgumentNullException.ThrowIfNull(originManifest);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return new Dictionary<string, EvidenceReceiptPublicationResult>(StringComparer.Ordinal);
        if (requests.Any(request => request.Lease.StoreRoot != _root || request.Lease.RepositoryNamespace != _namespace))
            throw new InvalidOperationException("Every publication lease must belong to this receipt store domain.");
        if (requests.Select(static request => request.Lease.Key).Distinct(StringComparer.Ordinal).Count() != requests.Count
            || requests.Select(static request => request.ProducerId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new ArgumentException("Invocation publication requests must have unique producer IDs and keys.", nameof(requests));
        return await EvidenceReceiptKeyLease.PublishInvocationCoreAsync(_root, _namespace, executionId,
            originPlan, originManifest, requests, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureStoreRoot()
    {
        foreach (var forbidden in _forbiddenRoots)
            if (PathsOverlap(_root, forbidden)) throw new InvalidOperationException("Receipt store root overlaps a declared input or output root.");
        // Unix persists each retained ancestor entry as well as the later index rename. Windows uses the
        // NTFS write-through rename in quota publication; the separate path-sync primitive is Unix-only.
        using var existing = CoverageRunOutputLease.AcquireArtifactDirectory(_root, createMissing: false, requirePrivate: true,
            durableDirectoryPath: !OperatingSystem.IsWindows());
        if (existing is not null) return;
        using var created = CoverageRunOutputLease.AcquireArtifactDirectory(_root, createMissing: true, requirePrivate: true,
            durableDirectoryPath: !OperatingSystem.IsWindows())
            ?? throw new IOException("Receipt store root is unavailable.");
    }

    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;
    private static StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;
    private static bool PathsOverlap(string left, string right) => IsWithin(left, right) || IsWithin(right, left);
    private static bool IsWithin(string parent, string candidate)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        if (string.Equals(normalizedParent, normalizedCandidate, PathComparison)) return true;
        var parentPrefix = Path.EndsInDirectorySeparator(normalizedParent)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(parentPrefix, PathComparison);
    }

    private static string EnsureChild(string parentPath, string name)
    {
        var path = Path.Combine(parentPath, name);
        using var child = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true)
            ?? throw new IOException("Receipt directory is unavailable.");
        return path;
    }

    private static bool IsBusy(Exception exception) => exception.Message.Contains("ASCOV202", StringComparison.Ordinal);

    /// <summary>Inspects a receipt without creating files, updating LRU, or restoring artifacts.</summary>
    /// <remarks>Reads an authoritative snapshot under the short root-index lease. Execution still requires the per-key lease.</remarks>
    internal EvidenceReceiptReadResult Peek(string key)
    {
        ValidateHash(key, nameof(key));
        var normalizedKey = key.ToLowerInvariant();
        try
        {
            var authority = EvidenceReceiptQuota.ReadAuthority(_root, _namespace, normalizedKey, readOnly: _readOnly);
            var proposed = ClassifyAuthority(authority, _clock);
            if (!proposed.IsCandidate) return proposed with { State = authority };
            var state = proposed.State!;
            using var payload = CoverageRunOutputLease.AcquireArtifactDirectory(
                Path.Combine(_root, _namespace, normalizedKey, state.PayloadDirectoryName!), false, true);
            if (payload is null) return EvidenceReceiptReadResult.Miss("payload-missing", state);
            try
            {
                var origin = EvidenceReceiptPayload.ReadOriginMetadata(payload, state);
                return proposed with
                {
                    Result = origin.Result,
                    OriginPlan = origin.Plan,
                    OriginManifest = origin.Manifest,
                    InputIdentity = origin.InputIdentity,
                    OriginCompletedAt = state.OriginCompletedAt,
                    OriginElapsedMilliseconds = origin.Result.ElapsedMilliseconds
                };
            }
            catch (EvidenceReceiptPayload.ReceiptMetadataException exception)
            {
                var reason = exception.Reason switch
                {
                    "input-identity-digest-invalid" => "input-identity-invalid",
                    "passed-producer-invalid" => "origin-binding-invalid",
                    _ => exception.Reason
                };
                return EvidenceReceiptReadResult.Miss(reason, state);
            }
        }
        catch (FormatException) { return EvidenceReceiptReadResult.Miss("passed-producer-record-invalid"); }
        catch (FileNotFoundException) { return EvidenceReceiptReadResult.Miss("missing"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or NotSupportedException)
        { return EvidenceReceiptReadResult.Unavailable("foreign-or-unsupported-state-preserved"); }
    }

    /// <summary>Classifies an authority snapshot before any receipt payload is opened.</summary>
    /// <remarks>Ownership and quarantine take precedence over age and payload eligibility.
    /// The clock is read only when age is relevant. An age refusal retains the snapshot.</remarks>
    private static EvidenceReceiptReadResult ClassifyAuthority(EvidenceReceiptState? state, TimeProvider clock)
    {
        if (state is null) return EvidenceReceiptReadResult.Miss("missing");
        if (state.InFlightExecutionId is not null) return EvidenceReceiptReadResult.Unavailable("unresolved-execution");
        if (state.AuditIntentExecutionId is not null || state.AuditMismatch) return EvidenceReceiptReadResult.Miss("audit-quarantined");
        if (state.OriginCompletedAt is { } completedAt
            && EvidenceReceiptRetention.GetAgeMissReason(completedAt, clock.GetUtcNow()) is { } ageMissReason)
            return EvidenceReceiptReadResult.Miss(ageMissReason, state);
        if (state.PayloadDirectoryName is null || state.PassedProducerRecord is null || state.OriginManifestDigest is null)
            return EvidenceReceiptReadResult.Miss("no-passed-receipt");
        return EvidenceReceiptReadResult.Candidate(state);
    }

    /// <summary>Finds the latest verified same-producer origin for a bounded read-only difference explanation.</summary>
    /// <param name="producerId">The stable producer ID from the current plan.</param>
    /// <param name="currentIdentity">The current hash-only identity computed before execution.</param>
    /// <returns>A bounded reason and, when file inputs differ, the first differing repository-relative path.</returns>
    internal EvidenceReceiptComparableResult FindComparable(string producerId, EvidenceProducerIdentity currentIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerId);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        ValidateHash(currentIdentity.Key, nameof(currentIdentity));
        if (!StringComparer.Ordinal.Equals(currentIdentity.RepositoryNamespace, _namespace))
            return EvidenceReceiptComparableResult.Unavailable("repository-namespace-mismatch");
        try
        {
            var (current, _) = EvidenceReceiptInputIdentity.Create(currentIdentity);
            var candidates = EvidenceReceiptQuota.ReadComparable(_root, _namespace, producerId, _clock.GetUtcNow(),
                readOnly: _readOnly);
            foreach (var candidate in candidates)
            {
                var verified = Peek(candidate.Key);
                if (!verified.IsCandidate || verified.Result?.ProducerId != producerId
                    || verified.InputIdentity is not { } identity || verified.OriginCompletedAt is not { } verifiedCompletedAt)
                    continue;
                var differingPath = EvidenceProducerIdentityBuilder.FirstDifferingPath(
                    current.Files.Select(static file => new EvidenceProducerInputFile(file.RelativePath, file.Mode, file.LengthBytes, file.Sha256)).ToArray(),
                    identity.Files.Select(static file => new EvidenceProducerInputFile(
                        file.RelativePath, file.Mode, file.LengthBytes, file.Sha256)).ToArray());
                if (differingPath is not null)
                    return EvidenceReceiptComparableResult.Candidate("input-files-differ", differingPath, null, verifiedCompletedAt);
                var component = currentIdentity.ComponentHashes.Keys.Union(identity.ComponentHashes.Keys, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).FirstOrDefault(name =>
                        currentIdentity.ComponentHashes.GetValueOrDefault(name) != identity.ComponentHashes.GetValueOrDefault(name));
                return component is null
                    ? EvidenceReceiptComparableResult.Candidate("identity-matches", null, null, verifiedCompletedAt)
                    : EvidenceReceiptComparableResult.Candidate("identity-component-differs", null, component, verifiedCompletedAt);
            }
            return EvidenceReceiptComparableResult.Miss("no-comparable-receipt");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return EvidenceReceiptComparableResult.Unavailable("receipt-index-unavailable"); }
    }

    private static void ValidateHash(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected exactly 64 hexadecimal characters.", parameter);
    }

    /// <summary>Owns one key directory and its native operation lease.</summary>
    internal sealed class EvidenceReceiptKeyLease : IDisposable
    {
        private readonly string directory;
        private readonly string storeRoot;
        private readonly string repositoryNamespace;
        private readonly CoverageArtifactOperationLease nativeLease;
        private readonly TimeProvider clock;
        private readonly string essentialReservationId;
        private bool _disposed;

        internal EvidenceReceiptKeyLease(string key, string directory, string storeRoot, string repositoryNamespace,
            CoverageArtifactOperationLease nativeLease, TimeProvider clock, string essentialReservationId)
        {
            Key = key;
            this.directory = directory;
            this.storeRoot = storeRoot;
            this.repositoryNamespace = repositoryNamespace;
            this.nativeLease = nativeLease;
            this.clock = clock;
            this.essentialReservationId = essentialReservationId;
        }
        /// <summary>Gets the canonical lowercase content key.</summary>
        internal string Key { get; }
        internal string StoreRoot => storeRoot;
        internal string RepositoryNamespace => repositoryNamespace;
        internal string DirectoryPath => directory;
        internal string EssentialReservationId => essentialReservationId;

        /// <summary>Reads bounded authoritative state while the native key lease is held.</summary>
        internal EvidenceReceiptState? ReadState()
        {
            Verify();
            return EvidenceReceiptQuota.ReadAuthority(storeRoot, repositoryNamespace, Key);
        }

        /// <summary>Durably establishes execution or audit authority before any producer side effect.</summary>
        /// <param name="executionId">Fresh opaque execution identifier.</param>
        /// <param name="auditIntent">Whether this is an explicit fresh reuse audit.</param>
        /// <param name="allowQuarantinedFresh">Admits fresh ordinary work while preserving quarantine; publication remains refused.</param>
        /// <param name="expectedAssertions">Expected audit outcome captured before execution and retained through recovery.</param>
        internal void EstablishAuthority(string executionId, bool auditIntent = false, bool allowQuarantinedFresh = false,
            string? expectedAssertions = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            var state = ReadState() ?? new EvidenceReceiptState(SchemaVersion, Key, null, null, null, null, null);
            if (state.InFlightExecutionId is not null)
                throw new InvalidOperationException("An unresolved execution authority already blocks this key.");
            if (!EvidenceReceiptState.AreAuditAssertionsWithinLimit(expectedAssertions))
                throw new InvalidDataException("Expected audit assertions exceed their bound.");
            var quarantined = state.AuditIntentExecutionId is not null || state.AuditMismatch;
            if (quarantined && !auditIntent && !allowQuarantinedFresh)
                throw new InvalidOperationException("A pending audit intent blocks ordinary execution.");
            WriteState(state with
            {
                InFlightExecutionId = executionId,
                AuditIntentExecutionId = auditIntent ? executionId : state.AuditIntentExecutionId,
                AuditExpectedAssertions = auditIntent
                    ? quarantined ? state.AuditExpectedAssertions : expectedAssertions
                    : state.AuditExpectedAssertions
            });
        }

        /// <summary>Clears only matching settled execution authority, retaining any audit refusal.</summary>
        internal void SettleExecution(string executionId)
        {
            var state = ReadState() ?? throw new InvalidOperationException("No execution authority exists.");
            if (!StringComparer.Ordinal.Equals(state.InFlightExecutionId, executionId))
                throw new InvalidOperationException("Execution authority belongs to another execution.");
            WriteState(state with { InFlightExecutionId = null });
        }

        /// <summary>Durably quarantines this key after a receipt audit mismatch.</summary>
        /// <remarks>The intent remains authoritative even if later audit work or publication fails.</remarks>
        internal void MarkAuditMismatch(string executionId, string expectedAssertions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(expectedAssertions);
            if (!EvidenceReceiptState.AreAuditAssertionsWithinLimit(expectedAssertions))
                throw new InvalidDataException("Expected audit assertions exceed their bound.");
            var state = ReadState() ?? throw new InvalidOperationException("Audit intent must be established before recording a mismatch.");
            if (!StringComparer.Ordinal.Equals(state.AuditIntentExecutionId, executionId))
                throw new InvalidOperationException("Audit intent belongs to another execution.");
            WriteState(state with { AuditExpectedAssertions = state.AuditExpectedAssertions ?? expectedAssertions, AuditMismatch = true });
        }

        /// <summary>Returns the currently admissible receipt candidate without changing LRU metadata.</summary>
        /// <remarks>Pending intent, mismatch quarantine, or unresolved execution authority always denies reuse.</remarks>
        internal EvidenceReceiptReadResult ProposeReadReceipt() => ClassifyAuthority(ReadState(), clock);

        /// <summary>Verifies a complete origin receipt and restores its artifacts through the bounded writer.</summary>
        /// <param name="producer">Current declaration that must exactly match the origin plan.</param>
        /// <param name="writer">Fresh current-run writer that owns restored artifact paths.</param>
        /// <param name="cancellationToken">Cancels verification and restoration.</param>
        /// <returns>A restored result, an ordinary miss for owned invalid payload, or unavailable for unknown authority.</returns>
        /// <remarks>Validation and restore handles close before invalid payload retirement, including on Windows.</remarks>
        internal async Task<EvidenceReceiptReadResult> ReadReceiptAsync(EvidenceProducerDeclaration producer,
            EvidenceArtifactWriter writer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(producer);
            ArgumentNullException.ThrowIfNull(writer);
            Verify();
            EvidenceReceiptReadResult proposed;
            try { proposed = ProposeReadReceipt(); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                return EvidenceReceiptReadResult.Unavailable("foreign-or-unsupported-state-preserved");
            }
            if (!proposed.IsCandidate) return proposed;
            var state = proposed.State!;
            try
            {
                using (var payload = EvidenceReceiptPayload.Open(directory, state.PayloadDirectoryName!))
                {
                    var (origin, completedAt) = await EvidenceReceiptPayload.VerifyForRestoreAsync(payload, state, producer,
                        cancellationToken).ConfigureAwait(false);
                    var originResult = origin.Result;
                    try
                    {
                        using var artifactRoot = EvidenceReceiptPayload.OpenArtifacts(payload);
                        if (!await EvidenceReceiptPayload.RestoreArtifactsAsync(artifactRoot, originResult.Artifacts ?? [],
                                writer, cancellationToken).ConfigureAwait(false))
                            return EvidenceReceiptReadResult.Miss("restore-artifact-verification-failed", state);

                        RecordSuccessfulUse(clock.GetUtcNow());
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                        or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException
                        or CoverageEvidenceInventoryException)
                    {
                        return EvidenceReceiptReadResult.Miss("restore-or-lru-unavailable", state);
                    }
                    var reused = originResult with
                    {
                        Outcome = EvidenceProducerOutcome.Reused,
                        ElapsedMilliseconds = 0,
                        ProducerKey = Key,
                        OriginManifestDigest = origin.Manifest.ManifestDigest,
                        OriginCompletedAt = completedAt,
                        Diagnostic = producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase)
                            ? "Coverage report: coverage/coverage-gate.md (relative to the producer artifact root)."
                            : originResult.Diagnostic
                    };
                    return EvidenceReceiptReadResult.Restored(reused, origin.Plan, origin.Manifest,
                        completedAt, origin.InputIdentity, originResult.ElapsedMilliseconds);
                }
            }
            catch (EvidenceReceiptPayload.ReceiptMetadataException exception)
            {
                return InvalidateOwned(state, exception.Reason);
            }
            catch (EvidenceReceiptPayload.InvalidOwnedReceiptException exception)
            {
                // All validation/restore handles have unwound before native payload deletion.
                return InvalidateOwned(state, exception.Reason);
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is FileNotFoundException
                || exception is DirectoryNotFoundException || exception is JsonException || exception is FormatException
                || exception is ArgumentException || exception is InvalidOperationException || exception is KeyNotFoundException
                || exception is CoverageEvidenceInventoryException)
            {
                return InvalidateOwned(state, "owned-payload-invalid");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return EvidenceReceiptReadResult.Unavailable("receipt-payload-io-unavailable", state);
            }
        }

        /// <summary>Compatibility entry point for one-key invocation publication.</summary>
        internal async Task<EvidenceReceiptPublicationResult> PublishAsync(string executionId, EvidencePlan originPlan,
            EvidenceManifest originManifest, string producerId, string sourceArtifactRoot, DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            var result = await PublishInvocationCoreAsync(storeRoot, repositoryNamespace, executionId, originPlan, originManifest,
                [new(this, producerId, sourceArtifactRoot, completedAt)], cancellationToken).ConfigureAwait(false);
            return result[producerId];
        }

        /// <summary>Stages and commits reusable receipt payloads as one invocation batch.</summary>
        /// <param name="storeRoot">Private receipt-store root containing the quota index.</param>
        /// <param name="repositoryNamespace">Namespace owning every requested key lease.</param>
        /// <param name="executionId">Execution that currently owns each requested authority row.</param>
        /// <param name="originPlan">Verified origin plan persisted with each staged payload.</param>
        /// <param name="originManifest">Completed passing manifest persisted with each staged payload.</param>
        /// <param name="requests">Distinct producer and key publication requests.</param>
        /// <param name="cancellationToken">Cancels source verification and staging before authority commit.</param>
        /// <param name="afterAuthorityCommitForTests">Optional per-call fault seam invoked immediately after the quota authority commit returns. If it throws, the result is refused while final payloads and their quota accounting remain intact because the committed authority may be visible.</param>
        /// <param name="beforeArtifactCopyForTests">Optional per-call staging fault seam. Invoked for each artifact after
        /// complete source preflight and metadata staging, immediately before copying that artifact. Tests can use it
        /// to cancel or mutate their owned fixture; normal copy verification and reservation cleanup still apply.
        /// Production callers omit this callback. It does not run for artifacts refused during preflight.
        /// Any exception escaping staging attempts cleanup of all earlier staged requests before propagation;
        /// unconfirmed cleanup retains its quota reservation.</param>
        /// <param name="beforeAuthorityCommitForTests">Optional per-call fault seam invoked after staging and before the atomic authority commit. Production callers omit this callback.</param>
        /// <returns>A publication result for each requested producer.</returns>
        internal static async Task<IReadOnlyDictionary<string, EvidenceReceiptPublicationResult>> PublishInvocationCoreAsync(
            string storeRoot, string repositoryNamespace, string executionId, EvidencePlan originPlan,
            EvidenceManifest originManifest, IReadOnlyList<EvidenceReceiptPublicationRequest> requests,
            CancellationToken cancellationToken, Action? afterAuthorityCommitForTests = null,
            Action<EvidenceReceiptPublicationRequest, EvidenceArtifactResult>? beforeArtifactCopyForTests = null,
            Action? beforeAuthorityCommitForTests = null)
        {
            var outputs = new Dictionary<string, EvidenceReceiptPublicationResult>(StringComparer.Ordinal);
            var auditBatch = requests.Any(request => request.Lease.ReadState()?.AuditIntentExecutionId == executionId);
            var staged = new List<StagedReceiptPublication>();
            try
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = await request.Lease.StagePublicationAsync(executionId, originPlan, originManifest, request,
                        cancellationToken, beforeArtifactCopyForTests).ConfigureAwait(false);
                    if (outcome.Stage is { } stage)
                    {
                        staged.Add(stage);
                        continue;
                    }
                    outputs[request.ProducerId] = outcome.Result!;
                    if (auditBatch)
                    {
                        await CleanupStagedAsync(staged).ConfigureAwait(false);
                        foreach (var item in requests)
                            outputs[item.ProducerId] = EvidenceReceiptPublicationResult.Refused("audit-batch-aborted:" + outcome.Result!.Reason);
                        return outputs;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await CleanupStagedAsync(staged).ConfigureAwait(false);
                throw;
            }

            if (staged.Count == 0) return outputs;
            try
            {
                var commits = staged.Select(stage => new EvidenceReceiptBatchCommit(repositoryNamespace, stage.Lease.Key,
                    stage.ProducerId, executionId, stage.NextState, stage.PayloadName, stage.CompletedAt,
                    stage.PayloadBytes, stage.ReservationBytes, stage.Lease.EssentialReservationId)).ToArray();
                beforeAuthorityCommitForTests?.Invoke();
                EvidenceReceiptQuota.CommitInvocation(storeRoot, commits);
                afterAuthorityCommitForTests?.Invoke();
                foreach (var stage in staged)
                {
                    outputs[stage.ProducerId] = EvidenceReceiptPublicationResult.PublishedResult(stage.PayloadName);
                    try
                    {
                        foreach (var retiredPayload in EvidenceReceiptQuota.ReadRetiredPayloadNames(storeRoot,
                                     repositoryNamespace, stage.Lease.Key))
                        {
                            var deleted = EvidenceReceiptKeyLease.TryDeleteOwnedPayload(stage.Lease.DirectoryPath, retiredPayload, recoverCommittedStage: true);
                            EvidenceReceiptQuota.ReleaseRetiredPayload(storeRoot, repositoryNamespace,
                                stage.Lease.Key, retiredPayload, deleted);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                        or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
                    {
                        // Keep unconfirmed retired bytes charged; later maintenance can safely retry under a free key lease.
                    }
                }
                return outputs;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or CoverageEvidenceInventoryException)
            {
                if (IsConfirmedNotCommitted(staged))
                    await CleanupStagedAsync(staged).ConfigureAwait(false);
                // A failed or unreadable authority snapshot is indeterminate. Preserve every final payload and
                // reservation because the atomic index replacement may already be visible.
                foreach (var item in staged)
                    outputs[item.ProducerId] = EvidenceReceiptPublicationResult.Refused("invocation-batch-commit-failed");
                return outputs;
            }
        }

        private static bool IsConfirmedNotCommitted(IReadOnlyList<StagedReceiptPublication> staged)
        {
            try
            {
                return staged.All(item => item.Lease.ReadState() == item.PreviousState);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
            {
                return false;
            }
        }

        private async Task<StagePublicationOutcome> StagePublicationAsync(string executionId, EvidencePlan originPlan,
            EvidenceManifest originManifest, EvidenceReceiptPublicationRequest request, CancellationToken cancellationToken,
            Action<EvidenceReceiptPublicationRequest, EvidenceArtifactResult>? beforeArtifactCopyForTests)
        {
            try
            {
                Verify();
                var state = ReadState();
                if (state is null || state.InFlightExecutionId != executionId)
                    return StagePublicationOutcome.Refused("execution-authority-mismatch");
                if (state.AuditIntentExecutionId is not null && state.AuditIntentExecutionId != executionId)
                    return StagePublicationOutcome.Refused("audit-intent-owner-mismatch");
                if (state.AuditMismatch && state.AuditIntentExecutionId != executionId)
                    return StagePublicationOutcome.Refused("audit-quarantined");
                if (!EvidenceManifestBuilder.Verify(originPlan, originManifest)
                    || originManifest.ExecutionVerdict != EvidenceExecutionVerdict.Passed || !originManifest.Metrics.CleanupCompleted)
                    return StagePublicationOutcome.Refused("invocation-not-complete-and-passed");
                var producer = originPlan.Profile.Producers.SingleOrDefault(item => item.Id == request.ProducerId);
                var result = originManifest.ProducerResults.SingleOrDefault(item => item.ProducerId == request.ProducerId);
                if (producer is null || result is null || producer.Inputs is null || result.Outcome != EvidenceProducerOutcome.Passed
                    || result.ProducerKey is not null || !EvidenceArtifactValidation.AreValid(producer, result.Artifacts))
                    return StagePublicationOutcome.Refused("producer-not-reusable-passed-result");
                var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceArtifactRoot));
                if (EvidenceReceiptStore.IsWithin(sourceRoot, storeRoot) || EvidenceReceiptStore.IsWithin(storeRoot, sourceRoot))
                    return StagePublicationOutcome.Refused("source-root-overlaps-receipt-store");
                var artifacts = result.Artifacts ?? [];
                await EvidenceReceiptPayload.VerifySourceArtifactsAsync(sourceRoot, producer, artifacts,
                    cancellationToken).ConfigureAwait(false);
                var planBytes = EvidenceCanonicalJson.Serialize(originPlan);
                var manifestBytes = EvidenceCanonicalJson.Serialize(originManifest);
                var producerBytes = EvidenceCanonicalJson.Serialize(result);
                // The authority reader bounds the Base64 producer record by its encoded-character limit.
                // Bound the raw bytes before reserving payload capacity or creating a staging directory.
                if (producerBytes.Length > EvidenceReceiptState.MaximumPassedProducerBase64Characters / 4 * 3)
                    return StagePublicationOutcome.Refused("producer-result-too-large");
                var identityBytes = request.Identity is null
                    ? Array.Empty<byte>()
                    : EvidenceReceiptInputIdentity.Create(request.Identity).CanonicalBytes;
                if (request.Identity is not null)
                {
                    if (request.Identity.Key != Key || request.Identity.RepositoryNamespace != repositoryNamespace)
                        throw new InvalidDataException("Receipt identity does not belong to the held producer key and namespace.");
                }
                if (request.CompletedAt > clock.GetUtcNow())
                    throw new InvalidDataException("Receipt origin completion time is in the future.");
                var documents = new EvidenceReceiptPayload.ReceiptDocumentBytes(planBytes, manifestBytes, producerBytes, identityBytes);
                var reservation = EvidenceReceiptQuota.EstimatePayloadReservation(artifacts.Sum(static artifact => artifact.LengthBytes),
                    planBytes.Length, manifestBytes.Length, producerBytes.Length, identityBytes.Length, artifacts.Count);
                var reservationId = Guid.NewGuid().ToString("N");
                var stageName = EvidenceReceiptPayloadNames.Stage(reservationId);
                var payloadName = EvidenceReceiptPayloadNames.Final(reservationId);
                if (!EvidenceReceiptQuota.TryReservePayload(storeRoot, repositoryNamespace, Key, reservation, clock.GetUtcNow(),
                        reservationId, stageName, payloadName))
                    return StagePublicationOutcome.Refused("payload-quota-full");
                string? stageIdentity = null;
                try
                {
                    using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(directory, false, true)
                        ?? throw new IOException("Receipt key directory is unavailable.");
                    using (var stage = keyRoot.CreateArtifactDirectory(stageName))
                    {
                        stageIdentity = stage.ArtifactDirectoryIdentity;
                        await EvidenceReceiptPayload.WriteStageAsync(stage, sourceRoot, documents, artifacts,
                            beforeArtifactCopyForTests is null ? null : artifact => beforeArtifactCopyForTests(request, artifact),
                            cancellationToken).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    using var destination = CoverageRunOutputLease.AcquireArtifactDirectory(directory, false, true)
                        ?? throw new IOException("Receipt key directory is unavailable.");
                    destination.MoveArtifactEntry(stageName, destination, payloadName, expectedSourceIdentity: stageIdentity);
                    using var payload = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Combine(directory, payloadName), false, true)
                        ?? throw new IOException("Staged receipt payload is unavailable.");
                    var payloadBytes = EvidenceReceiptQuota.MeasurePayload(payload);
                    var next = state with
                    {
                        InFlightExecutionId = null,
                        AuditIntentExecutionId = null,
                        OriginManifestDigest = originManifest.ManifestDigest,
                        OriginCompletedAt = request.CompletedAt,
                        PayloadDirectoryName = payloadName,
                        PassedProducerRecord = Convert.ToBase64String(producerBytes),
                        LastSuccessfulUse = null,
                        AuditMismatch = false,
                        AuditExpectedAssertions = null,
                        InputIdentityDigest = identityBytes.Length == 0 ? null : EvidenceDigest.Sha256(identityBytes)
                    };
                    if (JsonSerializer.SerializeToUtf8Bytes(next).Length > EvidenceReceiptState.MaximumSerializedBytes)
                        throw new IOException("Serialized receipt authority exceeds its encoded-byte bound.");
                    return StagePublicationOutcome.Ready(new(this, request.ProducerId, payloadName, request.CompletedAt,
                        reservationId, reservation, payloadBytes, next, state));
                }
                catch
                {
                    var payloadDeleted = TryDeleteOwnedPayload(directory, payloadName);
                    var stageDeleted = TryDeleteOwnedPayload(directory, stageName, stageIdentity);
                    try
                    {
                        ReleaseReservationAfterConfirmedCleanup(payloadDeleted && stageDeleted,
                            () => EvidenceReceiptQuota.ReleasePayloadReservation(storeRoot, repositoryNamespace, Key, reservationId));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                        or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
                    {
                        // Retain the charge if release is unavailable, preserving the original staging failure
                        // and especially caller cancellation instead of replacing it with a cleanup refusal.
                    }
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (CoverageEvidenceInventoryException exception)
            { return StagePublicationOutcome.Refused(exception.Reason); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return StagePublicationOutcome.Refused("payload-publication-failed"); }
        }

        /// <summary>Deletes one owned payload directory and reports true only after its absence is verified.</summary>
        /// <param name="keyDirectory">The retained private key-directory path.</param>
        /// <param name="name">The generated stage or payload component name.</param>
        /// <param name="identity">The previously retained directory identity when available.</param>
        /// <param name="recoverCommittedStage">Whether committed or retired authority permits recovery of the exact
        /// generated paired stage. Defaults false; precommit cleanup keeps its explicit stage identity.</param>
        /// <returns>True only when every authorized entry is confirmed absent.</returns>
        internal static bool TryDeleteOwnedPayload(string keyDirectory, string name, string? identity = null,
            bool recoverCommittedStage = false)
        {
            try
            {
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(keyDirectory, false, true);
                if (root is null) return false;
                if (recoverCommittedStage) return EvidenceReceiptQuota.DeleteOwnedPayloadIfPresent(root, name, identity);
                var item = root.EnumerateArtifactEntries(EvidenceReceiptPayload.MaximumScannedEntries)
                    .SingleOrDefault(entry => entry.Name == name);
                if (item is null) return true;
                if (item is not { IsDirectory: true }) return false;
                root.DeleteArtifactEntry(name, EvidenceReceiptPayload.MaximumScannedEntries, identity ?? item.Identity);
                return root.EnumerateArtifactEntries(EvidenceReceiptPayload.MaximumScannedEntries).All(entry => entry.Name != name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            { return false; }
        }

        /// <summary>Releases reserved bytes only after every staged entry is confirmed absent.</summary>
        /// <param name="cleanupConfirmed">Whether cleanup verified absence of all owned staging entries.</param>
        /// <param name="release">The quota reservation release operation.</param>
        /// <returns>True when the reservation release was performed.</returns>
        internal static bool ReleaseReservationAfterConfirmedCleanup(bool cleanupConfirmed, Action release)
        {
            ArgumentNullException.ThrowIfNull(release);
            if (!cleanupConfirmed) return false;
            release();
            return true;
        }

        private static async Task CleanupStagedAsync(IEnumerable<StagedReceiptPublication> staged)
        {
            foreach (var item in staged)
            {
                var deleted = TryDeleteOwnedPayload(item.Lease.DirectoryPath, item.PayloadName);
                try
                {
                    ReleaseReservationAfterConfirmedCleanup(deleted,
                        () => EvidenceReceiptQuota.ReleasePayloadReservation(item.Lease.StoreRoot,
                            item.Lease.RepositoryNamespace, item.Lease.Key, item.ReservationId));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                    or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
                {
                    // An unsafe or unreadable quota index keeps the reservation charged; still attempt the other
                    // owned stages. Cleanup uncertainty must never turn into unaccounted retained payload bytes.
                }
                await Task.Yield();
            }
        }

        private sealed record StagedReceiptPublication(EvidenceReceiptKeyLease Lease, string ProducerId,
            string PayloadName, DateTimeOffset CompletedAt, string ReservationId, long ReservationBytes, long PayloadBytes,
            EvidenceReceiptState NextState, EvidenceReceiptState PreviousState);

        private sealed record StagePublicationOutcome(StagedReceiptPublication? Stage, EvidenceReceiptPublicationResult? Result)
        {
            internal static StagePublicationOutcome Ready(StagedReceiptPublication stage) => new(stage, null);
            internal static StagePublicationOutcome Refused(string reason) => new(null, EvidenceReceiptPublicationResult.Refused(reason));
        }

        private EvidenceReceiptReadResult InvalidateOwned(EvidenceReceiptState state, string reason)
        {
            var current = ReadState();
            if (current is not null && current.Key == Key && current.PayloadDirectoryName == state.PayloadDirectoryName
                && current.AuditIntentExecutionId is null && current.InFlightExecutionId is null)
            {
                var invalidated = current with
                {
                    OriginManifestDigest = null,
                    OriginCompletedAt = null,
                    PayloadDirectoryName = null,
                    PassedProducerRecord = null,
                    LastSuccessfulUse = null,
                    InputIdentityDigest = null
                };
                EvidenceReceiptQuota.InvalidatePayload(this, current, invalidated);
                foreach (var retiredPayload in EvidenceReceiptQuota.ReadRetiredPayloadNames(storeRoot, repositoryNamespace, Key))
                {
                    var deleted = TryDeleteOwnedPayload(directory, retiredPayload, recoverCommittedStage: true);
                    EvidenceReceiptQuota.ReleaseRetiredPayload(storeRoot, repositoryNamespace, Key, retiredPayload, deleted);
                }
            }
            return EvidenceReceiptReadResult.Miss(reason);
        }

        /// <summary>Records successful use for LRU ordering after a caller has fully verified and restored the payload.</summary>
        internal void RecordSuccessfulUse(DateTimeOffset usedAt)
        {
            var state = ReadState() ?? throw new InvalidOperationException("No receipt exists to record as used.");
            if (state.AuditIntentExecutionId is not null || state.InFlightExecutionId is not null || state.PayloadDirectoryName is null)
                throw new InvalidOperationException("Only an eligible, settled receipt can update LRU metadata.");
            WriteState(state with { LastSuccessfulUse = usedAt });
        }

        private void WriteState(EvidenceReceiptState state)
        {
            Verify();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > EvidenceReceiptState.MaximumSerializedBytes)
                throw new IOException("Essential receipt authority capacity is unavailable.");
            Verify();
            EvidenceReceiptQuota.RecordState(storeRoot, repositoryNamespace, Key, bytes.Length, state);
        }

        /// <summary>Verifies that the live native key lease still names its retained directory without reading index authority.</summary>
        internal void VerifyBinding() => Verify();

        private void Verify()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nativeLease.VerifyBinding();
        }

        /// <summary>Releases this speculative reservation under the native key lease, then releases coordination.</summary>
        public void Dispose() => DisposeCore(null);

        /// <summary>Unwinds acquisition without starting another root-index wait after its deadline or cancellation.</summary>
        internal void DisposeAfterFailedAcquisition() => DisposeCore(TimeSpan.Zero);

        private void DisposeCore(TimeSpan? indexWaitTimeout)
        {
            if (_disposed) return;
            try
            {
                EvidenceReceiptQuota.ReleaseEssentialReservation(storeRoot, repositoryNamespace, Key, essentialReservationId, indexWaitTimeout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException
                or NotSupportedException)
            {
                // Keep the accounted reservation when the root index cannot safely confirm release.
            }
            finally
            {
                _disposed = true;
                nativeLease.Dispose();
            }
        }
    }
}

/// <summary>Outcome of a non-mutating receipt lookup proposal.</summary>
internal sealed record EvidenceReceiptReadResult(bool IsCandidate, bool IsUnavailable, string Reason, EvidenceReceiptState? State)
{
    internal EvidenceProducerResult? Result { get; init; }
    internal EvidencePlan? OriginPlan { get; init; }
    internal EvidenceManifest? OriginManifest { get; init; }
    internal EvidenceReceiptInputIdentity? InputIdentity { get; init; }
    internal DateTimeOffset? OriginCompletedAt { get; init; }
    internal long? OriginElapsedMilliseconds { get; init; }
    internal static EvidenceReceiptReadResult Candidate(EvidenceReceiptState state) => new(true, false, "candidate", state);
    internal static EvidenceReceiptReadResult Restored(EvidenceProducerResult result, EvidencePlan plan, EvidenceManifest manifest,
        DateTimeOffset completedAt, EvidenceReceiptInputIdentity? inputIdentity = null, long? originElapsedMilliseconds = null) => new(true, false, "restored", null)
        {
            Result = result,
            OriginPlan = plan,
            OriginManifest = manifest,
            OriginCompletedAt = completedAt,
            InputIdentity = inputIdentity,
            OriginElapsedMilliseconds = originElapsedMilliseconds
        };
    internal static EvidenceReceiptReadResult Miss(string reason, EvidenceReceiptState? state = null) => new(false, false, reason, state);
    internal static EvidenceReceiptReadResult Unavailable(string reason, EvidenceReceiptState? state = null) => new(false, true, reason, state);
}

/// <summary>Reports whether a complete fresh producer receipt was committed.</summary>
internal sealed record EvidenceReceiptPublicationResult(bool Published, string Reason, string? PayloadDirectoryName)
{
    internal static EvidenceReceiptPublicationResult PublishedResult(string payloadName) => new(true, "published", payloadName);
    internal static EvidenceReceiptPublicationResult Refused(string reason) => new(false, reason, null);
}

/// <summary>Versioned authoritative state for one private producer receipt key.</summary>
/// <remarks>Missing optional values do not imply successful execution. Unresolved IDs are fail-closed authority.</remarks>
internal sealed record EvidenceReceiptState(int SchemaVersion, string Key, string? InFlightExecutionId,
    string? AuditIntentExecutionId, string? OriginManifestDigest, DateTimeOffset? OriginCompletedAt,
    string? PayloadDirectoryName, string? PassedProducerRecord = null, DateTimeOffset? LastSuccessfulUse = null,
    bool AuditMismatch = false, string? AuditExpectedAssertions = null)
{
    /// <summary>Maximum UTF-8 length of the serialized authority state.</summary>
    internal const int MaximumSerializedBytes = 64 * 1024;

    /// <summary>Maximum character count of the Base64 passed-producer record inside authority state.</summary>
    internal const int MaximumPassedProducerBase64Characters = 64 * 1024;

    /// <summary>Maximum UTF-8 byte count of the persisted audit assertion identity.</summary>
    internal const int MaximumAuditAssertionBytes = 4096;

    /// <summary>Checks the audit assertion identity size, allowing an absent optional value.</summary>
    internal static bool AreAuditAssertionsWithinLimit(string? assertions)
        => assertions is null || Encoding.UTF8.GetByteCount(assertions) <= MaximumAuditAssertionBytes;

    public string? InputIdentityDigest { get; init; }
}

/// <summary>Hash-only producer input inventory retained for stable explain output.</summary>
internal sealed record EvidenceReceiptInputIdentity(IReadOnlyDictionary<string, string> ComponentHashes,
    IReadOnlyList<EvidenceReceiptInputFile> Files)
{
    private const int MaximumComponentHashes = 64;
    private static readonly HashSet<string> AllowedComponentNames = new(
        ["declaration", "implementation", "resources", "tools", "material", "platform", "patch", "files"], StringComparer.Ordinal);

    /// <summary>Projects a prepared producer identity to the persisted hash-only inventory.</summary>
    /// <param name="identity">The prepared identity whose components and files will be retained.</param>
    /// <returns>The validated receipt identity and the same canonical bytes used for its size check.</returns>
    /// <exception cref="InvalidDataException">The resulting inventory is invalid or exceeds its bounds.</exception>
    internal static (EvidenceReceiptInputIdentity Identity, byte[] CanonicalBytes) Create(EvidenceProducerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in identity.ComponentHashes) components.Add(pair.Key, pair.Value);
        var receipt = new EvidenceReceiptInputIdentity(components,
            identity.Files.Select(static file => new EvidenceReceiptInputFile(file.RelativePath, file.Sha256,
                file.Mode, file.LengthBytes)).OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray());
        return (receipt, receipt.ValidateAndEncode());
    }

    /// <summary>Validates the inventory and returns its bounded canonical UTF-8 representation.</summary>
    /// <remarks>Readers still compare these bytes with the bounded payload to reject noncanonical encodings.</remarks>
    /// <returns>The bytes to compare, reserve, hash, or write.</returns>
    /// <exception cref="InvalidDataException">An inventory field is invalid or a limit is exceeded.</exception>
    internal byte[] ValidateAndEncode()
    {
        if (ComponentHashes.Count > MaximumComponentHashes
            || Files.Count > EvidenceProducerIdentityBuilder.MaximumFiles
            || ComponentHashes.Any(pair => !AllowedComponentNames.Contains(pair.Key) || pair.Key.Length > 128 || !IsHash(pair.Value))
            || Files.Any(file => !IsHash(file.Sha256) || !IsValidMode(file.Mode)
                || file.LengthBytes < 0
                || !EvidenceProducerIdentityBuilder.IsValidInputRelativePath(file.RelativePath))
            || Files.Select(static file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != Files.Count)
            throw new InvalidDataException("Receipt input identity is invalid or exceeds its bound.");
        var bytes = EvidenceCanonicalJson.Serialize(this);
        if (bytes.Length > EvidenceProducerIdentityBuilder.MaximumMetadataBytes)
            throw new InvalidDataException("Receipt input identity exceeds its encoded-byte bound.");
        return bytes;
    }

    /// <summary>
    /// Checks whether a persisted mode is legacy-unspecified or describes a regular file in the supported mode domain.
    /// </summary>
    /// <param name="mode">The normalized mode recorded in an input inventory.</param>
    /// <returns>
    /// <see langword="true"/> for mode zero or a 16-bit regular-file mode; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Unix identities store the regular-file type bits with all twelve permission and special bits. Windows stores
    /// the same regular-file type marker with the available read-only distinction. Reject directory, device, negative,
    /// and out-of-range values so accepting the full permission domain does not admit non-file identities.
    /// </remarks>
    private static bool IsValidMode(int mode) => mode == 0
        || ((mode & 0xF000) == 0x8000 && (mode & ~0xFFFF) == 0);

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>One exact repository-relative input path and its SHA-256 digest, without file contents or raw tool and environment values.</summary>
internal sealed record EvidenceReceiptInputFile(string RelativePath, string Sha256, int Mode = 0, long LengthBytes = 0);

/// <summary>One producer payload staged for a single atomic invocation authority commit.</summary>
/// <param name="Lease">The key lease held from authority admission through invocation settlement.</param>
/// <param name="ProducerId">The producer whose passed result is being persisted.</param>
/// <param name="SourceArtifactRoot">The producer's current run artifact root, read without following links.</param>
/// <param name="CompletedAt">The origin completion time used for age eligibility.</param>
internal sealed record EvidenceReceiptPublicationRequest(EvidenceReceiptStore.EvidenceReceiptKeyLease Lease,
    string ProducerId, string SourceArtifactRoot, DateTimeOffset CompletedAt)
{
    /// <summary>Gets the optional prepared identity whose key and namespace are bound to this receipt.</summary>
    internal EvidenceProducerIdentity? Identity { get; init; }
}

/// <summary>Bounded read-only explanation from the latest verified same-producer receipt.</summary>
internal sealed record EvidenceReceiptComparableResult(bool Found, bool IsUnavailable, string Reason,
    string? FirstDifferingPath, string? DifferingComponent, DateTimeOffset? OriginCompletedAt)
{
    internal static EvidenceReceiptComparableResult Candidate(string reason, string? path, string? component, DateTimeOffset at) => new(true, false, reason, path, component, at);
    internal static EvidenceReceiptComparableResult Miss(string reason) => new(false, false, reason, null, null, null);
    internal static EvidenceReceiptComparableResult Unavailable(string reason) => new(false, true, reason, null, null, null);
}
