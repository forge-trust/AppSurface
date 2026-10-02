using System.Diagnostics;
using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

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
/// <param name="RequireTrustedEnvelope">Legacy setting retained for source compatibility; it cannot grant runtime admission.</param>
/// <param name="ArtifactDirectory">Legacy local path retained for source compatibility; admitted runs use the protected output root.</param>
public sealed record EvidenceHostOptions(
    bool RequireTrustedEnvelope = false,
    string? ArtifactDirectory = null);

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
    private readonly Dictionary<string, EvidenceResourceDeclaration> _resourceDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceProducerDeclaration> _producerDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aspireHealthResources = new(StringComparer.Ordinal);
    private readonly List<object> _additionalOwned = [];
    private readonly IReadOnlyDictionary<string, IEvidenceResourceReadiness> _resourceView;
    private readonly IReadOnlyDictionary<string, IEvidenceProducer> _producerView;
    internal Func<IDistributedApplicationBuilder>? ApplicationFactory { get; private set; }
    internal IReadOnlyList<object> AdditionalOwned => _additionalOwned;

    /// <summary>Creates empty explicit registration maps with read-only public views.</summary>
    public EvidenceHostRegistration()
    {
        _resourceView = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IEvidenceResourceReadiness>(_resources);
        _producerView = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IEvidenceProducer>(_producers);
    }

    /// <summary>Gets explicitly registered resource readiness probes.</summary>
    public IReadOnlyDictionary<string, IEvidenceResourceReadiness> Resources => _resourceView;

    /// <summary>Gets explicitly registered typed producers.</summary>
    public IReadOnlyDictionary<string, IEvidenceProducer> Producers => _producerView;

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

    /// <summary>Registers a readiness probe against its complete protected declaration.</summary>
    /// <param name="declaration">The resource declaration selected by the admitted plan.</param>
    /// <param name="resource">Consumer-owned readiness probe.</param>
    public void AddResource(EvidenceResourceDeclaration declaration, IEvidenceResourceReadiness resource)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(resource);
        if (!string.Equals(declaration.Id, resource.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The readiness probe id must match its declared resource id.", nameof(resource));
        }

        AddResource(resource);
        _resourceDeclarations.Add(declaration.Id, declaration);
    }

    /// <summary>Declares a resource whose Aspire health adapter will be bound after the admitted app starts.</summary>
    /// <param name="declaration">The complete protected resource declaration.</param>
    /// <param name="aspireResourceName">Consumer-owned Aspire resource name.</param>
    public void AddAspireHealthResource(EvidenceResourceDeclaration declaration, string aspireResourceName)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireResourceName);
        if (!string.Equals(declaration.Readiness, "aspire_health", StringComparison.Ordinal))
        {
            throw new ArgumentException("The declaration must use aspire_health readiness.", nameof(declaration));
        }

        if (_resourceDeclarations.ContainsKey(declaration.Id) || _aspireHealthResources.ContainsKey(declaration.Id))
        {
            throw new InvalidOperationException($"Evidence resource '{declaration.Id}' is already registered.");
        }

        _resourceDeclarations.Add(declaration.Id, declaration);
        _aspireHealthResources.Add(declaration.Id, aspireResourceName);
    }

    internal void AddOwnedApplication(EvidenceAspireApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _additionalOwned.Add(application);
        foreach (var (id, resourceName) in _aspireHealthResources)
        {
            _resources.Add(id, application.CreateHealthReadiness(id, resourceName));
        }
    }

    /// <summary>Retains pending restricted ownership before any application-start I/O.</summary>
    internal void RetainRestrictedApplication(EvidenceRestrictedAspireApplication application) => _additionalOwned.Add(application);

    /// <summary>Checks complete declarations and concrete compiled adapters before restricted startup.</summary>
    /// <remarks>Returned dictionaries are private snapshots; later changes to caller-visible registration maps cannot replace adapters.</remarks>
    internal (IReadOnlyDictionary<string, IEvidenceResourceReadiness> Resources, IReadOnlyDictionary<string, IEvidenceProducer> Producers)
        BindRestrictedApplication(EvidenceClosedApplicationDefinition definition, EvidenceRestrictedAspireApplication application)
    {
        var producers = CaptureRestrictedDeclarations(definition);
        var resources = definition.Resources.ToDictionary(static item => item.Declaration.Id,
            item => application.CreateReadiness(item.Declaration.Id), StringComparer.Ordinal);
        foreach (var (id, readiness) in resources) _resources.Add(id, readiness);
        return (new System.Collections.ObjectModel.ReadOnlyDictionary<string, IEvidenceResourceReadiness>(resources),
            producers);
    }

    /// <summary>Audits complete registered metadata and sealed producer types; does not enroll an application or start work.</summary>
    internal void ValidateRestrictedDeclarations(EvidenceClosedApplicationDefinition definition)
        => _ = CaptureRestrictedDeclarations(definition);

    private IReadOnlyDictionary<string, IEvidenceProducer> CaptureRestrictedDeclarations(EvidenceClosedApplicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var resources = new Dictionary<string, IEvidenceResourceReadiness>(_resources, StringComparer.Ordinal);
        var declarations = new Dictionary<string, EvidenceResourceDeclaration>(_resourceDeclarations, StringComparer.Ordinal);
        var names = new Dictionary<string, string>(_aspireHealthResources, StringComparer.Ordinal);
        var producers = CaptureRestrictedProducers(definition.Producers.Select(static item => item.Declaration).ToArray());
        if (resources.Count != 0 || declarations.Count != definition.Resources.Count
            || names.Count != definition.Resources.Count
            || definition.Resources.Any(item => !declarations.TryGetValue(item.Declaration.Id, out var declared)
                || !CanonicallyEqual(declared, item.Declaration)
                || !names.TryGetValue(item.Declaration.Id, out var name) || name != item.ResourceName))
        {
            throw new EvidenceAdmissionException("ASEVD404", "Restricted registrations do not match the complete compiled application.");
        }
        return producers;
    }

    /// <summary>Snapshots only sealed coverage registrations matching every protected producer declaration.</summary>
    internal IReadOnlyDictionary<string, IEvidenceProducer> CaptureRestrictedProducers(IReadOnlyList<EvidenceProducerDeclaration> declarations)
    {
        var producers = new Dictionary<string, IEvidenceProducer>(_producers, StringComparer.Ordinal);
        var registeredDeclarations = new Dictionary<string, EvidenceProducerDeclaration>(_producerDeclarations, StringComparer.Ordinal);
        if (producers.Count != declarations.Count || registeredDeclarations.Count != declarations.Count
            || declarations.Any(item => !registeredDeclarations.TryGetValue(item.Id, out var declared)
                || !CanonicallyEqual(declared, item)
                || !producers.TryGetValue(item.Id, out var producer)
                || producer is not EvidenceRestrictedCoverageRegistration registered
                || !CanonicallyEqual(registered.Declaration, item)))
        {
            throw new EvidenceAdmissionException("ASEVD404", "Protected producer registration is unavailable.");
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IEvidenceProducer>(
            producers);
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

    /// <summary>Registers a producer against its complete protected declaration.</summary>
    /// <param name="declaration">The producer declaration selected by the admitted plan.</param>
    /// <param name="producer">Consumer-owned producer.</param>
    public void AddProducer(EvidenceProducerDeclaration declaration, IEvidenceProducer producer)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(producer);
        if (!string.Equals(declaration.Id, producer.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The producer id must match its declared producer id.", nameof(producer));
        }

        AddProducer(producer);
        _producerDeclarations.Add(declaration.Id, declaration);
    }

    internal bool Matches(EvidencePlan plan)
    {
        if (_resourceDeclarations.Count != plan.Profile.Resources.Count || _resources.Count != plan.Profile.Resources.Count
            || _producerDeclarations.Count != plan.Profile.Producers.Count || _producers.Count != plan.Profile.Producers.Count)
        {
            return false;
        }

        return plan.Profile.Resources.All(declaration =>
                _resourceDeclarations.TryGetValue(declaration.Id, out var registered)
                && CanonicallyEqual(registered, declaration))
            && plan.Profile.Producers.All(declaration =>
                _producerDeclarations.TryGetValue(declaration.Id, out var registered)
                && CanonicallyEqual(registered, declaration));
    }

    internal void BindIdOnlyRegistrationsForTests(EvidencePlan plan)
    {
        foreach (var (id, _) in _resources)
        {
            if (!_resourceDeclarations.ContainsKey(id)
                && plan.Profile.Resources.FirstOrDefault(declaration => declaration.Id == id) is { } declaration)
            {
                _resourceDeclarations.Add(id, declaration);
            }
        }

        foreach (var (id, _) in _producers)
        {
            if (!_producerDeclarations.ContainsKey(id)
                && plan.Profile.Producers.FirstOrDefault(declaration => declaration.Id == id) is { } declaration)
            {
                _producerDeclarations.Add(id, declaration);
            }
        }
    }

    private static bool CanonicallyEqual<T>(T left, T right) =>
        EvidenceCanonicalJson.Serialize(left).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(right));

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

    /// <summary>Sets the deferred Aspire builder factory for a separately restricted supervisor child.</summary>
    /// <param name="factory">Consumer-owned builder factory.</param>
    /// <remarks>
    /// The current protected worker has no Aspire resource capability map or restricted child. The production path
    /// rejects a configured factory with <c>ASEVD407</c> before invoking it; direct head-application startup is not
    /// supported.
    /// </remarks>
    public void SetApplicationFactory(Func<IDistributedApplicationBuilder> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (ApplicationFactory is not null)
        {
            throw new InvalidOperationException("EvidenceHost accepts exactly one Aspire application factory.");
        }

        ApplicationFactory = factory;
    }
}

/// <summary>
/// Runs a separate, consumer-owned EvidenceHost with explicit registrations and bounded cleanup.
/// </summary>
public sealed class EvidenceHostBootstrap : IAsyncDisposable
{
    private readonly EvidencePlan _plan;
    private readonly Action<EvidenceHostRegistration> _configure;
    private EvidenceHostRegistration _registration = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private EvidenceLinuxArtifactRoot? _artifactRoot;
    private EvidenceRunByteQuota? _artifactQuota;
    private EvidenceAdmissionResult? _admission;
    private EvidenceWorkerExecution? _lifecycle;
    private readonly List<EvidenceArtifactWriter> _artifactWriters = [];
    private string? _testArtifactDirectory;
    private bool _cleaned;
    private bool _runClaimed;
    private EvidenceLinuxWorkerSupervisor? _restrictedWorker;
    private byte[]? _protectedDiffBytes;
    private EvidenceRunByteQuota? _processOutputQuota;
    private IReadOnlyDictionary<string, IEvidenceResourceReadiness>? _restrictedResources;
    private IReadOnlyDictionary<string, IEvidenceProducer>? _restrictedProducers;
    private EvidenceRestrictedAspireApplication? _restrictedApplication;

    private EvidenceHostBootstrap(
        EvidencePlan plan,
        Action<EvidenceHostRegistration> configure)
    {
        _plan = plan;
        _configure = configure;
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
    /// <returns>A single-use EvidenceHost instance.</returns>
    public static EvidenceHostBootstrap Create(
        EvidencePlan plan,
        Action<EvidenceHostRegistration> configure,
        EvidenceHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configure);

        var snapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(EvidenceCanonicalJson.Serialize(plan));
        _ = options;
        return new EvidenceHostBootstrap(snapshot, configure);
    }

    /// <summary>
    /// Rejects legacy Boolean execution before configuration, output allocation, or callback invocation.
    /// </summary>
    /// <param name="observationOnly">Omission or false rejects with ASEVD401; true rejects with ASEVD402.</param>
    /// <param name="cancellationToken">Retained for source compatibility; no lifecycle is started.</param>
    /// <returns>A task faulted with <see cref="EvidenceAdmissionException"/> containing the migration diagnostic.</returns>
    [Obsolete("Use RunAsync(EvidenceExecutionRequest) with a supported protected worker. The legacy Boolean cannot select admission.")]
    public Task<EvidenceManifest> RunAsync(bool observationOnly = false, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // Preserve the legacy signature only to give every caller the same migration diagnostic. In
        // particular, do not configure registrations, allocate output, or invoke an Aspire factory.
        return Task.FromException<EvidenceManifest>(observationOnly
            ? new EvidenceAdmissionException("ASEVD402", "Legacy Observation selection requires an independently armed protected worker.")
            : new EvidenceAdmissionException("ASEVD401", "Select Trusted or Observation explicitly."));
    }

    /// <summary>Runs through the protected Linux worker and shared admission lifecycle.</summary>
    /// <param name="request">Explicit mode and protected launcher's control channel.</param>
    /// <param name="cancellationToken">Caller cancellation for admission and execution.</param>
    /// <returns>A terminal immutable manifest.</returns>
    /// <remarks>
    /// No production Aspire consumer proof or registered resource capability map is currently available. Production
    /// admission remains fail-closed until a supported Linux setup supplies that proof and any Aspire application runs
    /// in a separately restricted supervisor child. The internal test seam exercises library behavior only.
    /// A run claims this instance before authentication. Authentication, admission, or execution failure consumes
    /// that single attempt; create another host for another run. A null request or cancellation before acquiring
    /// execution ownership does not consume the attempt.
    /// </remarks>
    public Task<EvidenceManifest> RunAsync(EvidenceExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSupervisedAsync(request, cancellationToken);
    }

    /// <summary>
    /// Exercises the same shared admission and admitted lifecycle core with test-owned acceptance and supervision.
    /// </summary>
    /// <param name="mode">Requested execution mode.</param>
    /// <param name="context">Test-owned admission context for the resolved plan.</param>
    /// <param name="supervisor">Test-owned execution supervisor.</param>
    /// <param name="verifier">Optional test-owned admission verifier.</param>
    /// <param name="artifactDirectory">Temporary output directory used by this test run.</param>
    /// <param name="jobRemaining">Frozen monotonic allowance supplied by the test supervisor.</param>
    /// <param name="completeWorker">Optional test-owned worker completion callback.</param>
    /// <param name="cancellationToken">Cancellation for admission and execution.</param>
    /// <param name="loweredStartAllowance">Optional stricter per-stage allowance. It must be positive and no greater than the protected 120-second maximum.</param>
    /// <returns>The terminal manifest produced by the shared lifecycle.</returns>
    /// <remarks>
    /// This internal seam is available only to the Aspire test assembly. Its synthetic output binding and in-process
    /// supervisor do not establish protected consumer, process-isolation, or platform acceptance.
    /// The same start limit must reach registration and application startup. If one execution keeps the default cap,
    /// its exact-stage check can reject a run that was admitted with a stricter protected allowance.
    /// </remarks>
    internal async Task<EvidenceManifest> RunSharedCoreForTestsAsync(
        EvidenceExecutionMode mode,
        EvidenceAdmissionContext context,
        IEvidenceExecutionSupervisor supervisor,
        IEvidenceAdmissionVerifier? verifier,
        string artifactDirectory,
        TimeSpan jobRemaining,
        Func<CancellationToken, ValueTask>? completeWorker = null,
        CancellationToken cancellationToken = default,
        TimeSpan? loweredStartAllowance = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        if (loweredStartAllowance is { } requestedStartAllowance
            && (requestedStartAllowance <= TimeSpan.Zero || requestedStartAllowance > EvidenceRunBudgetLimits.Start))
        {
            throw new ArgumentOutOfRangeException(
                nameof(loweredStartAllowance),
                "The lowered start allowance must be positive and no greater than 120 seconds.");
        }

        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        EvidenceWorkerExecution? lifecycle = null;
        EvidenceAdmissionResult? admission = null;
        try
        {
            if (_runClaimed || State != EvidenceHostState.Created)
            {
                throw new InvalidOperationException("EvidenceHost instances execute exactly once.");
            }

            _runClaimed = true;
            var clock = TimeProvider.System;
            var admissionLimit = EvidenceRunBudgetLimits.Admission;
            var startLimit = loweredStartAllowance ?? EvidenceRunBudgetLimits.Start;
            var collectionLimit = EvidenceRunBudgetLimits.Collection;
            var cleanupLimit = EvidenceRunBudgetLimits.Cleanup;
            var stoppingLimit = EvidenceRunBudgetLimits.Stopping;
            var budgetStages = new List<EvidenceRunStageDeadline>
            {
                new(EvidenceRunStage.Admission, admissionLimit),
                new(EvidenceRunStage.Start, startLimit),
                new(EvidenceRunStage.Start, startLimit),
                new(EvidenceRunStage.Start, startLimit),
            };
            budgetStages.AddRange(OrderResources(_plan.Profile.Resources.ToDictionary(static item => item.Id, StringComparer.Ordinal))
                .Select(static item => new EvidenceRunStageDeadline(EvidenceRunStage.Resource, TimeSpan.FromSeconds(item.DeadlineSeconds))));
            budgetStages.AddRange(_plan.Profile.Producers
                .Select(static item => new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(item.TimeoutSeconds))));

            if (!EvidenceRunTimeBudget.TryCreateFromAllowance(
                clock,
                jobRemaining,
                budgetStages,
                collectionLimit,
                cleanupLimit,
                stoppingLimit,
                out var timeBudget)
                || timeBudget is null)
            {
                throw new EvidenceAdmissionException("ASEVD410", "Test job time cannot cover admitted work and cleanup reserves.");
            }

            lifecycle = new EvidenceWorkerExecution(
                supervisor,
                clock,
                jobRemaining,
                cleanupLimit,
                stoppingLimit,
                fatalTermination: static message => new EvidenceWorkerTestInterruptionException(message),
                collectionReserve: collectionLimit);
            _lifecycle = lifecycle;

            var admitted = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Admission,
                admissionLimit,
                token => EvidenceAdmission.AdmitAsync(mode, _plan, context, supervisor, verifier, token),
                cancellationToken).ConfigureAwait(false);
            if (admitted.Outcome != EvidenceWorkerStageOutcome.Passed || admitted.Value is null)
            {
                await lifecycle.StopAndDisposeAsync().ConfigureAwait(false);
                if (lifecycle.TerminalException is EvidenceAdmissionException admissionFailure)
                {
                    throw admissionFailure;
                }

                throw new EvidenceAdmissionException("ASEVD410", "Test admission did not complete.");
            }

            admission = admitted.Value;
            var outputReady = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Start,
                startLimit,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetFullPath(artifactDirectory));
                    return ValueTask.FromResult(true);
                },
                cancellationToken,
                admission).ConfigureAwait(false);
            if (outputReady.Outcome != EvidenceWorkerStageOutcome.Passed)
            {
                throw new EvidenceAdmissionException("ASEVD410", "Test output binding did not complete.");
            }

            admission.Activate("internal-test-output-binding");
            return await RunAdmittedStagesAsync(
                timeBudget,
                lifecycle,
                admission,
                root: null,
                artifactDirectory,
                completeWorker,
                bindIdOnlyRegistrationsForTests: true,
                startLimit: startLimit,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (lifecycle is not null)
            {
                try { await lifecycle.StopAndDisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the host-selected primary diagnostic. */ }
            }

            if (admission is not null && State != EvidenceHostState.Completed)
            {
                admission.LatchFailure();
            }

            throw;
        }
        finally
        {
            _artifactRoot = null;
            _artifactQuota = null;
            _admission = null;
            _lifecycle = null;
            _testArtifactDirectory = null;
            _artifactWriters.Clear();
            _execution.Release();
        }
    }

    private async Task<EvidenceManifest> RunSupervisedAsync(EvidenceExecutionRequest request, CancellationToken cancellationToken)
    {
        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        EvidenceLinuxWorkerSupervisor? supervisor = null;
        EvidenceAdmissionResult? admission = null;
        EvidenceLinuxArtifactRoot? root = null;
        EvidenceWorkerExecution? lifecycle = null;
        EvidenceClosedApplicationDefinition? compiledApplication = null;
        byte[]? protectedDiffBytes = null;
        try
        {
            if (_runClaimed || State != EvidenceHostState.Created)
            {
                throw new InvalidOperationException("EvidenceHost instances execute exactly once.");
            }

            _runClaimed = true;
            try
            {
                supervisor = await EvidenceLinuxWorkerSupervisor.ConnectAsync(request.ControlChannel, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatalException(exception))
            {
                throw new EvidenceAdmissionException("ASEVD402", "Protected worker authentication failed.");
            }
            if (supervisor.Descriptor.Mode != request.Mode.ToString().ToLowerInvariant())
            {
                throw new EvidenceAdmissionException("ASEVD401", "Requested mode does not match the protected worker mode.");
            }

            var descriptor = supervisor.Descriptor;
            var systemClock = TimeProvider.System;
            // JobDeadlineUtc remains audit metadata. Control time comes only from the
            // launcher's frozen monotonic allowance, including its socket handshake.
            var remaining = supervisor.JobRemaining;
            var admissionLimit = TimeSpan.FromSeconds(descriptor.AdmissionSeconds);
            var startLimit = TimeSpan.FromSeconds(descriptor.StartSeconds);
            var collectionLimit = TimeSpan.FromSeconds(descriptor.CollectionSeconds);
            var cleanupLimit = TimeSpan.FromSeconds(descriptor.CleanupSeconds);
            var stoppingLimit = TimeSpan.FromSeconds(descriptor.StoppingSeconds);
            var budgetStages = new List<EvidenceRunStageDeadline>
            {
                new(EvidenceRunStage.Admission, admissionLimit),
                new(EvidenceRunStage.Start, startLimit), // secure output allocation
                new(EvidenceRunStage.Start, startLimit), // consumer registration callback
                new(EvidenceRunStage.Start, startLimit), // optional Aspire application factory
            };

            budgetStages.AddRange(OrderResources(_plan.Profile.Resources.ToDictionary(static item => item.Id, StringComparer.Ordinal))
                .Select(static item => new EvidenceRunStageDeadline(EvidenceRunStage.Resource, TimeSpan.FromSeconds(item.DeadlineSeconds))));
            budgetStages.AddRange(_plan.Profile.Producers
                .Select(static item => new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(item.TimeoutSeconds))));
            if (!EvidenceRunTimeBudget.TryCreateFromAllowance(
                systemClock,
                remaining,
                budgetStages,
                collectionLimit,
                cleanupLimit,
                stoppingLimit,
                out var timeBudget)
                || timeBudget is null)
            {
                throw new EvidenceAdmissionException("ASEVD410", "Protected job time cannot cover admitted work and cleanup reserves.");
            }

            lifecycle = new EvidenceWorkerExecution(
                supervisor,
                systemClock,
                remaining,
                cleanupLimit,
                stoppingLimit,
                collectionReserve: collectionLimit);

            var admitted = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Admission,
                admissionLimit,
                async token =>
                {
                    var protectedInputs = await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, token).ConfigureAwait(false);
                    if (!SamePlan(_plan, protectedInputs.Plan))
                    {
                        throw new EvidenceAdmissionException("ASEVD403", "Caller plan does not match the protected resolved plan.");
                    }

                    var context = EvidenceProtectedWorkerInputs.CreateContext(descriptor, protectedInputs.Policy, protectedInputs.Plan);
                    protectedDiffBytes = protectedInputs.DiffBytes;
                    if (descriptor.Application is not null)
                    {
                        compiledApplication = EvidenceClosedApplicationCatalogue.Resolve(protectedInputs.Policy, protectedInputs.Plan, descriptor);
                    }
                    IEvidenceAdmissionVerifier? verifier = context.ExpectedAssertion is null
                        ? null
                        : new EvidenceProtectedWorkerInputs.RegisteredVerifier(supervisor, context.ExpectedAssertion);
                    return await EvidenceAdmission.AdmitAsync(
                        request.Mode, protectedInputs.Plan, context, supervisor, verifier, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (admitted.Outcome != EvidenceWorkerStageOutcome.Passed || admitted.Value is null)
            {
                await lifecycle.StopAndDisposeAsync().ConfigureAwait(false);
                if (lifecycle.TerminalException is EvidenceAdmissionException admissionFailure)
                {
                    throw admissionFailure;
                }

                throw new EvidenceAdmissionException("ASEVD410", "Protected admission did not complete.");
            }

            admission = admitted.Value;
            _lifecycle = lifecycle;

            var allocation = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Start,
                startLimit,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var allocated = EvidenceLinuxArtifactRoot.Allocate(
                        descriptor.OutputParent,
                        descriptor.OutputParentIdentity,
                        descriptor.OutputSlot,
                        descriptor.WorkerUid,
                        descriptor.WorkerGid);
                    // Keep the retained handles owned even if cancellation wins before this stage returns.
                    root = allocated;
                    token.ThrowIfCancellationRequested();
                    admission.Activate(allocated.Identity.ToString());
                    return ValueTask.FromResult(allocated);
                },
                cancellationToken,
                admission).ConfigureAwait(false);
            if (allocation.Outcome != EvidenceWorkerStageOutcome.Passed || allocation.Value is null)
            {
                await lifecycle.StopAndDisposeAsync().ConfigureAwait(false);
                throw new EvidenceAdmissionException("ASEVD410", "Protected artifact root activation did not complete.");
            }

            root = allocation.Value;
            _artifactRoot = root;
            _admission = admission;
            var manifest = await RunAdmittedStagesAsync(
                timeBudget,
                lifecycle,
                admission,
                root,
                testArtifactDirectory: null,
                completeWorker: supervisor.CompleteWorkerAsync,
                bindIdOnlyRegistrationsForTests: false,
                startLimit: startLimit,
                cancellationToken: cancellationToken,
                worker: supervisor,
                compiledApplication: compiledApplication,
                protectedDiffBytes: protectedDiffBytes).ConfigureAwait(false);
            root = null;
            return manifest;
        }
        catch
        {
            if (lifecycle is not null)
            {
                try { await lifecycle.StopAndDisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the host-selected primary diagnostic. */ }
            }
            else if (supervisor is not null)
            {
                try
                {
                    supervisor.CloseAdmission();
                    using var stopping = new CancellationTokenSource(EvidenceRunBudgetLimits.Stopping);
                    await supervisor.RequestStopAsync(stopping.Token).ConfigureAwait(false);
                    await supervisor.WaitForOwnedExitAsync(stopping.Token).ConfigureAwait(false);
                }
                catch { /* No callback was admitted; best-effort bounded handshake cleanup. */ }
            }

            if (admission is not null && State != EvidenceHostState.Completed)
            {
                admission.LatchFailure();
            }

            throw;
        }
        finally
        {
            if (root is not null)
            {
                try { await root.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the primary failure; unsuccessful collection publishes no returned manifest. */ }
            }

            _artifactRoot = null;
            _admission = null;
            _artifactQuota = null;
            _lifecycle = null;
            _restrictedWorker = null;
            _protectedDiffBytes = null;
            _processOutputQuota = null;
            _restrictedResources = null;
            _restrictedProducers = null;
            _restrictedApplication = null;
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
            if (State == EvidenceHostState.Disposed)
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

    private async Task<EvidenceManifest> RunAdmittedStagesAsync(
        EvidenceRunTimeBudget timeBudget,
        EvidenceWorkerExecution lifecycle,
        EvidenceAdmissionResult admission,
        EvidenceLinuxArtifactRoot? root,
        string? testArtifactDirectory,
        Func<CancellationToken, ValueTask>? completeWorker,
        bool bindIdOnlyRegistrationsForTests,
        TimeSpan startLimit,
        CancellationToken cancellationToken,
        EvidenceLinuxWorkerSupervisor? worker = null,
        EvidenceClosedApplicationDefinition? compiledApplication = null,
        byte[]? protectedDiffBytes = null)
    {
        _artifactRoot = root;
        _testArtifactDirectory = testArtifactDirectory is null ? null : Path.GetFullPath(testArtifactDirectory);
        _admission = admission;
        _restrictedWorker = worker;
        _protectedDiffBytes = protectedDiffBytes;
        _processOutputQuota = worker is null ? null : EvidenceRunByteQuota.CreateProcessOutput(onExceeded: () =>
        {
            lifecycle.LatchFailure();
            admission.LatchFailure();
        });
        if (root is not null)
        {
            _artifactQuota = EvidenceRunByteQuota.CreateArtifact(onExceeded: () =>
            {
                lifecycle.LatchFailure();
                admission.LatchFailure();
            });
        }

        if (!lifecycle.RegisterDisposer(_ => CleanRegistrationsAsync()))
        {
            throw new EvidenceAdmissionException("ASEVD410", "Registration cleanup could not be admitted.");
        }

        EvidenceRestrictedAspireApplication? restrictedApplication = null;
        if (compiledApplication is not null)
        {
            if (worker is null) throw new EvidenceAdmissionException("ASEVD407", "Restricted application supervision is unavailable.");
            restrictedApplication = new(worker, admission, _plan, compiledApplication, lifecycle);
            _registration.RetainRestrictedApplication(restrictedApplication);
            _restrictedApplication = restrictedApplication;
        }

        State = EvidenceHostState.Validating;
        // Pitfall: this value must match the scheduled Start stages and both exact-stage checks below.
        var configured = await ExecuteBudgetedAsync(
            timeBudget,
            lifecycle,
            EvidenceRunStage.Start,
            startLimit,
            token =>
            {
                token.ThrowIfCancellationRequested();
                admission.ValidateActive(_plan);
                _configure(_registration);
                return ValueTask.FromResult(true);
            },
            cancellationToken,
            admission).ConfigureAwait(false);
        if (configured.Outcome != EvidenceWorkerStageOutcome.Passed)
        {
            throw new EvidenceAdmissionException("ASEVD410", "Registration configuration did not complete.");
        }

        if (bindIdOnlyRegistrationsForTests)
        {
            _registration.BindIdOnlyRegistrationsForTests(_plan);
        }

        if (_registration.ApplicationFactory is not null)
        {
            throw new EvidenceAdmissionException(
                "ASEVD407",
                "Aspire application startup requires a separately restricted supervisor child.");
        }

        if (restrictedApplication is not null)
        {
            (_restrictedResources, _restrictedProducers) = _registration.BindRestrictedApplication(compiledApplication!, restrictedApplication);
        }
        else if (worker is not null && _plan.Profile.Resources.Count != 0)
        {
            throw new EvidenceAdmissionException("ASEVD407", "Resource-backed execution requires a compiled restricted application.");
        }

        if (worker is not null)
        {
            _restrictedProducers = _registration.CaptureRestrictedProducers(_plan.Profile.Producers);
        }

        ValidateRegistrations();

        // Only a resolved compile-owned registration can reach root-mediated start.
        // Public factories remain rejected, and v1 keeps its scheduled no-op start stage.
        var appStart = await ExecuteBudgetedAsync(
            timeBudget,
            lifecycle,
            EvidenceRunStage.Start,
            startLimit,
            async token =>
            {
                if (restrictedApplication is not null) await restrictedApplication.StartAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            admission).ConfigureAwait(false);
        if (appStart.Outcome != EvidenceWorkerStageOutcome.Passed)
        {
            throw new EvidenceAdmissionException("ASEVD410", "Aspire application startup did not complete.");
        }

        var execution = Stopwatch.StartNew();
        State = EvidenceHostState.WaitingForResources;
        var readiness = await WaitForResourcesAsync(cancellationToken, lifecycle, timeBudget, admission).ConfigureAwait(false);
        State = EvidenceHostState.Producing;
        var producerResults = readiness.FailureResults
            ?? await ProduceAsync(cancellationToken, lifecycle, timeBudget, admission).ConfigureAwait(false);

        State = EvidenceHostState.Cleaning;
        _ = timeBudget.TryAbandonStagesAndBeginCleanup(stageClosed: true);
        var cleanupTimer = Stopwatch.StartNew();
        var stopAndDisposeSucceeded = await lifecycle.StopAndDisposeAsync().ConfigureAwait(false);
        cleanupTimer.Stop();
        var cleanupCompleted = lifecycle.CleanupCompleted;
        _ = timeBudget.CompleteCleanup();
        if (!stopAndDisposeSucceeded || lifecycle.TerminalCode != EvidenceWorkerTerminalCode.None || !cleanupCompleted)
        {
            admission.LatchFailure();
        }

        execution.Stop();
        var ownedWorkStopped = lifecycle.OwnWorkStopped;
        if (!ownedWorkStopped)
        {
            throw new EvidenceAdmissionException("ASEVD410", "Owned work has not stopped; finalization is forbidden.");
        }

        var terminalFailureCode = TerminalFailureCode(lifecycle.TerminalCode);
        var metrics = new EvidenceExecutionMetrics(
            ResourceReadinessMilliseconds: readiness.ResourceResults.Sum(static result => result.ElapsedMilliseconds),
            ProducerMilliseconds: producerResults.Sum(static result => result.ElapsedMilliseconds),
            CleanupMilliseconds: cleanupTimer.ElapsedMilliseconds,
            TotalMilliseconds: execution.ElapsedMilliseconds,
            CleanupCompleted: cleanupCompleted,
            CleanupDiagnostic: cleanupCompleted ? null : "Evidence cleanup failed.",
            TerminalFailureCode: terminalFailureCode);

        if (!timeBudget.TryBeginCollection())
        {
            admission.LatchFailure();
            throw new EvidenceAdmissionException("ASEVD410", "Manifest collection reserve is unavailable.");
        }

        var collected = await lifecycle.CollectAsync(
            timeBudget.CollectionRemaining,
            async token =>
            {
                var verification = await VerifyAndAttachArtifactsAsync(producerResults, token).ConfigureAwait(false);
                producerResults = verification.Results;
                if (!verification.Verified)
                {
                    admission.LatchFailure();
                    metrics = metrics with { TerminalFailureCode = metrics.TerminalFailureCode ?? nameof(EvidenceWorkerTerminalCode.StageFailed) };
                }
                admission.Complete(ownedWorkStopped, verification.Verified, cleanupCompleted);
                var manifest = EvidenceManifestBuilder.Build(_plan, producerResults, admission, readiness.ResourceResults, metrics);

                if (root is not null)
                {
                    var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
                    await root.WriteAsync("manifest.json", manifestBytes, token).ConfigureAwait(false);
                    await root.VerifyAsync("manifest.json", manifestBytes.Length, EvidenceDigest.Sha256(manifestBytes), token)
                        .ConfigureAwait(false);

                    // This host-owned disposal remains inside the bounded collection callback.
                    // The broker exit acknowledgement follows only after retained handles close.
                    await root.DisposeAsync().ConfigureAwait(false);
                }

                if (completeWorker is not null)
                {
                    await completeWorker(token).ConfigureAwait(false);
                }

                return manifest;
            },
            CancellationToken.None).ConfigureAwait(false);
        _ = timeBudget.CompleteCollection();
        if (collected.Outcome != EvidenceWorkerStageOutcome.Passed || collected.Value is null)
        {
            throw new EvidenceAdmissionException("ASEVD410", "Manifest collection did not complete.");
        }

        // A pre-existing timeout/cancellation makes StopAndDisposeAsync return false even when
        // cleanup itself completed. Eligibility follows the latched cause; cleanup metrics use
        // CleanupCompleted independently.
        State = EvidenceHostState.Completed;
        return collected.Value;
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
            if (!_registration.Resources.ContainsKey(resource.Id))
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

        if (!_registration.Matches(_plan))
        {
            throw new EvidenceAdmissionException("ASEVD404", "Registered declarations do not exactly match the admitted plan.");
        }
    }

    private async Task<ResourceReadiness> WaitForResourcesAsync(
        CancellationToken cancellationToken,
        EvidenceWorkerExecution lifecycle,
        EvidenceRunTimeBudget timeBudget,
        EvidenceAdmissionResult admission)
    {
        var declarations = _plan.Profile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
        var results = new List<EvidenceResourceResult>();
        foreach (var resource in OrderResources(declarations))
        {
            var timer = Stopwatch.StartNew();
            var readiness = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Resource,
                TimeSpan.FromSeconds(resource.DeadlineSeconds),
                async token =>
                {
                    var probe = (_restrictedResources ?? _registration.Resources)[resource.Id];
                    if (_restrictedApplication is { } application)
                    {
                        await application.RunReadinessAsync(resource.Id, probe, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await probe.WaitUntilReadyAsync(token).ConfigureAwait(false);
                    }
                    return true;
                },
                cancellationToken,
                admission).ConfigureAwait(false);
            if (readiness.Outcome == EvidenceWorkerStageOutcome.Passed)
            {
                results.Add(new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.Ready, timer.ElapsedMilliseconds));
                continue;
            }

            var timeout = readiness.Outcome == EvidenceWorkerStageOutcome.TimedOut;
            var cancelled = readiness.Outcome == EvidenceWorkerStageOutcome.Cancelled || cancellationToken.IsCancellationRequested;
            var outcome = timeout
                ? EvidenceResourceOutcome.TimedOut
                : cancelled ? EvidenceResourceOutcome.Cancelled : EvidenceResourceOutcome.Unavailable;
            var diagnostic = timeout
                ? $"Resource '{resource.Id}' did not become ready before its {resource.DeadlineSeconds}-second deadline."
                : cancelled
                    ? $"Resource '{resource.Id}' readiness was cancelled by the caller."
                    : $"Resource '{resource.Id}' readiness did not complete successfully.";
            results.Add(new EvidenceResourceResult(resource.Id, outcome, timer.ElapsedMilliseconds, diagnostic));
            return new ResourceReadiness(
                results,
                FailureForEveryProducer(cancelled ? EvidenceProducerOutcome.Cancelled : EvidenceProducerOutcome.Unavailable, diagnostic));
        }

        return new ResourceReadiness(results, null);
    }

    private async Task<IReadOnlyList<EvidenceProducerResult>> ProduceAsync(
        CancellationToken cancellationToken,
        EvidenceWorkerExecution lifecycle,
        EvidenceRunTimeBudget timeBudget,
        EvidenceAdmissionResult admission)
    {
        var results = new List<EvidenceProducerResult>();
        foreach (var declaration in _plan.Profile.Producers)
        {
            var timer = Stopwatch.StartNew();
            var artifacts = _artifactRoot is { } protectedRoot
                ? new EvidenceArtifactWriter(declaration, protectedRoot, _artifactQuota!, lifecycle, _admission!)
                : new EvidenceArtifactWriter(declaration, Path.Join(_testArtifactDirectory!, declaration.Id));
            _artifactWriters.Add(artifacts);
            var execution = await ExecuteBudgetedAsync(
                timeBudget,
                lifecycle,
                EvidenceRunStage.Producer,
                TimeSpan.FromSeconds(declaration.TimeoutSeconds),
                async token =>
                {
                    var producer = (_restrictedProducers ?? _registration.Producers)[declaration.Id];
                    var context = new EvidenceProducerContext(_plan, declaration, TimeProvider.System, artifacts);
                    if (_restrictedWorker is null)
                    {
                        return await producer.ProduceAsync(context, token).ConfigureAwait(false);
                    }

                    using var lease = artifacts.BindRestrictedProducerLease(_restrictedWorker, admission, _plan,
                        _protectedDiffBytes, _processOutputQuota!, lifecycle, token);
                    return await producer.ProduceAsync(context, token).ConfigureAwait(false);
                },
                cancellationToken,
                admission).ConfigureAwait(false);
            var elapsed = timer.ElapsedMilliseconds;
            if (execution.Outcome == EvidenceWorkerStageOutcome.Passed && execution.Value is { } result)
            {
                if (result.ProducerId != declaration.Id || !Enum.IsDefined(result.Outcome)
                    || result.SatisfiedAssertionIds is null
                    || result.SatisfiedAssertionIds.Any(assertion => !declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal)))
                {
                    result = new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Invalid, [],
                        "Producer output does not match its protected declaration.");
                }
                results.Add(result with { ElapsedMilliseconds = elapsed });
                if (result.Outcome != EvidenceProducerOutcome.Passed)
                {
                    admission.LatchFailure();
                    await lifecycle.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.StageFailed).ConfigureAwait(false);
                    break;
                }
                continue;
            }

            var outcome = cancellationToken.IsCancellationRequested
                ? EvidenceProducerOutcome.Cancelled
                : execution.Outcome switch
            {
                EvidenceWorkerStageOutcome.TimedOut => EvidenceProducerOutcome.TimedOut,
                EvidenceWorkerStageOutcome.Cancelled => EvidenceProducerOutcome.Cancelled,
                _ => EvidenceProducerOutcome.Failed,
            };
            var timedOut = outcome == EvidenceProducerOutcome.TimedOut;
            results.Add(new EvidenceProducerResult(
                declaration.Id,
                outcome,
                [],
                timedOut
                    ? $"Producer '{declaration.Id}' exceeded its {declaration.TimeoutSeconds}-second deadline."
                    : outcome == EvidenceProducerOutcome.Cancelled
                        ? $"Producer '{declaration.Id}' was cancelled by the caller."
                        : $"Producer '{declaration.Id}' did not complete successfully.",
                ElapsedMilliseconds: elapsed));
            break;
        }

        return results;
    }

    private static async Task<(EvidenceWorkerStageOutcome Outcome, T? Value)> ExecuteBudgetedAsync<T>(
        EvidenceRunTimeBudget timeBudget,
        EvidenceWorkerExecution lifecycle,
        EvidenceRunStage expectedStage,
        TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken cancellationToken,
        EvidenceAdmissionResult? admission = null)
    {
        if (!timeBudget.TryBeginNextStage(cancellationToken, out var admittedStage)
            || admittedStage is null
            || admittedStage.Stage != expectedStage
            || admittedStage.Duration != deadline)
        {
            admission?.LatchFailure();
            _ = timeBudget.TryAbandonStagesAndBeginCleanup(stageClosed: true);
            await lifecycle.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.AdmissionClosed, cancellationToken).ConfigureAwait(false);
            return (EvidenceWorkerStageOutcome.Rejected, default);
        }

        var result = await lifecycle.ExecuteAsync(expectedStage, deadline, callback, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == EvidenceWorkerStageOutcome.Passed)
        {
            _ = timeBudget.CompleteCurrentStage();
        }
        else
        {
            admission?.LatchFailure();
            _ = timeBudget.TryAbandonStagesAndBeginCleanup(stageClosed: true);
        }

        return result;
    }

    private async Task<(bool Verified, IReadOnlyList<EvidenceProducerResult> Results)> VerifyAndAttachArtifactsAsync(
        IReadOnlyList<EvidenceProducerResult> results,
        CancellationToken cancellationToken)
    {
        State = EvidenceHostState.Collecting;
        var valid = true;
        var finalized = results.ToArray();
        for (var index = 0; index < _artifactWriters.Count; index++)
        {
            var writer = _artifactWriters[index];
            if (index >= _plan.Profile.Producers.Count) break;
            var declaration = _plan.Profile.Producers[index];
            var resultIndex = -1;
            for (var i = 0; i < results.Count; i++)
            {
                if (string.Equals(results[i].ProducerId, declaration.Id, StringComparison.Ordinal))
                {
                    resultIndex = i;
                    break;
                }
            }

            if (resultIndex < 0) continue;
            var result = results[resultIndex];
            var artifacts = writer.WrittenArtifacts;
            if (result.ProducerId != declaration.Id
                || (result.Artifacts is not null && !result.Artifacts.SequenceEqual(artifacts))
                || !await writer.VerifyWrittenArtifactsAsync(cancellationToken).ConfigureAwait(false))
            {
                valid = false;
                _admission!.LatchFailure();
                finalized[resultIndex] = result with
                {
                    Outcome = EvidenceProducerOutcome.Invalid,
                    Artifacts = [],
                    Diagnostic = "A declared artifact failed final verification.",
                };
                continue;
            }

            finalized[resultIndex] = result with { Artifacts = artifacts };
        }

        return (valid, finalized);
    }

    private async ValueTask CleanRegistrationsAsync()
    {
        var failure = await CleanAsync().ConfigureAwait(false);
        if (failure is not null)
        {
            throw new InvalidOperationException("Evidence registration cleanup failed.");
        }
    }

    private static bool SamePlan(EvidencePlan left, EvidencePlan right) =>
        EvidenceCanonicalJson.Serialize(left).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(right));

    private static string? TerminalFailureCode(EvidenceWorkerTerminalCode terminalCode) => terminalCode switch
    {
        EvidenceWorkerTerminalCode.None => null,
        EvidenceWorkerTerminalCode.StageFailed => nameof(EvidenceWorkerTerminalCode.StageFailed),
        EvidenceWorkerTerminalCode.DeadlineExceeded => nameof(EvidenceWorkerTerminalCode.DeadlineExceeded),
        EvidenceWorkerTerminalCode.CallerCancelled => nameof(EvidenceWorkerTerminalCode.CallerCancelled),
        EvidenceWorkerTerminalCode.CleanupFailed => nameof(EvidenceWorkerTerminalCode.CleanupFailed),
        EvidenceWorkerTerminalCode.AdmissionClosed => nameof(EvidenceWorkerTerminalCode.AdmissionClosed),
        _ => "ASEVD410",
    };

    private async Task<string?> CleanAsync()
    {
        if (_cleaned)
        {
            return null;
        }

        State = EvidenceHostState.Cleaning;
        _cleaned = true;
        Exception? failure = null;
        var owned = _registration.Producers.Values.Reverse().Cast<object>()
            .Concat(_registration.Resources.Values.Reverse().Cast<object>())
            .Concat(_registration.AdditionalOwned.Reverse())
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

        return failure is null ? null : $"Evidence cleanup failed with {failure.GetType().Name}.";
    }

    private static bool IsNonFatalException(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private IReadOnlyList<EvidenceProducerResult> FailureForEveryProducer(EvidenceProducerOutcome outcome, string diagnostic) =>
        _plan.Profile.Producers.Select(producer => new EvidenceProducerResult(producer.Id, outcome, [], diagnostic)).ToArray();

    private static IReadOnlyList<EvidenceResourceDeclaration> OrderResources(IReadOnlyDictionary<string, EvidenceResourceDeclaration> declarations)
    {
        var ordered = new List<EvidenceResourceDeclaration>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in declarations.Values.OrderBy(static resource => resource.Id, StringComparer.Ordinal))
        {
            Visit(resource);
        }

        return ordered;

        void Visit(EvidenceResourceDeclaration resource)
        {
            if (visited.Contains(resource.Id))
            {
                return;
            }

            if (!visiting.Add(resource.Id))
            {
                throw new EvidenceHostException("ASEVD304", $"Resource dependency cycle includes '{resource.Id}'.", "Declare an acyclic resource dependency graph.");
            }

            foreach (var dependencyId in resource.Requires)
            {
                if (!declarations.TryGetValue(dependencyId, out var dependency))
                {
                    throw new EvidenceHostException("ASEVD305", $"Resource '{resource.Id}' requires undeclared resource '{dependencyId}'.", "Declare every required resource in the selected profile.");
                }

                Visit(dependency);
            }

            visiting.Remove(resource.Id);
            visited.Add(resource.Id);
            ordered.Add(resource);
        }
    }

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
