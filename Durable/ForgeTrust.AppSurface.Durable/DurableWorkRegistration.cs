using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Carries one validated work item across the provider-to-application executor boundary.
/// </summary>
/// <remarks>
/// A provider creates this context only after validating its claim and execution fence. It is not an authorization
/// token by itself; a provider must record an effect permit against the same identity immediately before provider I/O.
/// </remarks>
public sealed record DurableWorkExecutionContext
{
    /// <summary>
    /// Initializes a validated work execution context.
    /// </summary>
    /// <param name="scopeId">Trusted owning scope.</param>
    /// <param name="workId">Claimed Work aggregate identity.</param>
    /// <param name="workName">Exact registered Work name.</param>
    /// <param name="workVersion">Exact registered Work version.</param>
    /// <param name="payload">Encoded Work input.</param>
    /// <param name="providerSafety">Declared external-effect ambiguity policy.</param>
    /// <param name="executionIdentity">Provider-validated attempt and fence identity.</param>
    /// <exception cref="ArgumentException">Thrown when an identifier is default or invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when payload or execution identity is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when provider safety is undefined.</exception>
    public DurableWorkExecutionContext(
        DurableScopeId scopeId,
        DurableWorkId workId,
        string workName,
        string workVersion,
        DurableEncodedPayload payload,
        DurableProviderSafety providerSafety,
        DurableWorkerExecutionIdentity executionIdentity)
    {
        DurableIdentifier.Require(scopeId.Value, nameof(scopeId), 200);
        DurableIdentifier.Require(workId.Value, nameof(workId), 200);
        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        ScopeId = scopeId;
        WorkId = workId;
        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ProviderSafety = providerSafety;
        ExecutionIdentity = executionIdentity ?? throw new ArgumentNullException(nameof(executionIdentity));
    }

    /// <summary>Gets the trusted owning scope.</summary>
    public DurableScopeId ScopeId { get; }

    /// <summary>Gets the immutable work aggregate identifier.</summary>
    public DurableWorkId WorkId { get; }

    /// <summary>Gets the registered work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the registered work version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the encoded work payload.</summary>
    public DurableEncodedPayload Payload { get; }

    /// <summary>Gets the provider ambiguity policy snapshot.</summary>
    public DurableProviderSafety ProviderSafety { get; }

    /// <summary>Gets the provider-validated execution identity and authorization fence.</summary>
    public DurableWorkerExecutionIdentity ExecutionIdentity { get; }
}

/// <summary>
/// Represents a locally validated work invocation that is ready for the runtime to record an external-effect permit.
/// </summary>
/// <remarks>
/// Preparation decodes the registered payload and resolves the executor before any permit exists. Calling
/// <see cref="InvokeAsync"/> is the provider-effect boundary and must happen only after the matching permit commits.
/// </remarks>
public abstract class DurablePreparedWork
{
    /// <summary>Executes the already prepared provider work and returns its encoded terminal result.</summary>
    /// <param name="cancellationToken">Token that cancels executor invocation.</param>
    /// <returns>The registered codec's encoded terminal result.</returns>
    /// <remarks>Call only after the matching external-effect permit commits.</remarks>
    public abstract ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes and invokes one registered durable work contract without reflection.
/// </summary>
public abstract class DurableWorkRegistration
{
    /// <summary>
    /// Initializes registration metadata.
    /// </summary>
    protected DurableWorkRegistration(
        string workName,
        string workVersion,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec workCodec,
        IDurablePayloadCodec resultCodec)
    {
        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        ProviderSafety = providerSafety;
        WorkCodec = workCodec ?? throw new ArgumentNullException(nameof(workCodec));
        ResultCodec = resultCodec ?? throw new ArgumentNullException(nameof(resultCodec));
    }

    /// <summary>Gets the stable work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the stable work contract version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the declared provider ambiguity policy.</summary>
    public DurableProviderSafety ProviderSafety { get; }

    /// <summary>Gets the work payload codec.</summary>
    public IDurablePayloadCodec WorkCodec { get; }

    /// <summary>Gets the result payload codec.</summary>
    public IDurablePayloadCodec ResultCodec { get; }

    /// <summary>Gets whether a side-effect-free reconciler is registered.</summary>
    public abstract bool CanReconcile { get; }

    /// <summary>
    /// Decodes and validates claimed bytes and resolves local executor dependencies without calling a provider.
    /// </summary>
    /// <param name="services">Application service provider used to resolve the executor.</param>
    /// <param name="work">Provider-validated execution context.</param>
    /// <returns>A prepared invocation that performs no provider I/O until invoked.</returns>
    public abstract DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work);

    /// <summary>
    /// Invokes the registered executor and encodes its terminal business result.
    /// </summary>
    /// <remarks>
    /// Runtime code must persist an effect permit before calling this method. Exceptions after that permit represent a
    /// potentially applied external effect and must be handled according to <see cref="ProviderSafety"/>.
    /// </remarks>
    /// <param name="services">Application service provider used to resolve the executor.</param>
    /// <param name="work">Provider-validated execution context.</param>
    /// <param name="cancellationToken">Token that cancels executor invocation.</param>
    /// <returns>The registered codec's encoded terminal result.</returns>
    public abstract ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles an unknown provider outcome without repeating the external mutation.
    /// </summary>
    /// <param name="services">Application service provider used to resolve the reconciler.</param>
    /// <param name="work">Provider-validated execution context.</param>
    /// <param name="cancellationToken">Token that cancels reconciliation.</param>
    /// <returns>Applied, not-applied, or unknown provider truth.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no reconciler is registered or reconciliation returns invalid data.</exception>
    public abstract ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reflection-free registration for one typed durable worker executor.
/// </summary>
/// <typeparam name="TWork">Executor work type.</typeparam>
/// <typeparam name="TResult">Executor terminal result type.</typeparam>
/// <typeparam name="TExecutor">Registered executor implementation.</typeparam>
public sealed class DurableWorkRegistration<TWork, TResult, TExecutor> : DurableWorkRegistration
    where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
{
    private readonly IDurablePayloadCodec<TWork> _workCodec;
    private readonly IDurablePayloadCodec<TResult> _resultCodec;
    private readonly Func<IServiceProvider, IDurableEffectReconciler<TWork, TResult>>? _reconcilerFactory;

    /// <summary>
    /// Initializes a typed durable work registration.
    /// </summary>
    /// <param name="workName">Stable registered Work name.</param>
    /// <param name="workVersion">Immutable registered Work version.</param>
    /// <param name="providerSafety">External-effect ambiguity policy.</param>
    /// <param name="workCodec">Allowlisted Work input codec.</param>
    /// <param name="resultCodec">Allowlisted terminal result codec.</param>
    /// <param name="reconcilerFactory">Optional side-effect-free reconciler resolver.</param>
    /// <exception cref="ArgumentException">Thrown when identifiers are invalid or reconcile-before-retry lacks a reconciler.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a codec is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when provider safety is undefined.</exception>
    public DurableWorkRegistration(
        string workName,
        string workVersion,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec<TWork> workCodec,
        IDurablePayloadCodec<TResult> resultCodec,
        Func<IServiceProvider, IDurableEffectReconciler<TWork, TResult>>? reconcilerFactory = null)
        : base(workName, workVersion, providerSafety, workCodec, resultCodec)
    {
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry && reconcilerFactory is null)
        {
            throw new ArgumentException(
                "ReconcileBeforeRetry work requires a registered side-effect-free reconciler.",
                nameof(reconcilerFactory));
        }

        _workCodec = workCodec;
        _resultCodec = resultCodec;
        _reconcilerFactory = reconcilerFactory;
    }

    /// <inheritdoc />
    public override bool CanReconcile => _reconcilerFactory is not null;

    /// <inheritdoc />
    public override async ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        await Prepare(services, work).InvokeAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(work);
        var envelope = CreateEnvelope(work);
        var executor = services.GetRequiredService<TExecutor>();
        return new PreparedInvocation(executor, envelope, _resultCodec);
    }

    /// <inheritdoc />
    public override async ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(work);
        var reconcilerFactory = _reconcilerFactory
            ?? throw new InvalidOperationException("This durable work registration has no provider reconciler.");
        var outcome = await reconcilerFactory(services)
            .ReconcileAsync(CreateEnvelope(work), cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(outcome);
        var encoded = outcome.Kind == DurableEffectReconciliationKind.Applied
            ? _resultCodec.Encode(outcome.Result!)
            : null;
        return new DurableEncodedEffectReconciliation(outcome.Kind, encoded);
    }

    private DurableWorkerEnvelope<TWork> CreateEnvelope(DurableWorkExecutionContext work)
    {
        if (!string.Equals(work.WorkName, WorkName, StringComparison.Ordinal)
            || !string.Equals(work.WorkVersion, WorkVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claimed work does not match the selected durable work registration.");
        }

        if (work.ProviderSafety != ProviderSafety)
        {
            throw new InvalidOperationException("Claimed work provider-safety snapshot does not match its registration.");
        }

        var payload = _workCodec.Decode(work.Payload);
        var executionIdentity = work.ExecutionIdentity;
        var correlation = new DurableWorkerCorrelation(
            WorkName,
            work.WorkId.Value,
            executionIdentity.ActivityId,
            $"{executionIdentity.AttemptNumber}:{executionIdentity.LeaseGeneration}");
        return new DurableWorkerEnvelope<TWork>(
            DurableWorkerProjectionOutcome.Claimed,
            "durable.claimed",
            DurableWorkerRetryability.Retryable,
            correlation,
            payload,
            executionIdentity: executionIdentity);
    }

    private sealed class PreparedInvocation(
        TExecutor executor,
        DurableWorkerEnvelope<TWork> envelope,
        IDurablePayloadCodec<TResult> resultCodec) : DurablePreparedWork
    {
        public override async ValueTask<DurableEncodedPayload> InvokeAsync(
            CancellationToken cancellationToken = default)
        {
            var result = await executor.ExecuteAsync(envelope, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(result);
            return resultCodec.Encode(result);
        }
    }
}

/// <summary>
/// Resolves durable work registrations by their stable name and version.
/// </summary>
public interface IDurableWorkRegistry
{
    /// <summary>
    /// Gets a required registration or throws before work is accepted or claimed.
    /// </summary>
    /// <param name="workName">Exact registered Work name.</param>
    /// <param name="workVersion">Exact immutable Work version.</param>
    /// <returns>The matching registration.</returns>
    /// <exception cref="ArgumentException">Thrown when an identifier is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no exact registration exists.</exception>
    DurableWorkRegistration GetRequired(string workName, string workVersion);
}

/// <summary>
/// Immutable registry of durable work registrations built during host startup.
/// </summary>
public sealed class DurableWorkRegistry : IDurableWorkRegistry
{
    private readonly IReadOnlyDictionary<(string Name, string Version), DurableWorkRegistration> _registrations;

    /// <summary>
    /// Initializes a registry and rejects duplicate contract identities.
    /// </summary>
    /// <param name="registrations">Complete registrations available at host startup.</param>
    /// <exception cref="ArgumentNullException">Thrown when the sequence or an element is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a Work name/version appears more than once.</exception>
    public DurableWorkRegistry(IEnumerable<DurableWorkRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var map = new Dictionary<(string Name, string Version), DurableWorkRegistration>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var key = (registration.WorkName, registration.WorkVersion);
            if (!map.TryAdd(key, registration))
            {
                throw new InvalidOperationException(
                    $"Durable work '{registration.WorkName}' version '{registration.WorkVersion}' is registered more than once.");
            }
        }

        _registrations = map;
    }

    /// <inheritdoc />
    public DurableWorkRegistration GetRequired(string workName, string workVersion)
    {
        var name = DurableIdentifier.Require(workName, nameof(workName), 200);
        var version = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        return _registrations.TryGetValue((name, version), out var registration)
            ? registration
            : throw new InvalidOperationException($"Durable work '{name}' version '{version}' is not registered.");
    }
}

/// <summary>
/// Registration helpers for typed durable worker executors.
/// </summary>
public static class DurableServiceCollectionExtensions
{
    /// <summary>
    /// Registers one versioned work contract, its allowlisted codecs, and its transient executor.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="workName">Stable Work name.</param>
    /// <param name="workVersion">Immutable Work version.</param>
    /// <param name="providerSafety">External-effect ambiguity policy; use the reconciler overload for reconcile-before-retry.</param>
    /// <param name="workCodec">Allowlisted Work codec.</param>
    /// <param name="resultCodec">Allowlisted terminal result codec.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentException">Thrown when the registration is invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when services or a codec is null.</exception>
    public static IServiceCollection AddDurableWork<TWork, TResult, TExecutor>(
        this IServiceCollection services,
        string workName,
        string workVersion,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec<TWork> workCodec,
        IDurablePayloadCodec<TResult> resultCodec)
        where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        var registration = new DurableWorkRegistration<TWork, TResult, TExecutor>(
            workName,
            workVersion,
            providerSafety,
            workCodec,
            resultCodec);
        services.AddSingleton<IDurablePayloadCodec>(workCodec);
        services.AddSingleton<IDurablePayloadCodec>(resultCodec);
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddTransient<TExecutor>();
        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        return services;
    }

    /// <summary>
    /// Registers provider work that must reconcile an unknown effect before any retry.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="workName">Stable Work name.</param>
    /// <param name="workVersion">Immutable Work version.</param>
    /// <param name="workCodec">Allowlisted Work codec.</param>
    /// <param name="resultCodec">Allowlisted terminal result codec.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>Use this overload for <see cref="DurableProviderSafety.ReconcileBeforeRetry"/> so unknown effects are reconciled before retry.</remarks>
    /// <exception cref="ArgumentException">Thrown when the registration is invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when services or a codec is null.</exception>
    public static IServiceCollection AddDurableWorkWithReconciler<TWork, TResult, TExecutor, TReconciler>(
        this IServiceCollection services,
        string workName,
        string workVersion,
        IDurablePayloadCodec<TWork> workCodec,
        IDurablePayloadCodec<TResult> resultCodec)
        where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
        where TReconciler : class, IDurableEffectReconciler<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        var registration = new DurableWorkRegistration<TWork, TResult, TExecutor>(
            workName,
            workVersion,
            DurableProviderSafety.ReconcileBeforeRetry,
            workCodec,
            resultCodec,
            provider => provider.GetRequiredService<TReconciler>());
        services.AddSingleton<IDurablePayloadCodec>(workCodec);
        services.AddSingleton<IDurablePayloadCodec>(resultCodec);
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddTransient<TExecutor>();
        services.AddTransient<TReconciler>();
        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        return services;
    }

    /// <summary>
    /// Registers one immutable Flow definition, its approved context codec, and its durable activity bindings.
    /// </summary>
    /// <typeparam name="TContext">Flow context type.</typeparam>
    /// <param name="services">Application service collection.</param>
    /// <param name="definition">Immutable Flow definition and version.</param>
    /// <param name="contextCodec">Allowlisted durable context codec.</param>
    /// <param name="implementationVersion">
    /// Application-owned version for node implementation semantics not represented by topology or contract metadata.
    /// Change it whenever executable node behavior changes without changing the Flow definition version.
    /// </param>
    /// <param name="activityBindings">Optional registered activity bindings used by the definition.</param>
    /// <param name="eventBindings">Optional typed external-event bindings used by the definition.</param>
    /// <param name="evaluator">Optional one-node evaluator override.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddDurableFlow<TContext>(
        this IServiceCollection services,
        ForgeTrust.AppSurface.Flow.FlowDefinition<TContext> definition,
        IDurablePayloadCodec<TContext> contextCodec,
        string implementationVersion,
        IEnumerable<DurableFlowActivityBinding<TContext>>? activityBindings = null,
        IEnumerable<DurableFlowEventBinding>? eventBindings = null,
        ForgeTrust.AppSurface.Flow.IFlowTransitionEvaluator<TContext>? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var durableEventBindings = eventBindings?.ToArray() ?? [];
        var registration = new DurableFlowRegistration<TContext>(
            definition,
            contextCodec,
            implementationVersion,
            evaluator ?? new ForgeTrust.AppSurface.Flow.FlowTransitionEvaluator<TContext>(),
            activityBindings,
            durableEventBindings);
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        foreach (var eventBinding in durableEventBindings)
        {
            services.AddSingleton(eventBinding.PayloadCodec);
        }

        services.AddSingleton<DurableFlowRegistration>(registration);
        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        services.TryAddSingleton<IDurableFlowRegistry, DurableFlowRegistry>();
        return services;
    }
}
