using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

public sealed partial class EvidenceHostBootstrap
{
    /// <summary>Prepares local receipt decisions before the consumer composes or starts any required resources.</summary>
    /// <param name="plan">Digest-bound immutable selected plan.</param>
    /// <param name="configure">Side-effect-free producer, resource identity and envelope registrations.</param>
    /// <param name="options">Optional reuse, output and cleanup constraints.</param>
    /// <param name="timeProvider">Clock for callback deadlines and current invocation measurements.</param>
    /// <param name="cancellationToken">Preparation cancellation; actual owned work remains retained until settlement.</param>
    /// <returns>An owned single-use prepared run exposing the exact fresh resource closure.</returns>
    /// <remarks>
    /// Register immutable execution and fixture identities without building an Aspire application. Compose only the
    /// identifiers passed to the RunAsync factory. Dispose the prepared run even when execution is not requested.
    /// Integrated local reuse is informational and cannot provide a protected CI or release claim.
    /// </remarks>
    public static Task<EvidencePreparedRun> PrepareAsync(EvidencePlan plan,
        Action<EvidenceHostPreparationRegistration> configure, EvidenceHostOptions? options = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
        => EvidencePreparedRun.CreateAsync(plan, configure, options ?? new(), timeProvider ?? TimeProvider.System, cancellationToken);
}

/// <summary>Owns a frozen preparation and its single execution through actual resource, producer and child cleanup.</summary>
/// <remarks>
/// A returned incomplete manifest does not terminate in-process code. If callbacks, factory work or disposal exceed
/// a bounded wait, this run retains their actual tasks and native key leases. DisposeAsync joins that same cleanup.
/// Unknown abandoned execution authority is preserved; never use PID, age or lock release to erase it.
/// </remarks>
public sealed class EvidencePreparedRun : IAsyncDisposable
{
    private readonly EvidencePlan _plan;
    private readonly EvidenceHostOptions _options;
    private readonly TimeProvider _clock;
    private readonly EvidenceHostPreparationRegistration _registration;
    private readonly IReadOnlyDictionary<string, PreparedProducerRegistration> _producers;
    private readonly EvidenceProducerReuseSession _session;
    private readonly EvidenceEnvelopeStatus _envelopeStatus;
    private readonly object _cleanupSync = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly CancellationTokenSource _factoryCancellation = new();
    private Task<EvidenceHostResourceScope>? _factoryTask;
    private EvidenceHostResourceScope? _scope;
    private EvidenceHostBootstrap? _host;
    private Task<bool>? _cleanup;
    private bool _used;
    private bool _disposed;

    private EvidencePreparedRun(EvidencePlan plan, EvidenceHostOptions options, TimeProvider clock,
        EvidenceHostPreparationRegistration registration, IReadOnlyDictionary<string, PreparedProducerRegistration> producers,
        EvidenceProducerReuseSession session, EvidenceEnvelopeStatus envelopeStatus, IReadOnlyDictionary<string, string> artifactDirectories)
    {
        (_plan, _options, _clock, _registration, _producers, _session, _envelopeStatus) =
            (plan, options, clock, registration, producers, session, envelopeStatus);
        ArtifactDirectories = artifactDirectories;
        RequiredResourceIds = Array.AsReadOnly((plan.ContractVersion == "1.0"
            ? plan.Profile.Resources.Select(static resource => resource.Id)
            : ResourceClosure(plan, plan.Profile.Producers.Where(producer => !session.ReusedResults.ContainsKey(producer.Id))))
            .Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the immutable sorted resource IDs that this prepared run must start.</summary>
    /// <remarks>
    /// Contract 1.0 includes every declared profile resource, including unused resources, to preserve legacy resource
    /// reporting. Contract 1.1 includes the transitive closure required by fresh producers; verified restored
    /// producers contribute no startup requirement. The factory is not invoked when the resulting list is empty.
    /// </remarks>
    public IReadOnlyList<string> RequiredResourceIds { get; }

    /// <summary>Gets the frozen original plan used for current obligations and digest binding.</summary>
    public EvidencePlan Plan => _plan;

    /// <summary>Gets the resolved current invocation output root.</summary>
    public string ArtifactDirectory => _options.ArtifactDirectory!;

    /// <summary>Gets each producer's controlled artifact root, indexed by its unchanged declaration identifier.</summary>
    /// <remarks>Producer identifiers are opaque; consumers must use this map rather than treating an identifier as a filesystem path.</remarks>
    public IReadOnlyDictionary<string, string> ArtifactDirectories { get; }

    /// <summary>Gets bounded current producer decisions with stable reasons and safe recovery guidance.</summary>
    public IReadOnlyList<EvidenceReuseDecision> Decisions => Array.AsReadOnly(_session.Decisions.Select(static decision =>
        new EvidenceReuseDecision(decision.ProducerId, decision.Status switch
        {
            EvidenceProducerReuseStatus.Hit => EvidenceReuseStatus.Hit,
            EvidenceProducerReuseStatus.Miss => EvidenceReuseStatus.Miss,
            EvidenceProducerReuseStatus.Ineligible => EvidenceReuseStatus.Ineligible,
            _ => EvidenceReuseStatus.Unavailable,
        }, decision.Reason, decision.Diagnostic, decision.ProducerKey, decision.OriginCompletedAt, decision.OriginElapsedMilliseconds)).ToArray());

    internal static async Task<EvidencePreparedRun> CreateAsync(EvidencePlan plan,
        Action<EvidenceHostPreparationRegistration> configure, EvidenceHostOptions options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.VerifyReuse && !options.Reuse)
            throw new EvidenceHostException("reuse.verify_requires_reuse", "A reuse audit requires opt-in reuse.", "Enable Reuse or remove VerifyReuse.");
        if (options.CapturedPatch is { Length: > 20 * 1024 * 1024 })
            throw new ArgumentOutOfRangeException(nameof(options), "The captured patch exceeds the existing twenty-MiB bound.");
        if (options.ProducerCleanupTimeout <= TimeSpan.Zero || options.ProducerCleanupTimeout > TimeSpan.FromMinutes(1)
            || options.ReceiptWaitTimeout <= TimeSpan.Zero || options.ReceiptWaitTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "Cleanup must be positive and at most one minute; receipt waiting must be finite and positive.");
        if (options.Reuse && (options.RequireTrustedEnvelope || plan.Profile.Scope == EvidenceProfileScope.Release
            || string.Equals(options.EnvironmentReader("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)))
            throw new EvidenceHostException("reuse.local_only", "Reuse cannot supply protected CI or release evidence.", "Run the eager or prepared host fresh without reuse.");

        var frozen = FreezePlan(plan);
        var registration = new EvidenceHostPreparationRegistration();
        EvidenceProducerReuseSession? session = null;
        EvidencePreparedRun? prepared = null;
        try
        {
            configure(registration);
            var producers = registration.Freeze();
            ValidateRegistrations(frozen, registration, producers);
            var envelopeStatus = EvidenceEnvelopeStatus.NotRequired;
            if (options.RequireTrustedEnvelope || frozen.Profile.Scope == EvidenceProfileScope.Release)
            {
                if (registration.EnvelopeVerifier is null)
                    throw new EvidenceHostException("ASEVD302", "Trusted preparation has no execution-envelope verifier.", "Register the protected CI verifier before preparation.");
                var envelope = await registration.EnvelopeVerifier.VerifyAsync(frozen, cancellationToken).ConfigureAwait(false);
                if (!envelope.Accepted)
                    throw new EvidenceHostException("reuse.envelope_invalid", "The explicit execution envelope was not accepted.", "Fix the envelope before starting resources.");
                envelopeStatus = EvidenceEnvelopeStatus.ValidatedNotAttested;
            }
            var output = options.Reuse ? EvidenceReuseOutput.CreateFreshRoot(options.ArtifactDirectory, Path.Join("TestResults", "evidence"))
                : Path.GetFullPath(options.ArtifactDirectory ?? Path.Join("TestResults", "evidence", "artifacts"));
            // Preserve absence explicitly; null can otherwise convert through byte[] to non-null empty memory.
            options = options with { ArtifactDirectory = output, CapturedPatch = options.CapturedPatch is { } captured ? new ReadOnlyMemory<byte>(captured.ToArray()) : (ReadOnlyMemory<byte>?)null };
            var bindings = producers.ToDictionary(static pair => pair.Key, static pair =>
                new EvidenceReuseProducerRegistration(pair.Value.Producer, pair.Value.ImplementationIdentity, pair.Value.ToolIdentity), StringComparer.Ordinal);
            var artifactDirectories = new ReadOnlyDictionary<string, string>(frozen.Profile.Producers.ToDictionary(static producer => producer.Id,
                producer => Path.Join(output, "producers", EvidenceDigest.Sha256(Encoding.UTF8.GetBytes(producer.Id))), StringComparer.Ordinal));
            session = await EvidenceProducerReuseSession.PrepareAsync(frozen, bindings, registration.ResourceIdentities,
                new(options.Reuse, Path.GetFullPath(options.RepositoryRoot ?? GitRepositoryRootResolver.FindRepositoryRoot(Directory.GetCurrentDirectory())), options.ReceiptStore,
                    options.ReceiptWaitTimeout, options.ProducerCleanupTimeout, options.VerifyReuse, false, options.CapturedPatch, clock)
                {
                    ForbiddenRoots = [output],
                    GetEnvironmentVariable = options.EnvironmentReader,
                    WaitProgress = options.ReceiptWaitProgress is { } progress
                        ? (prefix, elapsed, remaining) => progress.Report(new(prefix, elapsed, remaining)) : null,
                }, producer => artifactDirectories[producer.Id], cancellationToken).ConfigureAwait(false);
            prepared = new EvidencePreparedRun(frozen, options, clock, registration, producers, session, envelopeStatus, artifactDirectories);
            if (session.IsCancelled)
            {
                prepared.ObserveCleanup();
                throw new OperationCanceledException(cancellationToken);
            }
            return prepared;
        }
        catch
        {
            if (prepared is null) await DisposeUntransferredAsync(registration, session).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeUntransferredAsync(EvidenceHostPreparationRegistration registration, EvidenceProducerReuseSession? session)
    {
        Exception? fatal = null;
        if (session is not null)
        {
            try { await session.JoinKeyMaterialAsync().ConfigureAwait(false); }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception)) { }
            catch (Exception exception) { fatal = exception; }
        }
        foreach (var owner in registration.Freeze().Values.Select(static value => (object)value.Producer).Distinct(ReferenceEqualityComparer.Instance))
        {
            try
            {
                if (owner is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
                else if (owner is IDisposable synchronous) synchronous.Dispose();
            }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception)) { }
            catch (Exception exception) { fatal ??= exception; }
        }
        if (session is not null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception)) { }
            catch (Exception exception) { fatal ??= exception; }
        }
        if (fatal is not null) ExceptionDispatchInfo.Capture(fatal).Throw();
    }

    /// <summary>Runs once, creating only the exact resource set that fresh producers require.</summary>
    /// <param name="createResources">Factory transferring the requested readiness instances and application owner; partial construction remains factory-owned.</param>
    /// <param name="observationOnly">Whether an otherwise fresh completion is informational.</param>
    /// <param name="cancellationToken">Invocation cancellation, forwarded to actual factory and producer work.</param>
    /// <returns>The current digest-bound manifest after bounded execution and cleanup waits.</returns>
    /// <remarks>
    /// Host admission and disposal share a handoff: cancellation that wins before admission retains any returned
    /// scope for cleanup without starting readiness or producers. Disposal after admission joins the owned run.
    /// </remarks>
    public async Task<EvidenceManifest> RunAsync(
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<EvidenceHostResourceScope>> createResources,
        bool observationOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createResources);
        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        var executionStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_used) throw new InvalidOperationException("Prepared Evidence runs execute exactly once.");
            _used = true;
            if (_producers.Any(pair => pair.Value.Producer.Id != pair.Key))
                throw new EvidenceHostException("reuse.registration_changed", "A producer identifier changed after preparation.", "Keep registered identifiers immutable and prepare a new run.");
            if (!await _session.BeginExecutionAsync(cancellationToken).ConfigureAwait(false))
                return FailureManifest(_session.IsCancelled ? EvidenceProducerOutcome.Cancelled : EvidenceProducerOutcome.Unavailable,
                    "reuse.preparation_unavailable: Preparation could not safely authorize execution; inspect Decisions before retrying.", observationOnly);
            executionStarted = true;
            using var cancellation = cancellationToken.Register(_factoryCancellation.Cancel);
            if (RequiredResourceIds.Count != 0)
            {
                // Own synchronous factory work before its ValueTask is returned as well as asynchronous startup.
                _factoryTask = Task.Run(async () => await createResources(RequiredResourceIds, _factoryCancellation.Token).ConfigureAwait(false));
                _scope = await _factoryTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (_scope is null || !_scope.Resources.Keys.Order(StringComparer.Ordinal).SequenceEqual(RequiredResourceIds, StringComparer.Ordinal)
                    || _scope.Resources.Any(pair => pair.Value.Id != pair.Key))
                    throw new EvidenceHostException("reuse.resource_scope_invalid", "The factory transferred missing, unrequested, or changed readiness bindings.", "Transfer exactly the requested resource identifiers and its application owner.");
            }
            var executionRegistration = new EvidenceHostRegistration();
            foreach (var producer in _producers.Values) executionRegistration.AddProducer(producer.Producer);
            if (_scope is not null)
                foreach (var resource in _scope.Resources.Values) executionRegistration.AddResource(resource);
            lock (_cleanupSync)
            {
                // Disposal and host admission share this handoff. A factory may return a scope after cancellation;
                // retain that scope for cleanup without admitting readiness or producer work when disposal won.
                _factoryCancellation.Token.ThrowIfCancellationRequested();
                _host = EvidenceHostBootstrap.CreatePreparedExecution(_plan, executionRegistration, _options, _clock,
                    _session.ReusedResults, RequiredResourceIds.ToHashSet(StringComparer.Ordinal), _scope,
                    _session.GetWriter, _session.BeforeCleanupAsync, _envelopeStatus, _session.ExecutionId,
                    id => _session.GetChildLifetime(id)?.Environment);
            }
            var manifest = await _host.RunAsync(observationOnly, cancellationToken).ConfigureAwait(false);
            return await _session.CompleteAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _factoryCancellation.IsCancellationRequested)
        {
            _factoryCancellation.Cancel();
            return FailureManifest(EvidenceProducerOutcome.Cancelled, "reuse.cancelled: The invocation was cancelled; actual factory and cleanup ownership remains retained.", observationOnly);
        }
        catch (Exception exception) when (executionStarted && EvidenceHostBootstrap.IsNonFatalException(exception) && exception is not EvidenceHostException)
        {
            return FailureManifest(EvidenceProducerOutcome.Unavailable,
                $"reuse.execution_unavailable: Prepared resource startup or execution failed ({exception.GetType().Name}). Fix that local failure and retry; owned work remains retained until cleanup settles.", observationOnly);
        }
        finally
        {
            ObserveCleanup();
            _execution.Release();
        }
    }

    private EvidenceManifest FailureManifest(EvidenceProducerOutcome outcome, string diagnostic, bool observationOnly)
    {
        var results = _plan.Profile.Producers.Select(producer => _session.ReusedResults.GetValueOrDefault(producer.Id)
            ?? new EvidenceProducerResult(producer.Id, outcome, [], diagnostic)).ToArray();
        var resources = _plan.Profile.Resources.Select(resource => new EvidenceResourceResult(resource.Id,
            RequiredResourceIds.Contains(resource.Id, StringComparer.Ordinal) ? EvidenceResourceOutcome.Unavailable : EvidenceResourceOutcome.NotRequiredForExecution,
            0, RequiredResourceIds.Contains(resource.Id, StringComparer.Ordinal) ? diagnostic : null)).ToArray();
        return EvidenceManifestBuilder.Build(_plan, results, observationOnly, _envelopeStatus, resources,
            new EvidenceExecutionMetrics(CleanupCompleted: false, CleanupDiagnostic: "Actual owned work is retained until settlement; inspect Decisions."));
    }

    private void ObserveCleanup()
    {
        Task<bool> cleanup;
        lock (_cleanupSync) cleanup = _cleanup ??= CleanupAsync();
        _ = cleanup.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<bool> CleanupAsync()
    {
        Exception? primary = null;
        var settled = true;
        try { await _session.JoinKeyMaterialAsync().ConfigureAwait(false); }
        catch (Exception exception) { primary = exception; settled = false; }
        if (_factoryTask is not null)
        {
            try { _scope ??= await _factoryTask.ConfigureAwait(false); }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception)) { /* Factory owns its partial construction on failure. */ }
            catch (Exception exception) { primary ??= exception; settled = false; }
        }
        // A bounded producer return may leave an enrolled child using the application's resources.
        // Join its actual handler before disposing any producer registration or resource scope.
        try { settled &= await _session.JoinActualTasksAsync().ConfigureAwait(false); }
        catch (Exception exception) { primary ??= exception; settled = false; }
        try
        {
            if (_host is not null) settled &= await _host.JoinPreparedOwnershipAsync(_session.AreChildrenSettled).ConfigureAwait(false);
            else if (_session.AreChildrenSettled)
            {
                foreach (var owner in _producers.Values.Select(static producer => (object)producer.Producer).Distinct(ReferenceEqualityComparer.Instance))
                {
                    try
                    {
                        if (owner is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
                        else if (owner is IDisposable synchronous) synchronous.Dispose();
                    }
                    catch (Exception exception) { primary ??= exception; settled = false; }
                }
                if (_scope is not null) await _scope.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) { primary ??= exception; settled = false; }
        try { await _session.CompleteUnsuccessfulSettlementAsync(settled).ConfigureAwait(false); }
        finally { await _session.DisposeAsync().ConfigureAwait(false); }
        if (primary is not null && !EvidenceHostBootstrap.IsNonFatalException(primary)) ExceptionDispatchInfo.Capture(primary).Throw();
        return settled;
    }

    /// <summary>Requests factory cancellation and joins actual cleanup before releasing execution ownership.</summary>
    /// <returns>The actual cleanup operation; this does not interpret a grace timeout as termination.</returns>
    /// <remarks>Cancellation is requested before joining an active run so a token-cooperative factory can stop.</remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_cleanupSync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _factoryCancellation.Cancel();
            }
        }
        await _execution.WaitAsync().ConfigureAwait(false);
        try { ObserveCleanup(); }
        finally { _execution.Release(); }
        await _cleanup!.ConfigureAwait(false);
        lock (_cleanupSync) _factoryCancellation.Dispose();
    }

    private static void ValidateRegistrations(EvidencePlan plan, EvidenceHostPreparationRegistration registration,
        IReadOnlyDictionary<string, PreparedProducerRegistration> producers)
    {
        if (plan.Profile.Resources.Count > EvidenceProfileLimits.MaximumResources
            || plan.Profile.Producers.Count > EvidenceProfileLimits.MaximumProducers || plan.Profile.Obligations.Count > EvidenceProfileLimits.MaximumObligations)
            throw new EvidenceHostException("ASEVD301", "The plan exceeds Evidence profile limits.", "Split it into bounded profiles.");
        if (!plan.Profile.Producers.Select(static producer => producer.Id).Order(StringComparer.Ordinal).SequenceEqual(producers.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || producers.Any(pair => pair.Value.Producer.Id != pair.Key))
            throw new EvidenceHostException("ASEVD303", "Every selected producer needs one explicit immutable registration.", "Register exactly the selected producers before preparation.");
        if (!plan.Profile.Resources.Select(static resource => resource.Id).Order(StringComparer.Ordinal).SequenceEqual(registration.ResourceIdentities.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new EvidenceHostException("ASEVD302", "Every selected resource needs one explicit fixture identity, including unused resources.", "Register exact resource identities before preparation; do not start readiness instances here.");
        _ = ResourceClosure(plan, plan.Profile.Producers).ToArray();
        // Validate unused graph branches too: an all-hit run must not bypass invalid descriptors.
        _ = ResourceClosure(plan, [new EvidenceProducerDeclaration("validation", "validation", "1", plan.Profile.Resources.Select(static resource => resource.Id).ToArray(), [], [], 1)]).ToArray();
    }

    private static IReadOnlySet<string> ResourceClosure(EvidencePlan plan, IEnumerable<EvidenceProducerDeclaration> producers)
    {
        var resources = plan.Profile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in producers.SelectMany(static producer => producer.RequiredResources)) Visit(id);
        return visited;
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!resources.TryGetValue(id, out var resource) || !visiting.Add(id))
                throw new EvidenceHostException("ASEVD304", "The resource graph is missing a dependency or contains a cycle.", "Declare an acyclic complete resource graph before preparation.");
            foreach (var dependency in resource.Requires) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static EvidencePlan FreezePlan(EvidencePlan plan)
    {
        var frozen = EvidenceCanonicalJson.Deserialize<EvidencePlan>(EvidenceCanonicalJson.Serialize(plan));
        if (!EvidenceManifestBuilder.Verify(frozen, EvidenceManifestBuilder.Build(frozen, [])))
            throw new EvidenceHostException("reuse.plan_invalid", "The supplied plan or policy digest is invalid.", "Resolve a current policy into a digest-bound plan before preparation.");
        var resolved = new EvidencePlanner().Resolve(frozen.PolicySnapshot!, frozen.ChangedPaths);
        if (frozen.PatchSha256 is not null)
        {
            resolved = resolved with { PatchSha256 = frozen.PatchSha256, PlanDigest = string.Empty };
            resolved = resolved with { PlanDigest = EvidenceDigest.CanonicalSha256(resolved) };
        }
        if (!string.Equals(resolved.PlanDigest, frozen.PlanDigest, StringComparison.Ordinal))
            throw new EvidenceHostException("reuse.plan_invalid", "The plan is inconsistent with its current policy resolution.", "Resolve the complete policy before preparation; do not manufacture selected profile or claim fields.");
        static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) => new ReadOnlyCollection<T>(values.ToArray());
        static EvidenceProducerDeclaration Producer(EvidenceProducerDeclaration producer) => producer with
        {
            RequiredResources = Copy(producer.RequiredResources),
            AssertionIds = Copy(producer.AssertionIds),
            ArtifactSlots = Copy(producer.ArtifactSlots),
            Inputs = producer.Inputs is { } inputs ? new(Copy(inputs.Include), Copy(inputs.Exclude)) : null,
        };
        static EvidenceProfile Profile(EvidenceProfile profile) => profile with
        {
            Producers = Copy(profile.Producers.Select(Producer)),
            Resources = Copy(profile.Resources.Select(resource => resource with { Requires = Copy(resource.Requires) })),
            Obligations = Copy(profile.Obligations.Select(obligation => obligation with { RequiredProducerIds = Copy(obligation.RequiredProducerIds) })),
        };
        return frozen with
        {
            Profile = Profile(frozen.Profile),
            ChangedPaths = Copy(frozen.ChangedPaths),
            MatchedRuleIds = Copy(frozen.MatchedRuleIds),
            PolicySnapshot = frozen.PolicySnapshot! with { Profiles = Copy(frozen.PolicySnapshot!.Profiles.Select(Profile)), Rules = Copy(frozen.PolicySnapshot.Rules) },
        };
    }
}
