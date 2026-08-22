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
public sealed class EvidenceHostBootstrap : IAsyncDisposable
{
    private readonly EvidencePlan _plan;
    private readonly EvidenceHostRegistration _registration;
    private readonly EvidenceHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _artifactDirectory;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private bool _cleaned;

    private EvidenceHostBootstrap(
        EvidencePlan plan,
        EvidenceHostRegistration registration,
        EvidenceHostOptions options,
        TimeProvider timeProvider)
    {
        _plan = plan;
        _registration = registration;
        _options = options;
        _timeProvider = timeProvider;
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

        var registration = new EvidenceHostRegistration();
        configure(registration);
        return new EvidenceHostBootstrap(plan, registration, options ?? new EvidenceHostOptions(), timeProvider ?? TimeProvider.System);
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
                var readiness = await WaitForResourcesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<EnvelopeValidation> ValidateEnvelopeAsync(CancellationToken cancellationToken)
    {
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
    }

    private async Task<ResourceReadiness> WaitForResourcesAsync(CancellationToken cancellationToken)
    {
        var declarations = _plan.Profile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
        var results = new List<EvidenceResourceResult>();
        foreach (var resource in OrderResources(declarations))
        {
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(resource.DeadlineSeconds));
            try
            {
                // A registration should observe cancellation, but the host must also enforce its
                // declared deadline when a third-party probe fails to do so.
                var readinessTask = _registration.Resources[resource.Id].WaitUntilReadyAsync(deadline.Token);
                await readinessTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                results.Add(new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.Ready, timer.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.TimedOut, timer.ElapsedMilliseconds, $"Resource '{resource.Id}' did not become ready before its {resource.DeadlineSeconds}-second deadline."));
                return new ResourceReadiness(
                    results,
                    FailureForEveryProducer(EvidenceProducerOutcome.Unavailable, $"Resource '{resource.Id}' did not become ready before its {resource.DeadlineSeconds}-second deadline."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatalException(exception))
            {
                results.Add(new EvidenceResourceResult(resource.Id, EvidenceResourceOutcome.Unavailable, timer.ElapsedMilliseconds, $"Resource '{resource.Id}' readiness failed with {exception.GetType().Name}."));
                return new ResourceReadiness(
                    results,
                    FailureForEveryProducer(EvidenceProducerOutcome.Unavailable, $"Resource '{resource.Id}' readiness failed with {exception.GetType().Name}."));
            }
        }

        return new ResourceReadiness(results, null);
    }

    private async Task<IReadOnlyList<EvidenceProducerResult>> ProduceAsync(CancellationToken cancellationToken)
    {
        var results = new List<EvidenceProducerResult>();
        foreach (var declaration in _plan.Profile.Producers)
        {
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(declaration.TimeoutSeconds));
            try
            {
                var artifacts = new EvidenceArtifactWriter(declaration, Path.Join(_artifactDirectory, declaration.Id));
                // A registration should observe cancellation, but the host must also enforce its
                // declared deadline when a third-party producer fails to do so.
                var producerTask = _registration.Producers[declaration.Id]
                    .ProduceAsync(new EvidenceProducerContext(_plan, declaration, _timeProvider, artifacts), deadline.Token)
                    .AsTask();
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
            return null;
        }

        State = EvidenceHostState.Cleaning;
        _cleaned = true;
        Exception? failure = null;
        var owned = _registration.Producers.Values.Reverse().Cast<object>()
            .Concat(_registration.Resources.Values.Reverse().Cast<object>())
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
