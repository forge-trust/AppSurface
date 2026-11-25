using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Identifies a side-effect-free explicit producer registration before resources exist.</summary>
internal sealed record EvidenceReuseProducerRegistration(IEvidenceProducer Producer, string ImplementationIdentity,
    IReadOnlyDictionary<string, string> EffectiveToolIdentity);

/// <summary>Allows a first-party adapter to preserve a valid fresh pass while refusing incomplete receipt custody.</summary>
/// <remarks>Ineligibility never waives a declared required artifact or a coverage gate. It only suppresses optimization publication.</remarks>
internal interface IEvidenceProducerReceiptEligibility
{
    /// <summary>Gets a bounded stable reason when this execution cannot provide its complete reusable artifact inventory.</summary>
    string? ReceiptIneligibilityReason { get; }
}

/// <summary>Configures one private reuse invocation; run-local paths and execution IDs never enter content keys.</summary>
/// <remarks>Captured patch memory is copied during preparation. An absent snapshot remains null; only supplied
/// bytes can establish a required patch identity. A supplied digest cannot manufacture missing bytes.</remarks>
internal sealed record EvidenceProducerReuseOptions(bool Enabled, string RepositoryRoot, string? StoreRoot,
    TimeSpan WaitTimeout, TimeSpan ProducerCleanupTimeout, bool VerifyReuse, bool ReadOnly,
    ReadOnlyMemory<byte>? CapturedPatch, TimeProvider Clock)
{
    /// <summary>Gets roots that must remain disjoint from the receipt coordination domain.</summary>
    internal IReadOnlyList<string> ForbiddenRoots { get; init; } = [];

    /// <summary>Gets the per-invocation environment reader; tests may model local and protected contexts without global mutation.</summary>
    internal Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Gets an optional bounded wait progress observer; values contain only a key prefix and durations.</summary>
    internal Action<string, TimeSpan, TimeSpan>? WaitProgress { get; init; }

    /// <summary>Gets an optional per-call fault seam immediately before each durable authority write; production omits it.</summary>
    internal Action<string>? BeforeAuthorityWriteForTests { get; init; }

    /// <summary>Gets an optional per-call fault seam before child-channel construction; production omits it.</summary>
    internal Action<string>? BeforeChildChannelStartForTests { get; init; }

    /// <summary>Gets an optional fault seam before settling a producer whose receipt publication was refused.</summary>
    /// <remarks>The store has already returned the per-producer publication outcomes when this runs.</remarks>
    internal Action<string>? BeforeRefusedPublicationSettlementForTests { get; init; }

    /// <summary>Checks the optional captured patch before any registration, store, or producer work.</summary>
    /// <param name="patch">The supplied patch, or null for a path-only plan.</param>
    /// <param name="parameterName">Name of the outer options argument so callers report the same diagnostic.</param>
    /// <exception cref="ArgumentOutOfRangeException">The patch exceeds the shared byte limit.</exception>
    internal static void ValidateCapturedPatch(ReadOnlyMemory<byte>? patch, string parameterName)
    {
        if (patch is { Length: > EvidencePlan.MaximumCapturedPatchBytes })
            throw new ArgumentOutOfRangeException(parameterName, "The captured patch exceeds the existing twenty-MiB bound.");
    }
}

/// <summary>Describes whether preparation can reuse, execute freshly, or safely execute nothing.</summary>
internal enum EvidenceProducerReuseStatus { Hit, Miss, Ineligible, Unavailable }

/// <summary>Records what happened to receipt custody independently of the diagnostic reason.</summary>
internal enum EvidenceReceiptPublication
{
    /// <summary>No current-run restoration or publication occurred.</summary>
    NotPublished,
    /// <summary>A verified receipt was restored into the current run.</summary>
    Restored,
    /// <summary>Fresh evidence was committed to the receipt store.</summary>
    Published,
    /// <summary>Receipt admission was withheld or publication failed.</summary>
    Suppressed,
}

/// <summary>Provides bounded diagnostics shared by CLI and prepared-host adapters.</summary>
internal sealed record EvidenceProducerReuseDecision(string ProducerId, EvidenceProducerReuseStatus Status, string Reason,
    string Diagnostic, string? ProducerKey = null, DateTimeOffset? OriginCompletedAt = null, long? OriginElapsedMilliseconds = null)
{
    /// <summary>Gets the current-run receipt effect. Read-only candidates leave this at <c>NotPublished</c>.</summary>
    internal EvidenceReceiptPublication Publication { get; init; } = Status == EvidenceProducerReuseStatus.Ineligible
        ? EvidenceReceiptPublication.Suppressed
        : EvidenceReceiptPublication.NotPublished;
}

/// <summary>Coordinates content preparation, native key ownership and whole-invocation receipt admission.</summary>
/// <remarks>
/// This private support layer owns actual key callbacks and native leases. Adapters own producer tasks and active
/// resource scopes, call BeforeCleanupAsync before disposing registrations, and pass the final cleaned manifest to
/// CompleteAsync. A bounded wait never releases an unsettled callback, child session, registration or key lease.
/// Explain uses the same identity resolver but never creates writers, coordination state, or execution channels.
/// </remarks>
internal sealed class EvidenceProducerReuseSession : IAsyncDisposable
{
    private enum AuditComparisonState
    {
        QuarantinedExpectation,
        UnmatchedVerifiedCandidate,
        MatchedVerifiedCandidate,
    }

    private sealed record AuditComparison
    {
        private readonly string[] _expectedAssertions;

        internal AuditComparison(IReadOnlyList<string> expectedAssertions, AuditComparisonState state)
        {
            _expectedAssertions = expectedAssertions.Order(StringComparer.Ordinal).ToArray();
            EncodedExpectation = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(_expectedAssertions));
            State = state;
        }

        internal string EncodedExpectation { get; }
        internal AuditComparisonState State { get; init; }

        internal static AuditComparison Recover(string? persistedExpectation, IReadOnlyList<string> fallback)
        {
            var expectedAssertions = persistedExpectation is null ? fallback
                : JsonSerializer.Deserialize<string[]>(persistedExpectation)
                    ?? throw new InvalidDataException("Audit assertion identity is invalid.");
            return new(expectedAssertions, AuditComparisonState.QuarantinedExpectation);
        }

        internal bool Matches(EvidenceProducerResult? result) =>
            result?.Outcome == EvidenceProducerOutcome.Passed
            && result.SatisfiedAssertionIds.Order(StringComparer.Ordinal).SequenceEqual(_expectedAssertions, StringComparer.Ordinal);
    }

    private const string PendingKeyCallbackDiagnostic =
        "The actual key callback is still active. No producer or resource may start; wait for ownership to settle before retrying.";
    private readonly EvidencePlan _plan;
    private readonly IReadOnlyDictionary<string, EvidenceReuseProducerRegistration> _registrations;
    private readonly IReadOnlyDictionary<string, string> _resourceIdentities;
    private readonly EvidenceProducerReuseOptions _options;
    private readonly Func<EvidenceProducerDeclaration, string> _artifactRoot;
    private readonly Dictionary<string, EvidenceProducerReuseDecision> _decisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceProducerIdentity> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceArtifactWriter> _writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceProducerResult> _reused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceReceiptStore.EvidenceReceiptKeyLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuditComparison> _auditComparisons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceChildLifetimeSession> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publicationSuppressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceKeyMaterialSnapshot> _lastMaterial = new(StringComparer.Ordinal);
    private readonly List<EvidenceProducerKeyMaterialExecution> _keyCallbacks = [];
    private readonly Stopwatch _waitBudget = new();
    private readonly object _disposeSync = new();
    private Task? _disposal;
    private bool _started;
    private bool _admissionCompleted;
    private bool _postExecutionChecked;
    private bool _childrenSettled = true;
    private string? _patchSha256;
    private long _preparationMilliseconds;
    private long _restoreMilliseconds;
    private EvidenceReceiptStore? _store;

    private EvidenceProducerReuseSession(EvidencePlan plan,
        IReadOnlyDictionary<string, EvidenceReuseProducerRegistration> registrations,
        IReadOnlyDictionary<string, string> resourceIdentities, EvidenceProducerReuseOptions options,
        Func<EvidenceProducerDeclaration, string> artifactRoot)
    {
        _plan = plan;
        _registrations = registrations;
        _resourceIdentities = resourceIdentities;
        // The nullable cast prevents an absent snapshot from using the null-array-to-empty-memory conversion.
        _options = options with { CapturedPatch = options.CapturedPatch is { } captured ? new ReadOnlyMemory<byte>(captured.ToArray()) : (ReadOnlyMemory<byte>?)null };
        _artifactRoot = artifactRoot;
    }

    /// <summary>Gets this invocation's opaque identity, used for ownership only, never for producer keys.</summary>
    internal string ExecutionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets immutable snapshots of current bounded decisions in producer order.</summary>
    internal IReadOnlyList<EvidenceProducerReuseDecision> Decisions => Array.AsReadOnly(_plan.Profile.Producers
        .Select(producer => _decisions[producer.Id]).ToArray());

    /// <summary>Gets framework-restored results; a producer callback cannot add provenance here.</summary>
    internal IReadOnlyDictionary<string, EvidenceProducerResult> ReusedResults => new ReadOnlyDictionary<string, EvidenceProducerResult>(_reused);

    /// <summary>Reports an actual matching fresh outcome/assertion comparison against a verified receipt.</summary>
    /// <param name="producerId">Opaque selected producer identifier.</param>
    /// <returns>True only after CompleteAsync compared a verified candidate. This proves neither cleanup nor publication;
    /// callers must still require the completed manifest and final receipt decision.</returns>
    internal bool HasMatchedVerifiedAudit(string producerId) => _auditComparisons.TryGetValue(producerId, out var audit)
        && audit.State == AuditComparisonState.MatchedVerifiedCandidate;

    /// <summary>Gets whether safe ownership could not be established; adapters must start no resources or producers.</summary>
    internal bool IsUnavailable { get; private set; }

    /// <summary>Gets whether caller cancellation interrupted preparation or admission.</summary>
    internal bool IsCancelled { get; private set; }

    /// <summary>Gets whether every admitted child confirmed completion; a disconnected handler is not settlement.</summary>
    /// <remarks>Prepared hosts retain producer and resource owners while this is false, even after handlers join.</remarks>
    internal bool AreChildrenSettled => _children.Values.All(static child => child.IsSettled && !child.IsUnknown);

    private bool AreKeyCallbacksSettled => _keyCallbacks.All(static callback => callback.IsSettled);

    /// <summary>Prepares content keys and verified receipts before computing fresh resource closure.</summary>
    internal static async Task<EvidenceProducerReuseSession> PrepareAsync(EvidencePlan plan,
        IReadOnlyDictionary<string, EvidenceReuseProducerRegistration> registrations,
        IReadOnlyDictionary<string, string> resourceIdentities, EvidenceProducerReuseOptions options,
        Func<EvidenceProducerDeclaration, string> artifactRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(resourceIdentities);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Clock);
        ArgumentNullException.ThrowIfNull(artifactRoot);
        if (options.VerifyReuse && !options.Enabled)
            throw new ArgumentException("reuse.verify_requires_reuse: Enable reuse before requesting an audit.", nameof(options));
        if (!EvidenceExecutionTimeouts.IsValidReceiptWait(options.WaitTimeout))
            throw new ArgumentOutOfRangeException(nameof(options), "Receipt wait timeout must be finite and positive.");
        EvidenceProducerReuseOptions.ValidateCapturedPatch(options.CapturedPatch, nameof(options));
        if (options.Enabled && EvidenceReuseContext.IsProtected(plan.Profile.Scope, options.GetEnvironmentVariable))
            throw new InvalidOperationException("reuse.local_only: Receipt reuse cannot supply release or protected CI evidence. Run fresh without reuse.");

        var session = new EvidenceProducerReuseSession(plan, registrations, resourceIdentities, options, artifactRoot);
        var timer = Stopwatch.StartNew();
        try
        {
            await session.PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.IsCancelled = true;
            session.Unavailable("reuse.cancelled", "Preparation was cancelled. Wait for owned work to settle before retrying.");
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            session.Unavailable("reuse.coordination_unavailable",
                $"Safe receipt preparation failed ({exception.GetType().Name}; contract {plan.ContractVersion}). Inspect the private store and retry after its owners settle; never delete unresolved authority."
                + (exception is PlatformNotSupportedException ? " Durable local storage requires directory synchronization on Linux/macOS or NTFS write-through metadata on Windows; choose a supported private store or run fresh without reuse." : string.Empty)
                + SafeUnexpectedStack(exception));
        }
        catch
        {
            var cleanup = session.DisposeAsync().AsTask();
            _ = cleanup.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        finally
        {
            session._preparationMilliseconds = Math.Max(0, timer.ElapsedMilliseconds - session._waitBudget.ElapsedMilliseconds - session._restoreMilliseconds);
        }
        return session;
    }

    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        foreach (var producer in _plan.Profile.Producers)
        {
            _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Ineligible, "reuse.disabled", "Reuse is disabled; execute fresh.");
            if (!_registrations.ContainsKey(producer.Id))
                throw new InvalidOperationException("Every selected producer requires an explicit preparation registration.");
        }
        if (!_options.Enabled) return;
        _patchSha256 = _options.CapturedPatch is { } captured ? EvidenceDigest.Sha256(captured.Span) : null;

        foreach (var producer in _plan.Profile.Producers)
        {
            var (probe, callbackPending) = await ProbeIdentityAsync(producer, cancellationToken).ConfigureAwait(false);
            RecordPreparationDecision(producer.Id, probe, callbackPending);
            if (IsUnavailable) return;
            if (probe.Identity is { } identity) _identities.Add(producer.Id, identity);
        }
        if (_identities.Count == 0) return;
        var namespaces = _identities.Values.Select(static identity => identity.RepositoryNamespace).Distinct(StringComparer.Ordinal).ToArray();
        if (namespaces.Length != 1) throw new InvalidOperationException("Producer repositories changed during preparation.");
        if (_options.ReadOnly)
        {
            var readOnly = EvidenceReceiptStore.OpenReadOnly(StoreOptions(namespaces[0], _options.WaitTimeout));
            foreach (var pair in _identities)
            {
                var candidate = readOnly.Peek(pair.Value.Key);
                if (candidate.Outcome == EvidenceReceiptReadOutcome.Miss)
                {
                    _decisions[pair.Key] = candidate.State?.IsQuarantined == true
                        ? new(pair.Key, EvidenceProducerReuseStatus.Miss, "reuse.audit-quarantined",
                            "The key is quarantined. Run with --reuse and --verify-reuse to recover after a complete successful fresh audit.", pair.Value.Key)
                        : DescribeMiss(readOnly, pair.Key, pair.Value, candidate.Reason);
                    continue;
                }
                _decisions[pair.Key] = new(pair.Key,
                    candidate.IsCandidate ? EvidenceProducerReuseStatus.Hit : EvidenceProducerReuseStatus.Unavailable,
                    "reuse." + candidate.Reason,
                    candidate.IsCandidate ? "Candidate receipt found; run will acquire ownership and revalidate all bytes." : "No admissible candidate receipt; run will revalidate before execution.",
                    pair.Value.Key, candidate.OriginCompletedAt ?? candidate.State?.OriginCompletedAt, candidate.OriginElapsedMilliseconds);
            }
            return;
        }

        if (!await AcquireStableKeysAsync(namespaces[0], cancellationToken).ConfigureAwait(false)) return;

        foreach (var producer in _plan.Profile.Producers.Where(producer => _identities.ContainsKey(producer.Id)))
        {
            var identity = _identities[producer.Id];
            var lease = _leases[identity.Key];
            var timer = new Stopwatch();
            EvidenceArtifactWriter? writer = null;
            var read = await lease.ReadReceiptForPreparationAsync(producer, CreateWriter, cancellationToken).ConfigureAwait(false);
            _restoreMilliseconds += timer.ElapsedMilliseconds;
            if (read.IsUnavailable)
            {
                if (read.State?.InFlightExecutionId is not null)
                    Unavailable("reuse.unresolved_execution", "A prior execution has unknown child settlement. Stop and verify the old environment before selecting a fresh store; never steal or erase its authority.");
                else
                    Unavailable("reuse." + read.Reason, "Receipt ownership is unsafe. Resolve the named store condition before retrying; no work was admitted.");
                return;
            }
            if (read.State is { IsQuarantined: true } state)
            {
                _quarantined.Add(producer.Id);
                if (_options.VerifyReuse)
                    _auditComparisons[producer.Id] = AuditComparison.Recover(state.AuditExpectedAssertions, producer.AssertionIds);
                _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Miss, "reuse.audit_required",
                    "The key is quarantined. Ordinary fresh execution cannot publish; use an explicit successful fresh audit to recover.", identity.Key);
                continue;
            }
            if (read is { Outcome: EvidenceReceiptReadOutcome.Restored, Result: { } restored })
            {
                if (_options.VerifyReuse)
                {
                    _auditComparisons[producer.Id] = new(
                        restored.SatisfiedAssertionIds,
                        AuditComparisonState.UnmatchedVerifiedCandidate);
                }
                else _reused.Add(producer.Id, restored);
                _decisions[producer.Id] = new(producer.Id, _options.VerifyReuse ? EvidenceProducerReuseStatus.Miss : EvidenceProducerReuseStatus.Hit,
                    _options.VerifyReuse ? "reuse.audit_requested" : "reuse.hit",
                    _options.VerifyReuse ? "Run freshly and compare outcome and assertions with the verified origin." : "Verified complete local receipt; no producer execution is needed.",
                    identity.Key, read.OriginCompletedAt, read.OriginElapsedMilliseconds)
                {
                    Publication = _options.VerifyReuse ? EvidenceReceiptPublication.NotPublished : EvidenceReceiptPublication.Restored,
                };
            }
            else
            {
                if (!_options.VerifyReuse && writer is { WrittenArtifacts.Count: > 0 })
                    await ResetFailedRestoreAsync(producer, writer, cancellationToken).ConfigureAwait(false);
                _decisions[producer.Id] = DescribeMiss(_store!, producer.Id, identity, read.Reason);
            }

            EvidenceArtifactWriter CreateWriter()
            {
                timer.Start();
                // Audit verification uses separate owned custody so fresh execution never overwrites restored files.
                return writer = _options.VerifyReuse
                    ? new EvidenceArtifactWriter(producer, Path.Join(_artifactRoot(producer), ".reuse-audit", ExecutionId),
                        requireUnlinkedPaths: true)
                    : GetWriter(producer);
            }
        }
    }

    /// <summary>Acquires receipt key leases and revalidates the identities before restoration.</summary>
    /// <param name="repositoryNamespace">The common repository namespace from initial identity preparation.</param>
    /// <param name="cancellationToken">Cancels acquisition and identity revalidation.</param>
    /// <returns>True when the remaining identities are stable under the held leases.</returns>
    /// <remarks>Both acquisition attempts share one wait budget. If a callback is still active, the session
    /// retains its leases through disposal and restoration must stop.</remarks>
    private async Task<bool> AcquireStableKeysAsync(string repositoryNamespace, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var remaining = _options.WaitTimeout - _waitBudget.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The cumulative receipt wait budget expired.");
            var store = EvidenceReceiptStore.Open(StoreOptions(repositoryNamespace, remaining));
            _store = store;
            _waitBudget.Start();
            IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease> acquired;
            try
            {
                acquired = await store.AcquireAsync(_identities.Values.Select(static identity => identity.Key), cancellationToken,
                    (prefix, _, _) => _options.WaitProgress?.Invoke(prefix, _waitBudget.Elapsed,
                        _options.WaitTimeout > _waitBudget.Elapsed ? _options.WaitTimeout - _waitBudget.Elapsed : TimeSpan.Zero)).ConfigureAwait(false);
            }
            finally { _waitBudget.Stop(); }
            foreach (var lease in acquired) _leases.Add(lease.Key, lease);
            var changed = false;
            foreach (var producer in _plan.Profile.Producers.Where(producer => _identities.ContainsKey(producer.Id)))
            {
                var prior = _identities[producer.Id];
                var (probe, callbackPending) = await ProbeIdentityAsync(producer, cancellationToken).ConfigureAwait(false);
                RecordPreparationDecision(producer.Id, probe, callbackPending);
                if (IsUnavailable) return false; // Actual late callback and key leases remain owned until disposal joins it.
                var current = probe.Identity;
                if (current is not null && current.RepositoryNamespace != prior.RepositoryNamespace)
                {
                    Unavailable("reuse.repository_changed", "The physical repository identity changed while waiting. Prepare from a stable verified checkout before retrying.");
                    return false;
                }
                if (current is null || current.Key != prior.Key)
                {
                    changed = true;
                    if (current is null) _identities.Remove(producer.Id);
                    else _identities[producer.Id] = current;
                }
            }
            if (!changed) break;
            ReleaseLeases();
            if (attempt == 1)
            {
                Unavailable("reuse.inputs_unstable", "Inputs changed repeatedly while waiting. Stop edits, prepare a new run and retry.");
                return false;
            }
        }
        _waitBudget.Stop();
        return true;
    }

    private EvidenceReceiptStoreOptions StoreOptions(string repositoryNamespace, TimeSpan waitTimeout) => new()
    {
        RootDirectory = _options.StoreRoot,
        RepositoryNamespace = repositoryNamespace,
        WaitTimeout = waitTimeout,
        Clock = _options.Clock,
        ForbiddenRoots = _options.ForbiddenRoots.Concat(_identities.Values.SelectMany(identity => identity.Files)
            .Select(file => Path.Join(_options.RepositoryRoot, file.RelativePath))).Distinct(StringComparer.Ordinal).ToArray(),
    };

    private static EvidenceProducerReuseDecision DescribeMiss(EvidenceReceiptStore store, string producerId,
        EvidenceProducerIdentity identity, string fallbackReason)
    {
        var comparable = store.FindComparable(producerId, identity);
        if (comparable.FirstDifferingPath is { } path)
            return new(producerId, EvidenceProducerReuseStatus.Miss, "reuse.inputs_changed",
                $"Input {JsonSerializer.Serialize(path)} differs from the most recent comparable origin; execute fresh.", identity.Key, comparable.OriginCompletedAt);
        var reason = comparable.DifferingComponent switch
        {
            EvidenceProducerIdentityComponents.Patch => "reuse.diff_changed",
            EvidenceProducerIdentityComponents.Tools or EvidenceProducerIdentityComponents.Implementation => "reuse.tool_changed",
            not null => "reuse.configuration_changed",
            _ => "reuse." + (comparable.Found ? fallbackReason : comparable.Reason),
        };
        return new(producerId, EvidenceProducerReuseStatus.Miss, reason,
            comparable.DifferingComponent is null ? "No usable complete comparable receipt; execute fresh before publishing a replacement."
                : $"The {comparable.DifferingComponent} identity changed; execute fresh with the current effective configuration.", identity.Key, comparable.OriginCompletedAt);
    }

    /// <summary>Captures current key material and identity without changing the producer's receipt decision.</summary>
    /// <remarks>Reports an unjoined callback separately so the calling phase can block the session while retaining ownership.</remarks>
    private async Task<(EvidenceProducerIdentityResult Result, bool CallbackPending)> ProbeIdentityAsync(
        EvidenceProducerDeclaration producer, CancellationToken cancellationToken)
    {
        if (producer.Inputs is null)
        {
            return (new(null, "reuse.inputs_required", "No Inputs declaration; execute fresh."), false);
        }
        if (string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
            && producer.CoverageExecution is not { CooperativeChildLifetime: true })
        {
            return (new(null, "reuse.child_lifetime_required",
                "Coverage reuse requires the explicit cooperative child-lifetime contract. Run fresh or enroll and join every test child before opting in."), false);
        }
        var material = EvidenceKeyMaterialSnapshot.NoMaterial;
        var registration = _registrations[producer.Id];
        if (registration.Producer is IEvidenceProducerKeyMaterial callback)
        {
            var execution = EvidenceProducerKeyMaterialExecution.Start(callback,
                new EvidenceProducerKeyMaterialContext(_plan, producer, _options.Clock), _options.ProducerCleanupTimeout, cancellationToken);
            _keyCallbacks.Add(execution);
            var result = await execution.WaitAsync().ConfigureAwait(false);
            if (result.CallerCancelled) IsCancelled = true;
            if (!result.Settled)
            {
                return (new(null, result.Reason, PendingKeyCallbackDiagnostic), true);
            }
            if (result.Material is null)
            {
                return (new(null, result.Reason,
                    "Key material failed or exceeded its deadline and has settled. Execute fresh; fix the local side-effect-free callback before retrying reuse."
                    + (result.Diagnostic is null ? string.Empty : " " + result.Diagnostic)), false);
            }
            material = result.Material;
        }
        _lastMaterial[producer.Id] = material;
        cancellationToken.ThrowIfCancellationRequested();
        return (await BuildIdentityAsync(producer, cancellationToken).ConfigureAwait(false), false);
    }

    /// <summary>Rebuilds the identity from disk and the last material captured while the producer was owned.</summary>
    private Task<EvidenceProducerIdentityResult> BuildIdentityAsync(EvidenceProducerDeclaration producer, CancellationToken cancellationToken)
    {
        var registration = _registrations[producer.Id];
        return EvidenceProducerIdentityBuilder.BuildAsync(new(_options.RepositoryRoot, _plan, producer,
            registration.ImplementationIdentity, _resourceIdentities, registration.EffectiveToolIdentity, _lastMaterial[producer.Id], _patchSha256), cancellationToken);
    }

    /// <summary>Records the preparation decision before receipt lookup or after the key becomes ineligible.</summary>
    private void RecordPreparationDecision(string producerId, EvidenceProducerIdentityResult probe, bool callbackPending)
    {
        if (callbackPending)
        {
            Unavailable(probe.Reason, PendingKeyCallbackDiagnostic);
            return;
        }

        _decisions[producerId] = probe.Identity is { } identity
            ? new(producerId, EvidenceProducerReuseStatus.Miss, "reuse.key_ready", "Content key prepared; receipt lookup follows ownership acquisition.", identity.Key)
            : new(producerId, EvidenceProducerReuseStatus.Ineligible, probe.Reason, probe.Diagnostic ?? "Identity is incomplete; execute fresh.");
    }

    /// <summary>Gets the same bounded writer for a fresh producer and its later receipt publication.</summary>
    internal EvidenceArtifactWriter GetWriter(EvidenceProducerDeclaration producer)
    {
        if (_options.ReadOnly) throw new InvalidOperationException("Explain cannot create artifact writers.");
        if (!_writers.TryGetValue(producer.Id, out var writer))
            _writers.Add(producer.Id, writer = new EvidenceArtifactWriter(producer, _artifactRoot(producer),
                requireUnlinkedPaths: _options.Enabled));
        return writer;
    }

    /// <summary>Gets the invocation-private cooperative channel only after execution authority is durable.</summary>
    internal EvidenceChildLifetimeSession? GetChildLifetime(string producerId) => _children.GetValueOrDefault(producerId);

    /// <summary>Rechecks prepared inputs and establishes every required durable authority before any side effect.</summary>
    internal async Task<bool> BeginExecutionAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("Prepared reuse sessions execute exactly once.");
        _started = true;
        if (IsUnavailable || IsCancelled || _options.ReadOnly) return false;
        if (!_options.Enabled) return true;
        try
        {
            foreach (var producer in _plan.Profile.Producers.Where(producer => _identities.ContainsKey(producer.Id)))
            {
                var prepared = _identities[producer.Id];
                var (probe, _) = await ProbeIdentityAsync(producer, cancellationToken).ConfigureAwait(false);
                if (!prepared.HasSameReceiptKey(probe.Identity) || IsUnavailable)
                {
                    Unavailable("reuse.prepared_inputs_changed", "Prepared repository namespace or inputs changed before execution. Start a new preparation; no resource or producer was started.");
                    return false;
                }
            }
            foreach (var producer in _plan.Profile.Producers.Where(producer => _identities.ContainsKey(producer.Id) && !_reused.ContainsKey(producer.Id)))
            {
                var lease = _leases[_identities[producer.Id].Key];
                var comparison = _auditComparisons.GetValueOrDefault(producer.Id);
                cancellationToken.ThrowIfCancellationRequested();
                _options.BeforeAuthorityWriteForTests?.Invoke(producer.Id);
                cancellationToken.ThrowIfCancellationRequested();
                lease.EstablishAuthority(ExecutionId, comparison is not null, allowQuarantinedFresh: _quarantined.Contains(producer.Id),
                    expectedAssertions: comparison?.EncodedExpectation);
                _admitted.Add(producer.Id);
            }
            // Channel creation is an execution side effect, so it follows admission of the complete key set.
            foreach (var producer in _plan.Profile.Producers.Where(producer => _admitted.Contains(producer.Id)
                && string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)))
            {
                _options.BeforeChildChannelStartForTests?.Invoke(producer.Id);
                var child = EvidenceChildLifetimeSession.Start(ExecutionId, _options.Clock);
                _children.Add(producer.Id, child);
                if (!child.CanAcceptChildren) throw new IOException("The cooperative child channel could not be established.");
            }
            _admissionCompleted = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsCancelled = true;
            Unavailable("reuse.cancelled", "Execution admission was cancelled before producer startup.");
            await RollBackUnstartedAdmissionAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            Unavailable("reuse.authority_unavailable", $"Durable execution authority could not be established ({exception.GetType().Name}). No producer or resource was started; inspect private store capacity and existing owners before retrying.");
            await RollBackUnstartedAdmissionAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Joins admission-only channels and clears this execution's partial authority before any workload can start.</summary>
    /// <remarks>
    /// Every key is inspected because a write can become visible before a durability error is reported. Never clear
    /// another execution's marker, audit intent, or a session with unknown admitted work. A failed store write remains
    /// conservative and is retried during disposal while the same key leases are still held.
    /// </remarks>
    private async Task RollBackUnstartedAdmissionAsync()
    {
        if (!await JoinActualTasksAsync().ConfigureAwait(false)) return;
        foreach (var lease in _leases.Values)
            TrySettleKnownExecution(lease);
    }

    /// <summary>Closes child enrollment and rechecks key material after actual producers finish and before disposal.</summary>
    /// <returns>False while a key callback or admitted child remains active; registrations remain owned until their actual tasks join.</returns>
    internal async Task<bool> BeforeCleanupAsync(bool actualProducerTasksSettled, CancellationToken cancellationToken)
    {
        if (!actualProducerTasksSettled) return false;
        if ((IsUnavailable && _started && !_admissionCompleted) || _postExecutionChecked)
            return AreKeyCallbacksSettled && AreChildrenSettled;
        _postExecutionChecked = true;
        // An unsettled key callback may return early and delay the actual join. Enrollment must still close.
        foreach (var session in _children.Values) session.CloseEnrollment();
        foreach (var producer in _plan.Profile.Producers.Where(producer => _admitted.Contains(producer.Id)))
        {
            if (_registrations[producer.Id].Producer is IEvidenceProducerReceiptEligibility { ReceiptIneligibilityReason: { } reason })
            {
                _publicationSuppressed.Add(producer.Id);
                _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Ineligible,
                    reason.StartsWith("reuse.", StringComparison.Ordinal) ? reason : "reuse." + reason,
                    "The fresh producer passed its execution contract but did not provide a complete bounded receipt inventory. Original reports are preserved; no receipt is published.", _identities[producer.Id].Key);
            }
            var before = _identities[producer.Id];
            EvidenceProducerIdentity? after = null;
            if (!cancellationToken.IsCancellationRequested)
            {
                var (probe, callbackPending) = await ProbeIdentityAsync(producer, cancellationToken).ConfigureAwait(false);
                if (callbackPending) Unavailable(probe.Reason, PendingKeyCallbackDiagnostic);
                after = probe.Identity;
            }
            if (!before.HasSameReceiptKey(after))
            {
                _publicationSuppressed.Add(producer.Id);
                var path = after is null ? null : EvidenceProducerIdentityBuilder.FirstDifferingPath(before.Files, after.Files);
                _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Ineligible, "reuse.inputs_changed",
                    path is null ? "Repository namespace, inputs, or effective key material changed during execution; receipt publication is suppressed. Pin restore inputs and retry."
                    : $"Input '{path}' changed during execution; receipt publication is suppressed. Pin this input and retry.", before.Key);
            }
            if (!AreKeyCallbacksSettled) return false;
        }
        foreach (var session in _children.Values)
            _childrenSettled &= await session.WaitForSettlementAsync(_options.ProducerCleanupTimeout, CancellationToken.None).ConfigureAwait(false);
        return _childrenSettled;
    }

    /// <summary>Evaluates audits and publishes only after complete successful current execution and cleanup.</summary>
    internal async Task<EvidenceManifest> CompleteAsync(EvidenceManifest manifest, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return manifest;
        if (_plan.ContractVersion == "1.0" && _identities.Count == 0) return manifest;
        var results = manifest.ProducerResults.ToArray();
        var auditMismatch = ApplyAuditComparisons(results);
        var metrics = manifest.Metrics with
        {
            PreparationMilliseconds = _preparationMilliseconds,
            ReceiptWaitMilliseconds = _waitBudget.ElapsedMilliseconds,
            ArtifactRestoreMilliseconds = _restoreMilliseconds,
            CleanupCompleted = manifest.Metrics.CleanupCompleted && _childrenSettled && !IsUnavailable,
            CleanupDiagnostic = !_childrenSettled ? "reuse.child_settlement_unknown: Cooperative child work did not settle. Preserve the store authority; stop and verify the old environment before choosing a fresh domain." : manifest.Metrics.CleanupDiagnostic,
        };
        var completed = EvidenceManifestBuilder.Build(_plan, results,
            manifest.ClaimKind == EvidenceClaimKind.ObservationOnly, manifest.EnvelopeStatus, manifest.ResourceResults, metrics);
        if (completed.ExecutionVerdict != EvidenceExecutionVerdict.Passed || auditMismatch)
        {
            CompleteUnsuccessfulSettlement(metrics.CleanupCompleted);
            return completed;
        }

        if (await PublishReceiptsAsync(completed, cancellationToken).ConfigureAwait(false))
            completed = EvidenceManifestBuilder.Build(_plan, results.Select(result => _auditComparisons.ContainsKey(result.ProducerId)
                ? result with { Outcome = EvidenceProducerOutcome.Failed, Diagnostic = "reuse.audit_finalization_failed: Fresh audit could not atomically finalize its receipt and clear intent. The key remains blocked; inspect store health and retry an explicit audit." }
                : result).ToArray(), observationOnly: manifest.ClaimKind == EvidenceClaimKind.ObservationOnly,
                envelopeStatus: manifest.EnvelopeStatus, resourceResults: manifest.ResourceResults, metrics: metrics);
        return completed;
    }

    private bool ApplyAuditComparisons(EvidenceProducerResult[] results)
    {
        var hasMismatch = false;
        foreach (var pair in _auditComparisons)
        {
            var result = results.SingleOrDefault(result => result.ProducerId == pair.Key);
            if (pair.Value.Matches(result))
            {
                if (pair.Value.State == AuditComparisonState.UnmatchedVerifiedCandidate)
                    _auditComparisons[pair.Key] = pair.Value with { State = AuditComparisonState.MatchedVerifiedCandidate };
                continue;
            }
            hasMismatch = true;
            try { _leases[_identities[pair.Key].Key].MarkAuditMismatch(ExecutionId, pair.Value.EncodedExpectation); }
            catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception)) { /* Durable pre-execution intent remains authoritative. */ }
            if (result is not null)
                results[Array.IndexOf(results, result)] = result with
                {
                    Outcome = EvidenceProducerOutcome.Failed,
                    Diagnostic = "reuse.audit_mismatch: Fresh outcome or assertions differ from the origin. The key remains quarantined; fix the workload's declared inputs and rerun an explicit audit."
                };
        }
        return hasMismatch;
    }

    // Returns whether an audited producer could not finalize its receipt. Ordinary refusals only affect its reuse decision.
    private async Task<bool> PublishReceiptsAsync(EvidenceManifest completed, CancellationToken cancellationToken)
    {
        var (publicationRequests, auditFailed) = await PreparePublicationRequestsAsync(cancellationToken).ConfigureAwait(false);
        if (auditFailed)
        {
            CompleteUnsuccessfulSettlement(actualCleanupCompleted: true);
            return true;
        }

        return publicationRequests.Count > 0
            && await PublishReceiptBatchAsync(completed, publicationRequests, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<EvidenceReceiptPublicationRequest> Requests, bool AuditFailed)> PreparePublicationRequestsAsync(
        CancellationToken cancellationToken)
    {
        var publicationRequests = new List<EvidenceReceiptPublicationRequest>();
        var auditFailed = false;
        foreach (var producer in _plan.Profile.Producers.Where(producer => _admitted.Contains(producer.Id)))
        {
            var preparedIdentity = _identities[producer.Id];
            var lease = _leases[preparedIdentity.Key];
            if (_publicationSuppressed.Contains(producer.Id) || _quarantined.Contains(producer.Id) && !_auditComparisons.ContainsKey(producer.Id))
            {
                RecordPublicationRefusal(producer.Id, ref auditFailed);
                TrySettleKnownExecution(lease);
                continue;
            }
            try
            {
                // Resource disposal can also rewrite declared files. Recheck disk bytes after all cleanup;
                // key material was captured after producer completion while its registration was still alive.
                var identity = await BuildIdentityAsync(producer, cancellationToken).ConfigureAwait(false);
                if (!preparedIdentity.HasSameReceiptKey(identity.Identity))
                {
                    _publicationSuppressed.Add(producer.Id);
                    _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Ineligible, "reuse.inputs_changed",
                        "Repository namespace or prepared input identity changed during execution or cleanup; the fresh result is preserved and no receipt is published.", preparedIdentity.Key);
                    RecordPublicationRefusal(producer.Id, ref auditFailed);
                    SettleKnownExecution(lease);
                    continue;
                }
                publicationRequests.Add(new(lease, producer.Id, _artifactRoot(producer), _options.Clock.GetUtcNow())
                { Identity = preparedIdentity });
            }
            catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
            {
                _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Miss, "reuse.publication_suppressed",
                    $"Fresh result passed; publication failed ({exception.GetType().Name}). Preserve unresolved authority and retry only after verifying the old environment.", preparedIdentity.Key)
                {
                    Publication = EvidenceReceiptPublication.Suppressed,
                };
                RecordPublicationRefusal(producer.Id, ref auditFailed);
                TrySettleKnownExecution(lease);
            }
        }
        return (publicationRequests, auditFailed);
    }

    private async Task<bool> PublishReceiptBatchAsync(EvidenceManifest completed,
        IReadOnlyList<EvidenceReceiptPublicationRequest> publicationRequests, CancellationToken cancellationToken)
    {
        var auditFailed = false;
        IReadOnlyDictionary<string, EvidenceReceiptPublicationResult> publications;
        try
        {
            publications = await _store!.PublishInvocationAsync(ExecutionId, _plan, completed, publicationRequests, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            foreach (var request in publicationRequests)
            {
                _decisions[request.ProducerId] = new(request.ProducerId, EvidenceProducerReuseStatus.Miss, "reuse.publication_suppressed",
                    $"Fresh execution passed; invocation publication failed ({exception.GetType().Name}). Preserve unresolved authority and inspect the private store before retrying.", _identities[request.ProducerId].Key)
                {
                    Publication = EvidenceReceiptPublication.Suppressed,
                };
                RecordPublicationRefusal(request.ProducerId, ref auditFailed);
                TrySettleKnownExecution(request.Lease);
            }
            return auditFailed;
        }

        foreach (var request in publicationRequests)
        {
            var publication = publications[request.ProducerId];
            var decision = new EvidenceProducerReuseDecision(request.ProducerId, EvidenceProducerReuseStatus.Miss, "reuse." + publication.Reason,
                publication.Published ? "Complete fresh receipt published after the whole invocation settled and cleaned."
                    : "Fresh result passed; receipt publication was suppressed. Inspect store capacity and retry when safe.", request.Lease.Key)
            {
                Publication = publication.Published ? EvidenceReceiptPublication.Published : EvidenceReceiptPublication.Suppressed,
            };
            if (!publication.Published)
            {
                RecordPublicationRefusal(request.ProducerId, ref auditFailed);
                try
                {
                    _options.BeforeRefusedPublicationSettlementForTests?.Invoke(request.ProducerId);
                    SettleKnownExecution(request.Lease);
                }
                catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
                {
                    decision = decision with
                    {
                        Diagnostic = "Fresh result passed; receipt publication was suppressed. Settlement of this key's execution authority could not be confirmed. Inspect the store and verify the previous execution before retrying."
                    };
                }
            }
            _decisions[request.ProducerId] = decision;
        }
        return auditFailed;
    }

    private void RecordPublicationRefusal(string producerId, ref bool auditFailed)
        => auditFailed |= _auditComparisons.ContainsKey(producerId);

    /// <summary>Joins actual callback and enrolled-child work; an unknown disconnected child never becomes proof.</summary>
    internal async Task<bool> JoinActualTasksAsync()
    {
        Exception? failure = null;
        try { await JoinKeyMaterialAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        foreach (var child in _children.Values)
        {
            try { await child.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return AreChildrenSettled;
    }

    /// <summary>Joins key callbacks before an adapter disposes the producer registrations they can still access.</summary>
    internal async Task JoinKeyMaterialAsync()
    {
        Exception? failure = null;
        foreach (var callback in _keyCallbacks)
        {
            try { await callback.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Clears only fully settled unsuccessful execution ownership, retaining audit intent and quarantine.</summary>
    /// <param name="actualCleanupCompleted">Whether the caller confirmed that actual owned cleanup completed.</param>
    /// <remarks>Call after attempting to join owned work. This method synchronously updates receipt authority under the held key leases.</remarks>
    internal void CompleteUnsuccessfulSettlement(bool actualCleanupCompleted)
    {
        if (!actualCleanupCompleted || !AreChildrenSettled) return;
        foreach (var id in _admitted)
        {
            var lease = _leases[_identities[id].Key];
            TrySettleKnownExecution(lease);
        }
    }

    private void TrySettleKnownExecution(EvidenceReceiptStore.EvidenceReceiptKeyLease lease)
    {
        try { SettleKnownExecution(lease); }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception)) { /* Unconfirmed settlement preserves authority. */ }
    }

    private void SettleKnownExecution(EvidenceReceiptStore.EvidenceReceiptKeyLease lease)
    {
        if (lease.ReadState()?.InFlightExecutionId == ExecutionId) lease.SettleExecution(ExecutionId);
    }

    private async Task ResetFailedRestoreAsync(EvidenceProducerDeclaration producer, EvidenceArtifactWriter writer, CancellationToken token)
    {
        if (!await writer.VerifyWrittenArtifactsAsync(token).ConfigureAwait(false))
            throw new IOException("A partially restored artifact changed before owned cleanup.");
        var rootPath = Path.GetFullPath(_artifactRoot(producer));
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in writer.WrittenArtifacts)
        {
            var path = Path.Join(rootPath, artifact.RelativePath);
            var parent = Path.GetDirectoryName(path)!;
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(parent, false, false)
                ?? throw new DirectoryNotFoundException();
            using var file = directory.OpenArtifactFile(Path.GetFileName(path), false, requirePrivate: false);
            var identity = CoverageRunOutputLease.GetArtifactObjectIdentity(file.SafeFileHandle);
            file.Dispose();
            directory.DeleteArtifactEntry(Path.GetFileName(path), 1, identity);
            while (!string.Equals(parent, rootPath, StringComparison.Ordinal))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent)!;
            }
        }
        foreach (var path in directories.OrderByDescending(static path => path.Length))
        {
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, false);
            if (directory is null || directory.EnumerateArtifactEntries(100_000).Count != 0) continue;
            var identity = directory.ArtifactDirectoryIdentity;
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(path)!, false, false)!;
            parent.DeleteArtifactEntry(Path.GetFileName(path), 1, identity);
        }
        _writers.Remove(producer.Id);
    }

    private void Unavailable(string reason, string diagnostic)
    {
        IsUnavailable = true;
        foreach (var producer in _plan.Profile.Producers)
            _decisions[producer.Id] = new(producer.Id, EvidenceProducerReuseStatus.Unavailable, reason, diagnostic,
                _identities.GetValueOrDefault(producer.Id)?.Key);
    }

    private void ReleaseLeases()
    {
        foreach (var lease in _leases.OrderByDescending(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Value)) lease.Dispose();
        _leases.Clear();
    }

    private static string SafeUnexpectedStack(Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or TimeoutException or JsonException) return string.Empty;
        var frames = new StackTrace(exception, false).GetFrames().Take(16).Select(static frame =>
        {
            var method = frame.GetMethod();
            return method is null ? "unknown frame" : $"{method.DeclaringType?.FullName}.{method.Name}";
        });
        return " Framework stack (messages and values omitted): " + string.Join(" <- ", frames);
    }

    /// <summary>Joins actual work before releasing key leases; concurrent disposal shares one cleanup task.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (IsUnavailable && _started && !_admissionCompleted) await RollBackUnstartedAdmissionAsync().ConfigureAwait(false);
            else await JoinActualTasksAsync().ConfigureAwait(false);
        }
        finally { ReleaseLeases(); }
    }
}
