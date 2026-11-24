using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>
/// Describes the lifecycle state of an explicit consumer-owned EvidenceHost instance.
/// </summary>
public enum EvidenceHostState
{
    /// <summary>The host has registrations but has not executed evidence.</summary>
    Created,

    /// <summary>The host is validating envelope, resource, and producer declarations.</summary>
    Validating,

    /// <summary>The host is waiting for its required disposable resources.</summary>
    WaitingForResources,

    /// <summary>The host is executing selected typed producers.</summary>
    Producing,

    /// <summary>The host is collecting the final immutable manifest.</summary>
    Collecting,

    /// <summary>The host is disposing producer and resource ownership.</summary>
    Cleaning,

    /// <summary>The host completed its single execution and cleanup.</summary>
    Completed,

    /// <summary>The host has been disposed without a reusable execution path.</summary>
    Disposed,
}

/// <summary>
/// Configures an explicit EvidenceHost lifecycle.
/// </summary>
/// <param name="RequireTrustedEnvelope">Whether every run requires an accepted envelope verifier result.</param>
/// <param name="ArtifactDirectory">Controlled root for producer artifacts. Defaults to <c>TestResults/evidence/artifacts</c>.</param>
public sealed record EvidenceHostOptions(
    bool RequireTrustedEnvelope = false,
    string? ArtifactDirectory = null)
{
    /// <summary>Gets the separate grace period for cancelled producers to finish before owned registrations are disposed.</summary>
    /// <remarks>Defaults to ten seconds. If a producer remains active, cleanup fails and its registrations are preserved.</remarks>
    public TimeSpan ProducerCleanupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets whether to prepare opt-in local producer receipts before starting resources.</summary>
    /// <remarks>Requires <c>EvidenceHostBootstrap.PrepareAsync</c>. Reused results are informational and cannot satisfy a CI gate.</remarks>
    public bool Reuse { get; init; }

    /// <summary>Gets whether every candidate receipt must be audited by a complete fresh execution.</summary>
    /// <remarks>Requires <see cref="Reuse"/>. A mismatch quarantines the producer key until a successful explicit audit.</remarks>
    public bool VerifyReuse { get; init; }

    /// <summary>Gets the repository root used to inventory declared inputs, defaulting to the repository containing the current directory.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Gets the optional local receipt store, taking precedence over APPSURFACE_EVIDENCE_RECEIPT_STORE and the per-user default.</summary>
    /// <remarks>A distinct store is a distinct coordination domain. Never switch stores while children from a prior execution remain active.</remarks>
    public string? ReceiptStore { get; init; }

    /// <summary>Gets the one cumulative positive native-lease wait budget, defaulting to thirty minutes.</summary>
    public TimeSpan ReceiptWaitTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets an optional observer for bounded native key acquisition progress.</summary>
    /// <remarks>The observer receives a key prefix and cumulative elapsed/remaining durations, never raw input values.</remarks>
    public IProgress<EvidenceReuseWaitProgress>? ReceiptWaitProgress { get; init; }

    /// <summary>Gets the exact captured patch bytes when the plan declares a patch coverage requirement.</summary>
    /// <remarks>Preparation copies supplied memory. Null means no snapshot was supplied and remains absent;
    /// it never becomes an empty snapshot. Reuse verifies a required patch's SHA-256 against the plan.
    /// A normalized changed-path list cannot replace these bytes.</remarks>
    public ReadOnlyMemory<byte>? CapturedPatch { get; init; }

    /// <summary>Gets the per-invocation environment reader used to verify protected contexts without global test mutation.</summary>
    internal Func<string, string?> EnvironmentReader { get; init; } = Environment.GetEnvironmentVariable;
}

/// <summary>
/// Describes a secret-safe execution-envelope validation result.
/// </summary>
/// <param name="Accepted">Whether the envelope is accepted for the requested claim scope.</param>
/// <param name="Attested">Whether the verifier has independent attestation; v1 normally returns <see langword="false"/>.</param>
/// <param name="Diagnostic">Secret-safe failure or explanatory detail.</param>
public sealed record EvidenceEnvelopeResult(bool Accepted, bool Attested, string? Diagnostic = null);

/// <summary>
/// Validates a CI-provided trusted execution envelope without exposing its secret values to a manifest.
/// </summary>
public interface IEvidenceExecutionEnvelopeVerifier
{
    /// <summary>
    /// Validates the caller's protected CI context for the resolved plan.
    /// </summary>
    /// <param name="plan">Resolved plan for the current execution.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>A constrained acceptance result.</returns>
    ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Represents an explicitly registered readiness probe for a plan resource.
/// </summary>
public interface IEvidenceResourceReadiness
{
    /// <summary>Gets the stable plan resource identifier handled by this probe.</summary>
    string Id { get; }

    /// <summary>
    /// Waits until the consumer-owned resource is ready or the supplied deadline cancels.
    /// </summary>
    /// <param name="cancellationToken">Deadline and caller cancellation.</param>
    /// <returns>A task that completes only when the resource is ready.</returns>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Collects the only resource, producer, and envelope registrations an EvidenceHost may use.
/// </summary>
public sealed class EvidenceHostRegistration
{
    private readonly Dictionary<string, IEvidenceResourceReadiness> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IEvidenceProducer> _producers = new(StringComparer.Ordinal);

    /// <summary>Gets explicitly registered resource readiness probes.</summary>
    public IReadOnlyDictionary<string, IEvidenceResourceReadiness> Resources => _resources;

    /// <summary>Gets explicitly registered typed producers.</summary>
    public IReadOnlyDictionary<string, IEvidenceProducer> Producers => _producers;

    /// <summary>Gets the optional trusted execution-envelope verifier.</summary>
    public IEvidenceExecutionEnvelopeVerifier? EnvelopeVerifier { get; private set; }

    /// <summary>
    /// Adds one explicitly named readiness probe.
    /// </summary>
    /// <param name="resource">Consumer-owned resource readiness probe.</param>
    public void AddResource(IEvidenceResourceReadiness resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!_resources.TryAdd(resource.Id, resource))
        {
            throw new InvalidOperationException($"Evidence resource '{resource.Id}' is already registered.");
        }
    }

    /// <summary>
    /// Adds one explicitly named typed producer.
    /// </summary>
    /// <param name="producer">Consumer-owned producer.</param>
    public void AddProducer(IEvidenceProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (!_producers.TryAdd(producer.Id, producer))
        {
            throw new InvalidOperationException($"Evidence producer '{producer.Id}' is already registered.");
        }
    }

    /// <summary>
    /// Sets the single trusted execution-envelope verifier used by this host.
    /// </summary>
    /// <param name="verifier">Base-owned verifier implementation.</param>
    public void SetEnvelopeVerifier(IEvidenceExecutionEnvelopeVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (EnvelopeVerifier is not null)
        {
            throw new InvalidOperationException("EvidenceHost accepts exactly one execution-envelope verifier.");
        }

        EnvelopeVerifier = verifier;
    }
}

/// <summary>
/// Runs a separate, consumer-owned EvidenceHost with explicit registrations and bounded cleanup.
/// </summary>
public sealed partial class EvidenceHostBootstrap : IAsyncDisposable
{
    private readonly EvidencePlan _plan;
    private readonly EvidenceHostRegistration _registration;
    private readonly EvidenceHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _artifactDirectory;
    private EvidenceResourceGraph? _resourceGraph;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly List<Task> _producerExecutions = [];
    private readonly List<Task> _readinessExecutions = [];
    private IReadOnlyDictionary<string, EvidenceProducerResult> _preparedResults = new Dictionary<string, EvidenceProducerResult>();
    private IReadOnlySet<string>? _requiredResourceIds;
    private EvidenceHostResourceScope? _resourceScope;
    private IReadOnlyDictionary<string, EvidenceResourceResult>? _preparedReadiness;
    private bool _cleaned;
    private string? _cleanupFailure;
    private Func<EvidenceProducerDeclaration, EvidenceArtifactWriter>? _preparedWriter;
    private Func<bool, CancellationToken, Task<bool>>? _beforePreparedCleanup;
    private EvidenceEnvelopeStatus? _preparedEnvelopeStatus;
    private Task<string?>? _preparedRegistrationCleanup;
    private CancellationToken _preparedCancellation;
    private string? _preparedExecutionId;
    private Func<string, IReadOnlyDictionary<string, string>?>? _preparedChildEnvironment;

    private EvidenceHostBootstrap(
        EvidencePlan plan,
        EvidenceHostRegistration registration,
        EvidenceHostOptions options,
        TimeProvider timeProvider,
        EvidenceResourceGraph? resourceGraph = null)
    {
        _plan = plan;
        _registration = registration;
        _options = options;
        _timeProvider = timeProvider;
        _resourceGraph = resourceGraph;
        _artifactDirectory = Path.GetFullPath(options.ArtifactDirectory ?? Path.Join("TestResults", "evidence", "artifacts"));
    }

    /// <summary>Gets the immutable plan owned by this host instance.</summary>
    public EvidencePlan Plan => _plan;

    /// <summary>Gets the current one-way EvidenceHost lifecycle state.</summary>
    public EvidenceHostState State { get; private set; } = EvidenceHostState.Created;

    /// <summary>
    /// Creates a host with registrations supplied only by explicit caller code.
    /// </summary>
    /// <param name="plan">Resolved immutable plan.</param>
    /// <param name="configure">Consumer-owned registration callback.</param>
    /// <param name="options">Optional lifecycle constraints.</param>
    /// <param name="timeProvider">Clock seam for deterministic tests and deadline handling.</param>
    /// <returns>A single-use EvidenceHost instance.</returns>
    public static EvidenceHostBootstrap Create(
        EvidencePlan plan,
        Action<EvidenceHostRegistration> configure,
        EvidenceHostOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configure);
        if (options is { Reuse: true } or { VerifyReuse: true })
            throw new EvidenceHostException("reuse.preparation_required", "Receipt reuse requires preparation before resource startup.", "Use EvidenceHostBootstrap.PrepareAsync and compose only its RequiredResourceIds in the resource factory.");
        if (options is not null && (options.ProducerCleanupTimeout <= TimeSpan.Zero || options.ProducerCleanupTimeout > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(options), "Producer cleanup timeout must be positive and at most one minute.");

        var registration = new EvidenceHostRegistration();
        configure(registration);
        return new EvidenceHostBootstrap(plan, registration, options ?? new EvidenceHostOptions(), timeProvider ?? TimeProvider.System);
    }

    /// <summary>Creates the execution half of a prepared run after receipt restoration and exact resource transfer.</summary>
    /// <param name="plan">Original complete plan, preserved for claims and digest binding.</param>
    /// <param name="registration">Validated producers and exact active readiness bindings.</param>
    /// <param name="options">Snapshot of prepared host options.</param>
    /// <param name="timeProvider">Preparation clock.</param>
    /// <param name="preparedResults">Framework-verified reused results, never supplied by producer callbacks.</param>
    /// <param name="requiredResourceIds">Immutable transitive closure of fresh producers' resources.</param>
    /// <param name="scope">Transferred application owner, or null for a resource-free execution.</param>
    /// <param name="resourceGraph">Validated graph shared with preparation.</param>
    /// <param name="writer">Coordinator-owned writer for each selected producer.</param>
    /// <param name="beforeCleanup">Post-execution preparation work that must settle before registration disposal.</param>
    /// <param name="envelopeStatus">Envelope result verified before resource startup.</param>
    /// <param name="executionId">Current opaque ownership ID.</param>
    /// <param name="childEnvironment">Private cooperative channel for an admitted producer, without global environment mutation.</param>
    /// <returns>The single-use execution host, owned by the prepared run.</returns>
    internal static EvidenceHostBootstrap CreatePreparedExecution(
        EvidencePlan plan,
        EvidenceHostRegistration registration,
        EvidenceHostOptions options,
        TimeProvider timeProvider,
        IReadOnlyDictionary<string, EvidenceProducerResult> preparedResults,
        IReadOnlySet<string> requiredResourceIds,
        EvidenceHostResourceScope? scope,
        EvidenceResourceGraph resourceGraph,
        Func<EvidenceProducerDeclaration, EvidenceArtifactWriter> writer,
        Func<bool, CancellationToken, Task<bool>> beforeCleanup,
        EvidenceEnvelopeStatus envelopeStatus,
        string executionId,
        Func<string, IReadOnlyDictionary<string, string>?> childEnvironment)
    {
        return new EvidenceHostBootstrap(plan, registration, options, timeProvider, resourceGraph)
        {
            _preparedResults = preparedResults,
            _requiredResourceIds = requiredResourceIds,
            _resourceScope = scope,
            _preparedWriter = writer,
            _beforePreparedCleanup = beforeCleanup,
            _preparedEnvelopeStatus = envelopeStatus,
            _preparedExecutionId = executionId,
            _preparedChildEnvironment = childEnvironment,
        };
    }

    /// <summary>
    /// Validates envelope and registrations, waits for resources, runs selected producers, and collects a manifest.
    /// </summary>
    /// <param name="observationOnly">Whether the caller intentionally requests a non-gate observation.</param>
    /// <param name="cancellationToken">Caller cancellation for the complete bounded lifecycle.</param>
    /// <returns>A terminal immutable manifest.</returns>
    public async Task<EvidenceManifest> RunAsync(bool observationOnly = false, CancellationToken cancellationToken = default)
    {
        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _preparedCancellation = cancellationToken;
            if (State != EvidenceHostState.Created)
            {
                throw new InvalidOperationException("EvidenceHost instances execute exactly once.");
            }

            var execution = Stopwatch.StartNew();
            var envelopeStatus = EvidenceEnvelopeStatus.NotRequired;
            var resourceResults = (IReadOnlyList<EvidenceResourceResult>)[];
            try
            {
                State = EvidenceHostState.Validating;
                var envelope = await ValidateEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                envelopeStatus = envelope.Status;
                if (envelope.Results is not null)
                {
                    return await CollectAndCleanAsync(envelope.Results, resourceResults, observationOnly, envelopeStatus, execution).ConfigureAwait(false);
                }

                ValidateRegistrations();
                State = EvidenceHostState.WaitingForResources;
                var readiness = _requiredResourceIds is null
                    ? await WaitForResourcesAsync(cancellationToken).ConfigureAwait(false)
                    : await WaitForPreparedResourcesAsync(cancellationToken).ConfigureAwait(false);
                resourceResults = readiness.ResourceResults;
                if (readiness.FailureResults is not null)
                {
                    return await CollectAndCleanAsync(readiness.FailureResults, resourceResults, observationOnly, envelopeStatus, execution).ConfigureAwait(false);
                }

                State = EvidenceHostState.Producing;
                var results = await ProduceAsync(cancellationToken).ConfigureAwait(false);
                return await CollectAndCleanAsync(results, resourceResults, observationOnly, envelopeStatus, execution).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await CollectAndCleanAsync(
                    FailureForEveryProducer(EvidenceProducerOutcome.Cancelled, "EvidenceHost execution was cancelled by the caller."),
                    resourceResults,
                    observationOnly,
                    envelopeStatus,
                    execution).ConfigureAwait(false);
            }
            catch
            {
                await CleanAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _execution.Release();
        }
    }

    /// <summary>
    /// Disposes registered resources and producers if the host has not already cleaned them up.
    /// </summary>
    /// <returns>A task that completes when owned cleanup settles.</returns>
    public async ValueTask DisposeAsync()
    {
        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == EvidenceHostState.Disposed && _cleaned)
            {
                return;
            }

            await CleanAsync().ConfigureAwait(false);
            State = EvidenceHostState.Disposed;
        }
        finally
        {
            _execution.Release();
        }
    }

    private async Task<EnvelopeValidation> ValidateEnvelopeAsync(CancellationToken cancellationToken)
    {
        if (_preparedEnvelopeStatus is { } preparedStatus) return new EnvelopeValidation(preparedStatus, null);
        var needsEnvelope = _options.RequireTrustedEnvelope || _plan.Profile.Scope == EvidenceProfileScope.Release;
        if (!needsEnvelope)
        {
            return new EnvelopeValidation(EvidenceEnvelopeStatus.NotRequired, null);
        }

        if (_registration.EnvelopeVerifier is null)
        {
            return new EnvelopeValidation(
                EvidenceEnvelopeStatus.Unavailable,
                FailureForEveryProducer(EvidenceProducerOutcome.Invalid, "Trusted evidence requires an explicitly registered CI envelope verifier."));
        }

        var result = await _registration.EnvelopeVerifier.VerifyAsync(_plan, cancellationToken).ConfigureAwait(false);
        return result.Accepted
            ? new EnvelopeValidation(EvidenceEnvelopeStatus.ValidatedNotAttested, null)
            : new EnvelopeValidation(
                EvidenceEnvelopeStatus.Invalid,
                FailureForEveryProducer(EvidenceProducerOutcome.Invalid, result.Diagnostic ?? "The CI execution envelope was not accepted."));
    }

    private void ValidateRegistrations()
    {
        if (_plan.Profile.Resources.Count > EvidenceProfileLimits.MaximumResources
            || _plan.Profile.Producers.Count > EvidenceProfileLimits.MaximumProducers
            || _plan.Profile.Obligations.Count > EvidenceProfileLimits.MaximumObligations)
        {
            throw new EvidenceHostException("ASEVD301", "The resolved plan exceeds v1 EvidenceHost limits.", "Split the policy into bounded profiles before execution.");
        }

        foreach (var resource in _plan.Profile.Resources)
        {
            if ((_requiredResourceIds is null || _requiredResourceIds.Contains(resource.Id))
                && !_registration.Resources.ContainsKey(resource.Id))
            {
                throw new EvidenceHostException("ASEVD302", $"Required resource '{resource.Id}' is not explicitly registered.", "Register a consumer-owned readiness probe for every selected resource.");
            }
        }

        foreach (var producer in _plan.Profile.Producers)
        {
            if (!_registration.Producers.ContainsKey(producer.Id))
            {
                throw new EvidenceHostException("ASEVD303", $"Required producer '{producer.Id}' is not explicitly registered.", "Register the selected producer in the EvidenceHost bootstrap callback.");
            }
        }
    }

    private async Task<ResourceReadiness> WaitForResourcesAsync(CancellationToken cancellationToken)
    {
        var results = new List<EvidenceResourceResult>();
        foreach (var resource in GetResourceGraph().GetOrderedResources())
        {
            var result = await ProbeResourceReadinessAsync(
                resource,
                cancellationToken,
                token => _registration.Resources[resource.Id].WaitUntilReadyAsync(token)).ConfigureAwait(false);
            results.Add(result);
            if (result.Outcome != EvidenceResourceOutcome.Ready)
            {
                return new ResourceReadiness(
                    results,
                    FailureForEveryProducer(EvidenceProducerOutcome.Unavailable, result.Diagnostic!));
            }
        }

        return new ResourceReadiness(results, null);
    }

    private async Task<EvidenceResourceResult> ProbeResourceReadinessAsync(
        EvidenceResourceDeclaration resource,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> startReadiness)
    {
        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(resource.DeadlineSeconds));
        try
        {
            // Eager callers start the probe inline. Prepared callers can start and register
            // their actual callback task before this method awaits its settlement.
            var readinessTask = startReadiness(deadline.Token);
            await readinessTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            return new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.Ready, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EvidenceResourceResult(
                resource.Id,
                EvidenceResourceOutcome.TimedOut,
                timer.ElapsedMilliseconds,
                $"Resource '{resource.Id}' did not become ready before its {resource.DeadlineSeconds}-second deadline.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatalException(exception))
        {
            return new EvidenceResourceResult(
                resource.Id,
                EvidenceResourceOutcome.Unavailable,
                timer.ElapsedMilliseconds,
                $"Resource '{resource.Id}' readiness failed with {exception.GetType().Name}.");
        }
    }

    private async Task<IReadOnlyList<EvidenceProducerResult>> ProduceAsync(CancellationToken cancellationToken)
    {
        var results = new List<EvidenceProducerResult>();
        foreach (var declaration in _plan.Profile.Producers)
        {
            if (_preparedResults.TryGetValue(declaration.Id, out var reused))
            {
                results.Add(reused);
                continue;
            }

            if (_preparedReadiness is not null && ProducerResourceClosure(declaration).Any(id =>
                !_preparedReadiness.TryGetValue(id, out var resource) || resource.Outcome != EvidenceResourceOutcome.Ready))
            {
                results.Add(new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Unavailable, [], "reuse.resource_unavailable: A resource required by this fresh producer did not become ready. Fix its readiness failure and retry."));
                continue;
            }
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(declaration.TimeoutSeconds));
            try
            {
                var artifacts = _preparedWriter?.Invoke(declaration)
                    ?? new EvidenceArtifactWriter(declaration, Path.Join(_artifactDirectory, declaration.Id));
                // A registration should observe cancellation, but the host must also enforce its
                // declared deadline when a third-party producer fails to do so.
                var context = new EvidenceProducerContext(_plan, declaration, _timeProvider, artifacts)
                {
                    ExecutionId = _preparedExecutionId,
                    OwnedChildEnvironment = _preparedChildEnvironment?.Invoke(declaration.Id),
                };
                var implementation = _registration.Producers[declaration.Id];
                var producerTask = _requiredResourceIds is null
                    ? implementation.ProduceAsync(context, deadline.Token).AsTask()
                    : Task.Run(async () => await implementation.ProduceAsync(context, deadline.Token).ConfigureAwait(false));
                _producerExecutions.Add(producerTask);
                var result = await producerTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                results.Add(await ValidateProducerResultAsync(
                    declaration,
                    result,
                    artifacts,
                    timer.ElapsedMilliseconds,
                    deadline.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.TimedOut, [], $"Producer '{declaration.Id}' exceeded its {declaration.TimeoutSeconds}-second deadline.", ElapsedMilliseconds: timer.ElapsedMilliseconds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatalException(exception))
            {
                results.Add(new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Failed, [], $"Producer '{declaration.Id}' failed with {exception.GetType().Name}.", ElapsedMilliseconds: timer.ElapsedMilliseconds));
            }
        }

        return results;
    }

    private static async Task<EvidenceProducerResult> ValidateProducerResultAsync(
        EvidenceProducerDeclaration declaration,
        EvidenceProducerResult result,
        EvidenceArtifactWriter artifacts,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(result.ProducerId, declaration.Id, StringComparison.Ordinal))
        {
            return new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Invalid, [], "Producer returned a result for a different declaration id.", ElapsedMilliseconds: elapsedMilliseconds);
        }

        if (result.Outcome == EvidenceProducerOutcome.Reused || result.ProducerKey is not null
            || result.OriginManifestDigest is not null || result.OriginCompletedAt is not null)
        {
            return new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Invalid, [], "reuse.forged_provenance: Only the prepared receipt coordinator may supply reuse outcomes or provenance.", ElapsedMilliseconds: elapsedMilliseconds);
        }

        var writtenArtifacts = artifacts.WrittenArtifacts;
        if (result.Artifacts is not null && !result.Artifacts.SequenceEqual(writtenArtifacts))
        {
            return new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Invalid, [], "Producer returned artifact metadata that was not written through the declared evidence writer.", ElapsedMilliseconds: elapsedMilliseconds);
        }

        if (!await artifacts.VerifyWrittenArtifactsAsync(cancellationToken).ConfigureAwait(false))
        {
            return new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Invalid, [], "A declared evidence artifact was missing or changed before manifest collection.", ElapsedMilliseconds: elapsedMilliseconds);
        }

        return result with { Artifacts = writtenArtifacts, ElapsedMilliseconds = elapsedMilliseconds };
    }

    private async Task<EvidenceManifest> CollectAndCleanAsync(
        IReadOnlyList<EvidenceProducerResult> results,
        IReadOnlyList<EvidenceResourceResult> resourceResults,
        bool observationOnly,
        EvidenceEnvelopeStatus envelopeStatus,
        Stopwatch execution)
    {
        State = EvidenceHostState.Collecting;
        var cleanupTimer = Stopwatch.StartNew();
        var cleanupFailure = await CleanAsync().ConfigureAwait(false);
        cleanupTimer.Stop();
        execution.Stop();
        var metrics = new EvidenceExecutionMetrics(
            ResourceReadinessMilliseconds: resourceResults.Sum(static result => result.ElapsedMilliseconds),
            ProducerMilliseconds: results.Sum(static result => result.ElapsedMilliseconds),
            CleanupMilliseconds: cleanupTimer.ElapsedMilliseconds,
            TotalMilliseconds: execution.ElapsedMilliseconds,
            CleanupCompleted: cleanupFailure is null,
            CleanupDiagnostic: cleanupFailure);
        var manifest = EvidenceManifestBuilder.Build(_plan, results, observationOnly, envelopeStatus, resourceResults, metrics);
        State = EvidenceHostState.Completed;
        return manifest;
    }

    private async Task<string?> CleanAsync()
    {
        if (_cleaned)
        {
            return _cleanupFailure;
        }

        State = EvidenceHostState.Cleaning;
        // A producer can still be stopping an owned child after its execution deadline.
        // Never delete its workspace or dependencies while that task remains active.
        var callbackTasks = _producerExecutions.Concat(_readinessExecutions).ToArray();
        var producers = Task.WhenAll(callbackTasks);
        try
        {
            await producers.WaitAsync(_options.ProducerCleanupTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!producers.IsCompleted)
        {
            _ = producers.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return "Evidence cleanup failed because a producer did not stop within its cleanup deadline; owned registrations were preserved.";
        }
        catch (Exception exception) when (IsNonFatalException(exception))
        {
            // Completed cancellation or producer failure is already represented in its result.
        }
        catch
        {
            // The original fatal producer failure is propagated by RunAsync. Do not
            // replay that task's exception when the caller later disposes the host.
            // Prepared ownership joins enrolled children before it attempts registration cleanup.
            if (_beforePreparedCleanup is null) _cleaned = true;
            throw;
        }
        var fatalCallback = FirstFatalCallbackFailure(callbackTasks);
        if (fatalCallback is not null)
        {
            if (_beforePreparedCleanup is null) _cleaned = true;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatalCallback).Throw();
        }
        if (_beforePreparedCleanup is not null)
        {
            if (!await _beforePreparedCleanup(true, _preparedCancellation).ConfigureAwait(false))
                return "reuse.material_unsettled: An actual key callback remains active; registrations and key ownership are preserved.";
            _preparedRegistrationCleanup ??= Task.Run(DisposePreparedRegistrationsAsync);
            try
            {
                _cleanupFailure = await _preparedRegistrationCleanup.WaitAsync(_options.ProducerCleanupTimeout, _timeProvider).ConfigureAwait(false);
                _cleaned = true;
                return _cleanupFailure;
            }
            catch (TimeoutException)
            {
                return "reuse.cleanup_unsettled: Actual producer/resource disposal did not finish in its grace period; ownership is retained until settlement.";
            }
        }
        _cleaned = true;
        Exception? failure = null;
        var resourceOwners = _resourceScope is null
            ? _registration.Resources.Values.Reverse().Cast<object>()
            : [_resourceScope];
        var owned = _registration.Producers.Values.Reverse().Cast<object>()
            .Concat(resourceOwners)
            .Distinct(ReferenceEqualityComparer.Instance);
        foreach (var disposable in owned)
        {
            try
            {
                if (disposable is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (disposable is IDisposable syncDisposable)
                {
                    syncDisposable.Dispose();
                }
            }
            catch (Exception exception) when (IsNonFatalException(exception))
            {
                failure ??= exception;
            }
        }

        _cleanupFailure = failure is null ? null : $"Evidence cleanup failed with {failure.GetType().Name}.";
        return _cleanupFailure;
    }

    private async Task<string?> DisposePreparedRegistrationsAsync()
    {
        var disposedOwners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var producers = await EvidenceOwnerDisposal.DisposeAsync(
            _registration.Producers.Values.Reverse(), disposedOwners).ConfigureAwait(false);
        var failure = producers.FirstNonFatalFailure;
        var fatal = producers.FirstFatalFailure;
        if (_resourceScope is not null)
        {
            try { await _resourceScope.DisposeAsync(disposedOwners).ConfigureAwait(false); }
            catch (Exception exception) when (IsNonFatalException(exception)) { failure ??= exception; }
            catch (Exception exception) { fatal ??= exception; }
        }
        else
        {
            var resources = await EvidenceOwnerDisposal.DisposeAsync(
                _registration.Resources.Values.Reverse(), disposedOwners).ConfigureAwait(false);
            failure ??= resources.FirstNonFatalFailure;
            fatal ??= resources.FirstFatalFailure;
        }

        if (fatal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
        return failure is null ? null : $"reuse.cleanup_failed: Owned cleanup failed with {failure.GetType().Name}; execution settlement is unknown.";
    }

    private static Exception? FirstFatalCallbackFailure(IEnumerable<Task> callbackTasks)
    {
        foreach (var exception in callbackTasks.Where(static task => task.IsFaulted)
                     .SelectMany(static task => task.Exception!.Flatten().InnerExceptions))
        {
            if (!IsNonFatalException(exception)) return exception;
        }
        return null;
    }

    /// <summary>Classifies failures that the host and readiness adapters may summarize or retain as secondary errors.</summary>
    /// <param name="exception">The failure being classified.</param>
    /// <returns>False for out-of-memory, stack-overflow, access-violation, and unloaded-domain failures; true otherwise.</returns>
    /// <remarks>Fatal failures must propagate unchanged. Classification does not perform cleanup or handle corrupted process state.</remarks>
    internal static bool IsNonFatalException(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private IReadOnlyList<EvidenceProducerResult> FailureForEveryProducer(EvidenceProducerOutcome outcome, string diagnostic) =>
        _plan.Profile.Producers.Select(producer => _preparedResults.TryGetValue(producer.Id, out var reused)
            ? reused : new EvidenceProducerResult(producer.Id, outcome, [], diagnostic)).ToArray();

    /// <summary>Joins actual callbacks and cleanup before a prepared coordinator can release execution leases.</summary>
    /// <param name="isChildSettlementKnown">Checks whether all admitted children confirmed completion after callback tasks join.</param>
    /// <returns>True only when all actual tasks and owned disposals completed successfully.</returns>
    /// <remarks>Unknown children retain registrations and resources, including when a fatal callback is propagated.</remarks>
    internal async Task<bool> JoinPreparedOwnershipAsync(Func<bool> isChildSettlementKnown)
    {
        ArgumentNullException.ThrowIfNull(isChildSettlementKnown);
        var callbackTasks = _producerExecutions.Concat(_readinessExecutions).ToArray();
        Exception? fatal = null;
        try
        {
            await Task.WhenAll(callbackTasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNonFatalException(exception))
        {
            // The terminal result already records a completed nonfatal callback failure.
        }
        catch (Exception exception) { fatal = exception; }
        fatal ??= FirstFatalCallbackFailure(callbackTasks);

        if (!isChildSettlementKnown())
        {
            if (fatal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
            return false;
        }

        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            if (fatal is not null)
            {
                if (_beforePreparedCleanup is not null)
                {
                    _preparedRegistrationCleanup ??= Task.Run(DisposePreparedRegistrationsAsync);
                    try { _cleanupFailure = await _preparedRegistrationCleanup.ConfigureAwait(false); }
                    catch (Exception) { /* Preserve the original fatal callback after all cleanup owners were attempted. */ }
                    _cleaned = true;
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
            }
            if (_preparedRegistrationCleanup is not null)
            {
                _cleanupFailure = await _preparedRegistrationCleanup.ConfigureAwait(false);
                _cleaned = true;
                return _cleanupFailure is null;
            }
            return await CleanAsync().ConfigureAwait(false) is null;
        }
        finally
        {
            _execution.Release();
        }
    }

    private async Task<ResourceReadiness> WaitForPreparedResourcesAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, EvidenceResourceResult>(StringComparer.Ordinal);
        foreach (var resource in GetResourceGraph().GetOrderedResources())
        {
            if (!_requiredResourceIds!.Contains(resource.Id))
            {
                results.Add(resource.Id, new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.NotRequiredForExecution, 0));
                continue;
            }

            if (resource.Requires.Any(id => results[id].Outcome != EvidenceResourceOutcome.Ready))
            {
                results.Add(resource.Id, new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.Unavailable, 0,
                    "reuse.resource_dependency_unavailable: A required resource failed readiness; fix that failure and retry."));
                continue;
            }

            var result = await ProbeResourceReadinessAsync(resource, cancellationToken, token =>
            {
                var actualTask = Task.Run(() => _registration.Resources[resource.Id].WaitUntilReadyAsync(token));
                _readinessExecutions.Add(actualTask);
                return actualTask;
            }).ConfigureAwait(false);
            results.Add(resource.Id, result);
        }

        _preparedReadiness = results;
        return new ResourceReadiness(results.Values.ToArray(), null);
    }

    private IReadOnlySet<string> ProducerResourceClosure(EvidenceProducerDeclaration producer) =>
        GetResourceGraph().GetProducerResourceClosure(producer);

    private EvidenceResourceGraph GetResourceGraph() =>
        _resourceGraph ??= new EvidenceResourceGraph(_plan.Profile.Resources);

    private sealed record EnvelopeValidation(EvidenceEnvelopeStatus Status, IReadOnlyList<EvidenceProducerResult>? Results);

    private sealed record ResourceReadiness(
        IReadOnlyList<EvidenceResourceResult> ResourceResults,
        IReadOnlyList<EvidenceProducerResult>? FailureResults);
}

/// <summary>
/// Represents a stable, user-facing EvidenceHost lifecycle failure.
/// </summary>
public sealed class EvidenceHostException : InvalidOperationException
{
    /// <summary>
    /// Initializes an exception with a stable code and concrete recovery action.
    /// </summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="problem">Concise failure description.</param>
    /// <param name="fix">Concrete next action.</param>
    public EvidenceHostException(string code, string problem, string fix)
        : base($"{code}: {problem} Fix: {fix}")
    {
        Code = code;
        Fix = fix;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the concrete recovery action.</summary>
    public string Fix { get; }
}
