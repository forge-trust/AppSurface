using System.Security.Cryptography;
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
    private const int MaximumStateBytes = 64 * 1024;
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
            var state = EvidenceReceiptQuota.ReadAuthority(_root, _namespace, normalizedKey, readOnly: _readOnly);
            if (state is null) return EvidenceReceiptReadResult.Miss("missing");
            if (state.InFlightExecutionId is not null) return EvidenceReceiptReadResult.Unavailable("unresolved-execution", state);
            if (state.AuditIntentExecutionId is not null || state.AuditMismatch)
                return EvidenceReceiptReadResult.Miss("audit-quarantined", state);
            if (state.OriginCompletedAt is { } completedAt)
            {
                var age = _clock.GetUtcNow() - completedAt;
                if (age < TimeSpan.Zero) return EvidenceReceiptReadResult.Miss("origin-time-in-future", state);
                if (age >= TimeSpan.FromDays(7)) return EvidenceReceiptReadResult.Miss("origin-expired", state);
            }
            if (state.PayloadDirectoryName is null || state.PassedProducerRecord is null || state.OriginManifestDigest is null)
                return EvidenceReceiptReadResult.Miss("no-passed-receipt", state);
            using var payload = CoverageRunOutputLease.AcquireArtifactDirectory(
                Path.Combine(_root, _namespace, normalizedKey, state.PayloadDirectoryName), false, true);
            if (payload is null) return EvidenceReceiptReadResult.Miss("payload-missing", state);
            try
            {
                var origin = ReadOriginMetadata(payload, state);
                return EvidenceReceiptReadResult.Candidate(state) with
                {
                    Result = origin.Result,
                    OriginPlan = origin.Plan,
                    OriginManifest = origin.Manifest,
                    InputIdentity = origin.InputIdentity,
                    OriginCompletedAt = state.OriginCompletedAt,
                    OriginElapsedMilliseconds = origin.Result.ElapsedMilliseconds
                };
            }
            catch (ReceiptMetadataException exception)
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
            var current = EvidenceReceiptInputIdentity.Create(currentIdentity);
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

    /// <summary>Reads and binds the canonical origin metadata without changing receipt state or touching artifacts.</summary>
    private static ReceiptOriginMetadata ReadOriginMetadata(CoverageRunOutputLease payload, EvidenceReceiptState state)
    {
        var resultBytes = ReadBoundedFile(payload, "passed-producer.json", 64 * 1024);
        if (!DecodePassedProducerRecord(state.PassedProducerRecord).AsSpan().SequenceEqual(resultBytes))
            throw new ReceiptMetadataException("passed-producer-record-invalid");

        var manifestBytes = ReadBoundedFile(payload, "origin.manifest.json", 4 * 1024 * 1024);
        var result = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(resultBytes);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);
        var planBytes = ReadBoundedFile(payload, "origin.plan.json", 4 * 1024 * 1024);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);

        EvidenceReceiptInputIdentity? inputIdentity = null;
        if (state.InputIdentityDigest is not null)
        {
            var identityBytes = ReadBoundedFile(payload, "identity.json", EvidenceProducerIdentityBuilder.MaximumMetadataBytes);
            if (!StringComparer.Ordinal.Equals(EvidenceDigest.Sha256(identityBytes), state.InputIdentityDigest))
                throw new ReceiptMetadataException("input-identity-digest-invalid");
            inputIdentity = EvidenceCanonicalJson.Deserialize<EvidenceReceiptInputIdentity>(identityBytes);
            inputIdentity.Validate();
            if (!identityBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(inputIdentity)))
                throw new ReceiptMetadataException("input-identity-noncanonical");
        }

        if (!resultBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(result))
            || !manifestBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(manifest))
            || !planBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan))
            || !EvidenceManifestBuilder.Verify(plan, manifest)
            || !StringComparer.Ordinal.Equals(manifest.ManifestDigest, state.OriginManifestDigest))
            throw new ReceiptMetadataException("origin-binding-invalid");

        if (result.Outcome != EvidenceProducerOutcome.Passed
            || !manifest.ProducerResults.Any(item => item.ProducerId == result.ProducerId
                && resultBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(item))))
            throw new ReceiptMetadataException("passed-producer-invalid");

        return new ReceiptOriginMetadata(plan, manifest, result, inputIdentity);
    }

    /// <summary>The verified origin data shared by inspection and restoration.</summary>
    private sealed record ReceiptOriginMetadata(EvidencePlan Plan, EvidenceManifest Manifest,
        EvidenceProducerResult Result, EvidenceReceiptInputIdentity? InputIdentity);

    /// <summary>A metadata refusal that inspection and leased restoration handle according to their ownership rules.</summary>
    private sealed class ReceiptMetadataException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private static byte[] ReadBoundedFile(CoverageRunOutputLease root, string name, int maximumBytes)
    {
        using var stream = root.OpenArtifactFile(name, false);
        if (stream.Length < 0 || stream.Length > maximumBytes) throw new InvalidDataException("Receipt document exceeds its bound.");
        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] DecodePassedProducerRecord(string? encoded)
    {
        if (encoded is null || encoded.Length > MaximumStateBytes)
            throw new InvalidDataException("Passed producer authority record is missing or over its encoded bound.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("Passed producer authority record exceeds its byte bound.");
        return bytes;
    }

    private static void ValidateHash(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected exactly 64 hexadecimal characters.", parameter);
    }

    /// <summary>Owns one key directory and its native operation lease.</summary>
    internal sealed class EvidenceReceiptKeyLease : IDisposable
    {
        private const int MaximumPayloadFiles = 256;
        private const int MaximumPayloadBytes = (int)EvidenceArtifactWriter.MaximumTotalArtifactBytes;
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
            if (expectedAssertions is not null && Encoding.UTF8.GetByteCount(expectedAssertions) > 4096)
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
            if (Encoding.UTF8.GetByteCount(expectedAssertions) > 4096)
                throw new InvalidDataException("Expected audit assertions exceed their bound.");
            var state = ReadState() ?? throw new InvalidOperationException("Audit intent must be established before recording a mismatch.");
            if (!StringComparer.Ordinal.Equals(state.AuditIntentExecutionId, executionId))
                throw new InvalidOperationException("Audit intent belongs to another execution.");
            WriteState(state with { AuditExpectedAssertions = state.AuditExpectedAssertions ?? expectedAssertions, AuditMismatch = true });
        }

        /// <summary>Returns the currently admissible receipt candidate without changing LRU metadata.</summary>
        /// <remarks>Pending intent, mismatch quarantine, or unresolved execution authority always denies reuse.</remarks>
        internal EvidenceReceiptReadResult ProposeReadReceipt()
        {
            var state = ReadState();
            if (state is null) return EvidenceReceiptReadResult.Miss("missing");
            if (state.InFlightExecutionId is not null) return EvidenceReceiptReadResult.Unavailable("unresolved-execution");
            if (state.AuditIntentExecutionId is not null || state.AuditMismatch) return EvidenceReceiptReadResult.Miss("audit-quarantined");
            if (state.OriginCompletedAt is { } completedAt)
            {
                var age = clock.GetUtcNow() - completedAt;
                if (age < TimeSpan.Zero) return EvidenceReceiptReadResult.Miss("origin-time-in-future", state);
                if (age >= TimeSpan.FromDays(7)) return EvidenceReceiptReadResult.Miss("origin-expired", state);
            }
            if (state.PayloadDirectoryName is null || state.PassedProducerRecord is null || state.OriginManifestDigest is null)
                return EvidenceReceiptReadResult.Miss("no-passed-receipt");
            return EvidenceReceiptReadResult.Candidate(state);
        }

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
                var payload = OpenPayload(state.PayloadDirectoryName!);
                using (payload.Root)
                {
                    var rootEntries = payload.Root.EnumerateArtifactEntries(8);
                    var names = rootEntries.Select(static entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                    var requiredNames = new HashSet<string>(["origin.plan.json", "origin.manifest.json", "passed-producer.json", "artifacts"], StringComparer.Ordinal);
                    if (state.InputIdentityDigest is not null) requiredNames.Add("identity.json");
                    if (!names.SetEquals(requiredNames)
                        || rootEntries.Single(static entry => entry.Name == "artifacts").IsDirectory is false
                        || rootEntries.Where(static entry => entry.Name != "artifacts").Any(static entry => entry.IsDirectory))
                        throw new InvalidOwnedReceiptException("payload-container-invalid");
                    var origin = ReadOriginMetadata(payload.Root, state);
                    if (state.OriginCompletedAt is null)
                        throw new InvalidOwnedReceiptException("origin-binding-invalid");

                    var declaration = origin.Plan.Profile.Producers.SingleOrDefault(item => item.Id == producer.Id);
                    var originResult = origin.Manifest.ProducerResults.SingleOrDefault(item => item.ProducerId == producer.Id);
                    if (declaration is null || originResult is null || origin.Result.ProducerId != producer.Id
                        || !StringComparer.Ordinal.Equals(
                            EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(declaration),
                            EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(producer))
                        || !EvidenceArtifactValidation.AreValid(producer, originResult.Artifacts))
                        throw new InvalidOwnedReceiptException("passed-producer-invalid");

                    var artifacts = originResult.Artifacts ?? [];
                    VerifyArtifacts(payload.Root, artifacts, cancellationToken);
                    if (producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase))
                        ValidateCoverageReceiptInventory(producer, artifacts,
                            Path.Combine(storeRoot, repositoryNamespace, Key, state.PayloadDirectoryName!, "artifacts"), cancellationToken);
                    try
                    {
                        using var artifactRoot = OpenSubdirectory(payload.Root, "artifacts");
                        foreach (var artifact in artifacts.OrderBy(static item => item.LogicalName, StringComparer.Ordinal)
                                     .ThenBy(static item => item.RelativePath, StringComparer.Ordinal))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var opened = OpenRelativeFile(artifactRoot, artifact.RelativePath, requirePrivate: true);
                            var restored = await writer.WriteAsync(artifact.LogicalName, artifact.RelativePath,
                                opened.Stream, cancellationToken).ConfigureAwait(false);
                            if (!ArtifactMetadataEquals(restored, artifact))
                                throw new InvalidOwnedReceiptException("restore-artifact-changed-during-copy");
                        }
                        if (!await writer.VerifyWrittenArtifactsAsync(cancellationToken).ConfigureAwait(false))
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
                        OriginCompletedAt = state.OriginCompletedAt,
                        Diagnostic = producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase)
                            ? "Coverage report: coverage/coverage-gate.md (relative to the producer artifact root)."
                            : originResult.Diagnostic
                    };
                    return EvidenceReceiptReadResult.Restored(reused, origin.Plan, origin.Manifest,
                        state.OriginCompletedAt.Value, origin.InputIdentity, originResult.ElapsedMilliseconds);
                }
            }
            catch (ReceiptMetadataException exception)
            {
                return InvalidateOwned(state, exception.Reason);
            }
            catch (InvalidOwnedReceiptException exception)
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
                    stage.ProducerId, executionId, stage.NextState, stage.StateBytes, stage.PayloadName, stage.CompletedAt,
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
                await VerifySourceArtifactsAsync(sourceRoot, artifacts, cancellationToken).ConfigureAwait(false);
                if (producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase))
                    ValidateCoverageReceiptInventory(producer, artifacts, sourceRoot, cancellationToken);
                var planBytes = EvidenceCanonicalJson.Serialize(originPlan);
                var manifestBytes = EvidenceCanonicalJson.Serialize(originManifest);
                var producerBytes = EvidenceCanonicalJson.Serialize(result);
                // The authority reader bounds the Base64 producer record to MaximumStateBytes characters.
                // Bound the raw bytes before reserving payload capacity or creating a staging directory.
                if (producerBytes.Length > MaximumStateBytes / 4 * 3)
                    return StagePublicationOutcome.Refused("producer-result-too-large");
                var identity = request.Identity is null
                    ? EvidenceReceiptInputIdentity.Create(request.ComponentHashes, request.Files)
                    : EvidenceReceiptInputIdentity.Create(request.Identity);
                if (request.Identity is not null)
                {
                    if (request.Identity.Key != Key || request.Identity.RepositoryNamespace != repositoryNamespace)
                        throw new InvalidDataException("Receipt identity does not belong to the held producer key and namespace.");
                }
                if (request.CompletedAt > clock.GetUtcNow())
                    throw new InvalidDataException("Receipt origin completion time is in the future.");
                var identityBytes = identity is null ? [] : EvidenceCanonicalJson.Serialize(identity);
                if (identityBytes.Length > EvidenceProducerIdentityBuilder.MaximumMetadataBytes)
                    throw new InvalidDataException("Receipt input identity exceeds its encoded-byte bound.");
                var reservation = EvidenceReceiptQuota.EstimatePayloadReservation(artifacts.Sum(static artifact => artifact.LengthBytes),
                    planBytes.Length, manifestBytes.Length, producerBytes.Length, identityBytes.Length, artifacts.Count);
                var reservationId = Guid.NewGuid().ToString("N");
                var stageName = "payload-stage-" + reservationId;
                var payloadName = "payload-" + reservationId;
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
                        WriteImmediate(stage, "origin.plan.json", planBytes, 4 * 1024 * 1024);
                        WriteImmediate(stage, "origin.manifest.json", manifestBytes, 4 * 1024 * 1024);
                        WriteImmediate(stage, "passed-producer.json", producerBytes, 64 * 1024);
                        if (identityBytes.Length != 0) WriteImmediate(stage, "identity.json", identityBytes, EvidenceProducerIdentityBuilder.MaximumMetadataBytes);
                        using var artifactsRoot = stage.CreateArtifactDirectory("artifacts");
                        foreach (var artifact in artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            beforeArtifactCopyForTests?.Invoke(request, artifact);
                            await CopySourceArtifactToStageAsync(sourceRoot, artifactsRoot, artifact, cancellationToken).ConfigureAwait(false);
                        }
                        VerifyArtifacts(stage, artifacts, cancellationToken);
                        VerifyStagedPayload(stage, artifacts);
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
                        InputIdentityDigest = identityBytes.Length == 0 ? null : Hash(identityBytes)
                    };
                    var encodedStateBytes = JsonSerializer.SerializeToUtf8Bytes(next).Length;
                    if (encodedStateBytes > MaximumStateBytes)
                        throw new IOException("Serialized receipt authority exceeds its encoded-byte bound.");
                    return StagePublicationOutcome.Ready(new(this, request.ProducerId, payloadName, request.CompletedAt,
                        reservationId, reservation, payloadBytes, encodedStateBytes, next, state, state.PayloadDirectoryName));
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
                var item = root.EnumerateArtifactEntries(MaximumPayloadFiles * 4 + 8).SingleOrDefault(entry => entry.Name == name);
                if (item is null) return true;
                if (item is not { IsDirectory: true }) return false;
                root.DeleteArtifactEntry(name, MaximumPayloadFiles * 4 + 8, identity ?? item.Identity);
                return root.EnumerateArtifactEntries(MaximumPayloadFiles * 4 + 8).All(entry => entry.Name != name);
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
            string PayloadName, DateTimeOffset CompletedAt, string ReservationId, long ReservationBytes, long PayloadBytes, int StateBytes,
            EvidenceReceiptState NextState, EvidenceReceiptState PreviousState, string? PreviousPayloadName);

        private sealed record StagePublicationOutcome(StagedReceiptPublication? Stage, EvidenceReceiptPublicationResult? Result)
        {
            internal static StagePublicationOutcome Ready(StagedReceiptPublication stage) => new(stage, null);
            internal static StagePublicationOutcome Refused(string reason) => new(null, EvidenceReceiptPublicationResult.Refused(reason));
        }

        /// <summary>Carries a typed validation miss outside open payload handles before invalidation.</summary>
        private sealed class InvalidOwnedReceiptException(string reason) : Exception
        {
            internal string Reason { get; } = reason;
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

        private (CoverageRunOutputLease Root, string Path) OpenPayload(string payloadName)
        {
            CoverageRunOutputLease.ValidateArtifactComponent(payloadName);
            if (!payloadName.StartsWith("payload-", StringComparison.Ordinal)) throw new InvalidDataException("Receipt payload name is not owned.");
            var path = Path.Combine(directory, payloadName);
            return (CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)
                ?? throw new DirectoryNotFoundException("Receipt payload is missing."), path);
        }

        private static void WriteImmediate(CoverageRunOutputLease root, string name, byte[] bytes, int maximumBytes)
        {
            if (bytes.Length > maximumBytes) throw new InvalidDataException("Receipt document exceeds its bound.");
            using var output = root.OpenArtifactFile(name, true);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }

        private static void VerifyArtifacts(CoverageRunOutputLease payload,
            IReadOnlyList<EvidenceArtifactResult> artifacts, CancellationToken cancellationToken)
        {
            ValidateInventoryBounds(artifacts);
            using var artifactsRoot = OpenSubdirectory(payload, "artifacts");
            long total = 0;
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (length, digest) = HashRelative(artifactsRoot, artifact.RelativePath, artifact.LengthBytes,
                    requirePrivate: true, cancellationToken);
                total = checked(total + length);
                if (total > MaximumPayloadBytes || !StringComparer.OrdinalIgnoreCase.Equals(digest, artifact.Sha256))
                    throw new InvalidDataException("Receipt artifact inventory digest or size differs.");
            }
            VerifyExactTree(artifactsRoot, artifacts.Select(static item => item.RelativePath).ToHashSet(StringComparer.Ordinal));
        }

        /// <summary>Verifies every declared artifact before optional receipt custody is staged.</summary>
        /// <param name="sourceRoot">Execution report root; it need not exist when the complete inventory is empty.</param>
        /// <param name="artifacts">Complete result inventory, already checked against the producer's slot declarations.</param>
        /// <param name="cancellationToken">Cancels validation before filesystem access and between streaming reads.</param>
        /// <remarks>Empty generic inventories require no source files or directory. The caller independently checks
        /// source/store separation, passing origin claims, coverage-specific completeness, and exact retained custody.</remarks>
        private static async Task VerifySourceArtifactsAsync(string sourceRoot,
            IReadOnlyList<EvidenceArtifactResult> artifacts, CancellationToken cancellationToken)
        {
            ValidateInventoryBounds(artifacts);
            cancellationToken.ThrowIfCancellationRequested();
            if (artifacts.Count == 0) return;
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetFullPath(sourceRoot), false, false)
                ?? throw new DirectoryNotFoundException("Producer artifact root is unavailable.");
            long total = 0;
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (length, digest) = await HashRelativeAsync(root, artifact.RelativePath, artifact.LengthBytes,
                    requirePrivate: false, cancellationToken).ConfigureAwait(false);
                total = checked(total + length);
                if (total > MaximumPayloadBytes || !StringComparer.OrdinalIgnoreCase.Equals(digest, artifact.Sha256))
                    throw new InvalidDataException("Source artifact inventory digest or size differs.");
            }
            // Source roots may also contain invocation plans and original execution output. Only the
            // declared writer inventory is copied; the staged/retained receipt tree must be exact.
        }

        private static (long Length, string Digest) HashRelative(CoverageRunOutputLease root, string relativePath,
            long expectedLength, bool requirePrivate, CancellationToken cancellationToken)
        {
            using var opened = OpenRelativeFile(root, relativePath, requirePrivate);
            return HashStream(opened.Stream, expectedLength, cancellationToken);
        }

        private static async Task<(long Length, string Digest)> HashRelativeAsync(CoverageRunOutputLease root,
            string relativePath, long expectedLength, bool requirePrivate, CancellationToken cancellationToken)
        {
            using var opened = OpenRelativeFile(root, relativePath, requirePrivate);
            if (expectedLength < 0 || expectedLength > MaximumPayloadBytes || opened.Stream.Length != expectedLength)
                throw new InvalidDataException("Artifact length differs from its manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var count = await opened.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length = checked(length + count);
                if (length > expectedLength) throw new InvalidDataException("Artifact grew during read.");
                hash.AppendData(buffer, 0, count);
            }
            if (length != expectedLength) throw new InvalidDataException("Artifact changed during read.");
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        /// <summary>Hashes an exact-length receipt stream using bounded reads without taking stream ownership.</summary>
        /// <param name="input">Caller-owned readable stream exposing its length, at the beginning of its declared artifact bytes.</param>
        /// <param name="expectedLength">Manifest length, from zero through the 256-MiB payload bound.</param>
        /// <param name="cancellationToken">Checked before each read, including the final end-of-stream observation.</param>
        /// <returns>The observed length and lower-case SHA-256 after exact-length end-of-stream verification.</returns>
        /// <remarks>
        /// This internal validation boundary is intentionally exposed for contract tests without reflection.
        /// It checks the initial length and refuses either growth or truncation while streaming in at most 64-KiB reads.
        /// The caller retains the stream, its position, and responsibility for disposal on success and failure.
        /// </remarks>
        /// <exception cref="InvalidDataException">The manifest length is outside its bound or the stream differs from it.</exception>
        /// <exception cref="OperationCanceledException">The caller cancelled a read boundary.</exception>
        internal static (long Length, string Digest) HashStream(Stream input, long expectedLength,
            CancellationToken cancellationToken)
        {
            if (expectedLength < 0 || expectedLength > MaximumPayloadBytes || input.Length != expectedLength)
                throw new InvalidDataException("Artifact length differs from its manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                length = checked(length + count);
                if (length > expectedLength) throw new InvalidDataException("Artifact grew during read.");
                hash.AppendData(buffer, 0, count);
            }
            if (length != expectedLength) throw new InvalidDataException("Artifact changed during read.");
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        private static async Task CopySourceArtifactToStageAsync(string sourceRoot, CoverageRunOutputLease stageRoot,
            EvidenceArtifactResult expected, CancellationToken cancellationToken)
        {
            using var source = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetFullPath(sourceRoot), false, false)
                ?? throw new DirectoryNotFoundException("Producer artifact root is unavailable.");
            using var opened = OpenRelativeFile(source, expected.RelativePath, requirePrivate: false);
            using var destination = OpenRelativeOutput(stageRoot, expected.RelativePath);
            if (opened.Stream.Length != expected.LengthBytes)
                throw new InvalidDataException("Source artifact length changed before staging.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await opened.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length = checked(length + count);
                if (length > expected.LengthBytes) throw new InvalidDataException("Source artifact grew during staging.");
                hash.AppendData(buffer, 0, count);
                await destination.Stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (length != expected.LengthBytes
                || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected.Sha256))
                throw new InvalidDataException("Source artifact changed during staging.");
            await destination.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Stream.Flush(flushToDisk: true);
        }

        private static bool ArtifactMetadataEquals(EvidenceArtifactResult actual, EvidenceArtifactResult expected)
            => actual.LogicalName == expected.LogicalName && actual.RelativePath == expected.RelativePath
                && actual.MediaType == expected.MediaType && actual.LengthBytes == expected.LengthBytes
                && StringComparer.OrdinalIgnoreCase.Equals(actual.Sha256, expected.Sha256);

        private static void ValidateInventoryBounds(IReadOnlyList<EvidenceArtifactResult> artifacts)
        {
            if (artifacts.Count > MaximumPayloadFiles || !artifacts.Select(static item => item.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count().Equals(artifacts.Count))
                throw new InvalidDataException("Receipt artifact inventory exceeds its bound or contains duplicate paths.");
            var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in artifacts)
            {
                try { _ = EvidenceArtifactValidation.NormalizePortableRelativePath(artifact.RelativePath); }
                catch (ArgumentException exception) { throw new InvalidDataException("Receipt artifact path is not portable.", exception); }
                var parts = artifact.RelativePath.Split('/');
                // The payload's artifacts container adds one directory to the exact-tree depth.
                if (parts.Length > 32) throw new InvalidDataException("Receipt artifact path exceeds its depth bound.");
                var prefix = string.Empty;
                foreach (var part in parts)
                {
                    prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                    if (components.TryGetValue(prefix, out var spelling) && spelling != prefix)
                        throw new InvalidDataException("Receipt artifact paths have colliding component spellings.");
                    components[prefix] = prefix;
                }
            }
            // Four metadata files and the artifacts container also consume exact-tree entries.
            if (components.Count + 5 > MaximumPayloadFiles * 4)
                throw new InvalidDataException("Receipt artifact tree exceeds its bounded entry count.");
        }

        private static void ValidateCoverageReceiptInventory(EvidenceProducerDeclaration producer,
            IReadOnlyList<EvidenceArtifactResult> artifacts, string artifactRoot, CancellationToken cancellationToken)
        {
            var slot = producer.ArtifactSlots.SingleOrDefault(item => item.LogicalName == CoverageEvidenceInventory.LogicalName);
            if (slot is null || slot.RelativeRoot != "coverage" || slot.MaximumFiles is null
                || slot.MaximumFiles > CoverageEvidenceInventory.MaximumFiles)
                throw new CoverageEvidenceInventoryException("inventory-slot-ineligible");

            var files = artifacts.Where(item => item.LogicalName == CoverageEvidenceInventory.LogicalName)
                .Select(item => new CoverageEvidenceInventoryFile(item.RelativePath, item.LengthBytes, item.Sha256))
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
            if (files.Length == 0 || files.Length > slot.MaximumFiles || files.Length > CoverageEvidenceInventory.MaximumFiles
                || files.Sum(file => file.LengthBytes) > slot.MaximumBytes)
                throw new CoverageEvidenceInventoryException("inventory-required-file-missing");

            var inventory = new CoverageEvidenceInventory(CoverageEvidenceInventory.CurrentVersion, files);
            inventory.Validate(artifactRoot, cancellationToken);
            var names = files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
            foreach (var required in new[]
                { "coverage/coverage.cobertura.xml", "coverage/coverage-gate.json", "coverage/coverage-gate.md", "coverage/timings.json", "coverage/summary.txt" })
                if (!names.Contains(required)) throw new CoverageEvidenceInventoryException("inventory-required-file-missing");

            using var timings = CoverageRunArtifactReader.OpenRegularFile(Path.GetFullPath(artifactRoot),
                Path.Combine(Path.GetFullPath(artifactRoot), "coverage"),
                Path.Combine(Path.GetFullPath(artifactRoot), "coverage", "timings.json"));
            if (timings.Length > CoverageEvidenceInventory.MaximumMetadataBytes)
                throw new CoverageEvidenceInventoryException("inventory-metadata-limit");
            using var document = JsonDocument.Parse(timings);
            foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
            {
                var slug = project.GetProperty("slug").GetString();
                if (string.IsNullOrWhiteSpace(slug)) throw new CoverageEvidenceInventoryException("inventory-project-manifest-mismatch");
                foreach (var required in new[]
                    { $"coverage/projects/{slug}/{CoverageProjectManifest.FileName}", $"coverage/projects/{slug}/coverage.cobertura.xml" })
                    if (!names.Contains(required)) throw new CoverageEvidenceInventoryException("inventory-required-file-missing");
                foreach (var report in project.GetProperty("testResults").EnumerateArray())
                {
                    var path = report.GetProperty("path").GetString();
                    if (path is null || !names.Contains("coverage/" + path))
                        throw new CoverageEvidenceInventoryException("inventory-required-file-missing");
                }
            }
        }

        private static OpenedArtifactFile OpenRelativeFile(CoverageRunOutputLease root, string relativePath, bool requirePrivate)
        {
            var normalized = EvidenceArtifactValidation.NormalizePortableRelativePath(relativePath);
            if (!StringComparer.Ordinal.Equals(normalized, relativePath)) throw new InvalidDataException("Artifact path is not canonical.");
            var components = normalized.Split('/');
            var current = root;
            var owned = new List<CoverageRunOutputLease>();
            try
            {
                for (var index = 0; index < components.Length - 1; index++)
                {
                    current = OpenSubdirectory(current, components[index], requirePrivate);
                    owned.Add(current);
                }
                return new OpenedArtifactFile(current.OpenArtifactFile(components[^1], false, requirePrivate), owned);
            }
            catch
            {
                foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose();
                throw;
            }
        }

        private static OpenedArtifactOutput OpenRelativeOutput(CoverageRunOutputLease root, string relativePath)
        {
            var components = EvidenceArtifactValidation.NormalizePortableRelativePath(relativePath).Split('/');
            var current = root;
            var owned = new List<CoverageRunOutputLease>();
            try
            {
                for (var index = 0; index < components.Length - 1; index++)
                {
                    var child = GetOrCreateSubdirectory(current, components[index]);
                    owned.Add(child);
                    current = child;
                }
                return new OpenedArtifactOutput(current.OpenArtifactFile(components[^1], true), owned);
            }
            catch { foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose(); throw; }
        }

        private static void VerifyStagedPayload(CoverageRunOutputLease stage, IReadOnlyList<EvidenceArtifactResult> artifacts)
        {
            var expected = artifacts.Select(static item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
            using var artifactRoot = OpenSubdirectory(stage, "artifacts");
            VerifyExactTree(artifactRoot, expected);
        }

        /// <summary>Verifies an exact, bounded artifact-path inventory without taking ownership of the root.</summary>
        /// <param name="root">Caller-owned native directory lease, held exclusively through verification.</param>
        /// <param name="expectedFiles">Immutable canonical portable root-relative file paths expected in the tree.</param>
        /// <param name="requirePrivate">Whether every opened child must retain current-user-only access.</param>
        /// <param name="maximumTreeEntries">A stricter entry budget from one through 1,024; defaults to the production limit of 1,024.</param>
        /// <remarks>Callers separately verify artifact hashes and lengths. Verification owns and disposes only its child leases.
        /// A smaller per-call budget supports bounded native traversal tests without changing process file-descriptor limits.</remarks>
        internal static void VerifyExactTree(CoverageRunOutputLease root, HashSet<string> expectedFiles, bool requirePrivate = true,
            int maximumTreeEntries = MaximumPayloadFiles * 4)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(expectedFiles);
            if (maximumTreeEntries is < 1 or > MaximumPayloadFiles * 4)
                throw new ArgumentOutOfRangeException(nameof(maximumTreeEntries));
            var actualFiles = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<(CoverageRunOutputLease Root, string Prefix, bool Owned)>();
            pending.Push((root, string.Empty, false));
            var owned = new List<CoverageRunOutputLease>();
            try
            {
                var entryCount = 0;
                while (pending.TryPop(out var item))
                {
                    if (item.Prefix.Count(static character => character == '/') >= 32)
                        throw new InvalidDataException("Receipt payload nesting exceeds its bound.");
                    foreach (var entry in item.Root.EnumerateArtifactEntries(MaximumPayloadFiles * 4 + 8))
                    {
                        if (++entryCount > maximumTreeEntries) throw new InvalidDataException("Receipt payload tree exceeds its bound.");
                        var path = item.Prefix.Length == 0 ? entry.Name : item.Prefix + "/" + entry.Name;
                        if (entry.IsDirectory)
                        {
                            if (!expectedFiles.Any(file => file.StartsWith(path + "/", StringComparison.Ordinal)))
                                throw new InvalidDataException("Receipt payload contains an undeclared directory.");
                            var child = OpenSubdirectory(item.Root, entry.Name, requirePrivate);
                            owned.Add(child);
                            pending.Push((child, path, true));
                        }
                        else if (!actualFiles.Add(path)) throw new InvalidDataException("Receipt payload contains duplicate paths.");
                    }
                }
                if (!actualFiles.SetEquals(expectedFiles)) throw new InvalidDataException("Receipt payload inventory differs from its manifest.");
            }
            finally { foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose(); }
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static CoverageRunOutputLease OpenSubdirectory(CoverageRunOutputLease parent, string name, bool requirePrivate = true)
        {
            CoverageRunOutputLease.ValidateArtifactComponent(name);
            return CoverageRunOutputLease.AcquireArtifactDirectory(Path.Combine(parent.ArtifactDirectoryPath, name), false, requirePrivate)
                ?? throw new DirectoryNotFoundException("Receipt payload directory is missing.");
        }

        private static CoverageRunOutputLease GetOrCreateSubdirectory(CoverageRunOutputLease parent, string name)
        {
            try { return OpenSubdirectory(parent, name); }
            catch (DirectoryNotFoundException) { return parent.CreateArtifactDirectory(name); }
        }

        private sealed class OpenedArtifactFile(FileStream stream, IReadOnlyList<CoverageRunOutputLease> roots) : IDisposable
        {
            internal FileStream Stream { get; } = stream;
            public void Dispose()
            {
                Stream.Dispose();
                foreach (var root in roots.Reverse()) root.Dispose();
            }
        }

        private sealed class OpenedArtifactOutput(FileStream stream, IReadOnlyList<CoverageRunOutputLease> roots) : IDisposable
        {
            internal FileStream Stream { get; } = stream;
            public void Dispose()
            {
                Stream.Dispose();
                foreach (var root in roots.Reverse()) root.Dispose();
            }
        }

        /// <summary>Records successful use for LRU ordering after a caller has fully verified and restored the payload.</summary>
        internal void RecordSuccessfulUse(DateTimeOffset usedAt)
        {
            var state = ReadState() ?? throw new InvalidOperationException("No receipt exists to record as used.");
            if (state.AuditIntentExecutionId is not null || state.InFlightExecutionId is not null || state.PayloadDirectoryName is null)
                throw new InvalidOperationException("Only an eligible, settled receipt can update LRU metadata.");
            var updated = state with { LastSuccessfulUse = usedAt };
            WriteState(updated);
            try
            {
                EvidenceReceiptQuota.RecordSuccessfulUse(storeRoot, repositoryNamespace, Key, usedAt,
                    JsonSerializer.SerializeToUtf8Bytes(updated).Length, updated);
            }
            catch (EvidenceReceiptQuotaUnavailableException)
            {
                // LRU is advisory; a receipt that was fully verified and restored remains valid if indexing is temporarily unavailable.
            }
        }

        /// <summary>Atomically commits a passed immutable payload reference and clears matching audit intent.</summary>
        /// <param name="executionId">Execution that owns the current durable authority.</param>
        /// <param name="originManifestDigest">Canonical origin-manifest SHA-256.</param>
        /// <param name="originCompletedAt">Caller-supplied origin completion time.</param>
        /// <param name="payloadDirectoryName">Validated immutable final component beginning with <c>payload-</c>;
        /// <c>payload-stage-</c> names, including case variants, are excluded.</param>
        /// <param name="passedProducerRecord">Bounded canonical producer record bytes.</param>
        internal void CommitPassed(string executionId, string originManifestDigest, DateTimeOffset originCompletedAt,
            string payloadDirectoryName, byte[] passedProducerRecord)
        {
            ValidateHash(originManifestDigest, nameof(originManifestDigest));
            if (originCompletedAt > clock.GetUtcNow())
                throw new ArgumentOutOfRangeException(nameof(originCompletedAt), "Receipt origin completion time cannot be in the future.");
            CoverageRunOutputLease.ValidateArtifactComponent(payloadDirectoryName);
            ArgumentNullException.ThrowIfNull(passedProducerRecord);
            if (passedProducerRecord.Length > 32 * 1024) throw new InvalidDataException("Producer record exceeds its bound.");
            var state = ReadState() ?? throw new InvalidOperationException("No execution authority exists.");
            if (state.InFlightExecutionId != executionId) throw new InvalidOperationException("Execution authority belongs to another execution.");
            if (state.AuditIntentExecutionId is not null && state.AuditIntentExecutionId != executionId)
                throw new InvalidOperationException("Audit intent belongs to another execution.");
            if (state.AuditMismatch && state.AuditIntentExecutionId != executionId)
                throw new InvalidOperationException("An ordinary execution cannot clear audit quarantine.");
            WriteState(state with
            {
                InFlightExecutionId = null,
                AuditIntentExecutionId = null,
                OriginManifestDigest = originManifestDigest.ToLowerInvariant(),
                OriginCompletedAt = originCompletedAt,
                PayloadDirectoryName = payloadDirectoryName,
                PassedProducerRecord = Convert.ToBase64String(passedProducerRecord),
                AuditMismatch = false,
                AuditExpectedAssertions = null
            });
        }

        private void WriteState(EvidenceReceiptState state)
        {
            Verify();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > MaximumStateBytes) throw new IOException("Essential receipt authority capacity is unavailable.");
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
    public string? InputIdentityDigest { get; init; }
}

/// <summary>Hash-only producer input inventory retained for stable explain output.</summary>
internal sealed record EvidenceReceiptInputIdentity(IReadOnlyDictionary<string, string> ComponentHashes,
    IReadOnlyList<EvidenceReceiptInputFile> Files)
{
    private static readonly HashSet<string> AllowedComponentNames = new(
        ["declaration", "implementation", "resources", "tools", "material", "platform", "patch", "files"], StringComparer.Ordinal);

    internal static EvidenceReceiptInputIdentity? Create(IReadOnlyDictionary<string, string>? componentHashes,
        IReadOnlyList<EvidenceReceiptInputFile>? files)
    {
        if (componentHashes is null && files is null) return null;
        var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (componentHashes is not null)
            foreach (var (name, hash) in componentHashes)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !IsHash(hash))
                    throw new InvalidDataException("Receipt identity components must be named SHA-256 hashes only.");
                components.Add(name, hash.ToLowerInvariant());
            }
        var inventory = (files ?? []).OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var identity = new EvidenceReceiptInputIdentity(components, inventory);
        identity.Validate();
        if (EvidenceCanonicalJson.Serialize(identity).Length > EvidenceProducerIdentityBuilder.MaximumMetadataBytes)
            throw new InvalidDataException("Receipt input identity exceeds its encoded-byte bound.");
        return identity;
    }

    internal static EvidenceReceiptInputIdentity Create(EvidenceProducerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in identity.ComponentHashes) components.Add(pair.Key, pair.Value);
        var receipt = new EvidenceReceiptInputIdentity(components,
            identity.Files.Select(static file => new EvidenceReceiptInputFile(file.RelativePath, file.Sha256,
                file.Mode, file.LengthBytes)).OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray());
        receipt.Validate();
        if (EvidenceCanonicalJson.Serialize(receipt).Length > EvidenceProducerIdentityBuilder.MaximumMetadataBytes)
            throw new InvalidDataException("Receipt input identity exceeds its encoded-byte bound.");
        return receipt;
    }

    internal void Validate()
    {
        if (ComponentHashes.Count > EvidenceProducerIdentityBuilder.MaximumMaterialKeys
            || Files.Count > EvidenceProducerIdentityBuilder.MaximumFiles
            || ComponentHashes.Any(pair => !AllowedComponentNames.Contains(pair.Key) || pair.Key.Length > 128 || !IsHash(pair.Value))
            || Files.Any(file => !IsHash(file.Sha256) || !IsValidMode(file.Mode)
                || file.LengthBytes < 0
                || !EvidenceProducerIdentityBuilder.IsValidInputRelativePath(file.RelativePath))
            || Files.Select(static file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != Files.Count)
            throw new InvalidDataException("Receipt input identity is invalid or exceeds its bound.");
        if (EvidenceCanonicalJson.Serialize(this).Length > EvidenceProducerIdentityBuilder.MaximumMetadataBytes)
            throw new InvalidDataException("Receipt input identity exceeds its encoded-byte bound.");
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
/// <param name="ComponentHashes">Optional allowlisted component names and SHA-256 digests; raw component values are never stored.</param>
/// <param name="Files">Optional hash-only input identity; exact relative paths are retained for explain output, while file contents and raw tool or environment values are never stored.</param>
internal sealed record EvidenceReceiptPublicationRequest(EvidenceReceiptStore.EvidenceReceiptKeyLease Lease,
    string ProducerId, string SourceArtifactRoot, DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, string>? ComponentHashes = null, IReadOnlyList<EvidenceReceiptInputFile>? Files = null)
{
    /// <summary>Gets an optional complete hash-only identity; preferred over the legacy hash and file arguments.</summary>
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
