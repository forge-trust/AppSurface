using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Supplies persisted input for exactly one registered Flow node evaluation.
/// </summary>
public sealed record DurableFlowEvaluationInput
{
    /// <summary>
    /// Initializes a persisted Flow evaluation input.
    /// </summary>
    public DurableFlowEvaluationInput(
        string nodeId,
        DurableEncodedPayload context,
        string? resumeEventName = null,
        DurableEncodedPayload? resumeEventPayload = null,
        bool isTimeout = false,
        string? activityCallsiteId = null,
        DurableEncodedPayload? activityResult = null)
    {
        var hasEvent = resumeEventName is not null || resumeEventPayload is not null || isTimeout;
        var hasActivity = activityCallsiteId is not null || activityResult is not null;
        if (hasEvent && hasActivity)
        {
            throw new ArgumentException("A Flow evaluation cannot carry both event and activity resume input.");
        }

        if ((resumeEventPayload is not null || isTimeout) && resumeEventName is null)
        {
            throw new ArgumentException("A Flow event payload or timeout requires an event name.", nameof(resumeEventName));
        }

        if ((activityCallsiteId is null) != (activityResult is null))
        {
            throw new ArgumentException("An activity resume requires both callsite identity and encoded result.", nameof(activityResult));
        }

        NodeId = DurableIdentifier.Require(nodeId, nameof(nodeId), 200);
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ResumeEventName = resumeEventName is null
            ? null
            : DurableIdentifier.Require(resumeEventName, nameof(resumeEventName), 200);
        ResumeEventPayload = resumeEventPayload;
        IsTimeout = isTimeout;
        ActivityCallsiteId = activityCallsiteId is null
            ? null
            : DurableIdentifier.Require(activityCallsiteId, nameof(activityCallsiteId), 200);
        ActivityResult = activityResult;
    }

    /// <summary>Gets the node to evaluate.</summary>
    public string NodeId { get; }

    /// <summary>Gets the persisted Flow context.</summary>
    public DurableEncodedPayload Context { get; }

    /// <summary>Gets the external event name, if any.</summary>
    public string? ResumeEventName { get; }

    /// <summary>Gets the allowlisted external event payload, if any.</summary>
    public DurableEncodedPayload? ResumeEventPayload { get; }

    /// <summary>Gets whether the event represents a durable wait timeout.</summary>
    public bool IsTimeout { get; }

    /// <summary>Gets the activity callsite that produced a result, if any.</summary>
    public string? ActivityCallsiteId { get; }

    /// <summary>Gets the encoded activity result, if any.</summary>
    public DurableEncodedPayload? ActivityResult { get; }
}

/// <summary>
/// Describes the durable work command produced by one Flow activity transition.
/// </summary>
public sealed record DurableFlowActivityCommand
{
    /// <summary>
    /// Initializes an activity command.
    /// </summary>
    public DurableFlowActivityCommand(
        string callsiteId,
        int resultContractVersion,
        string workName,
        string workVersion,
        DurableProviderSafety providerSafety,
        DurableEncodedPayload work)
    {
        if (resultContractVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resultContractVersion));
        }

        if (!Enum.IsDefined(providerSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSafety));
        }

        CallsiteId = DurableIdentifier.Require(callsiteId, nameof(callsiteId), 200);
        ResultContractVersion = resultContractVersion;
        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        ProviderSafety = providerSafety;
        Work = work ?? throw new ArgumentNullException(nameof(work));
    }

    /// <summary>Gets the stable Flow callsite.</summary>
    public string CallsiteId { get; }

    /// <summary>Gets the expected Flow result contract version.</summary>
    public int ResultContractVersion { get; }

    /// <summary>Gets the registered durable work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the registered durable work version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the provider ambiguity policy.</summary>
    public DurableProviderSafety ProviderSafety { get; }

    /// <summary>Gets the encoded activity work.</summary>
    public DurableEncodedPayload Work { get; }
}

/// <summary>
/// Describes the exact payload contract accepted by one persisted external-event wait.
/// </summary>
public sealed record DurableFlowEventContract
{
    /// <summary>Initializes an event payload contract.</summary>
    /// <param name="payloadRequired">Whether the event must carry a payload.</param>
    /// <param name="contractName">Exact contract name when a payload is required.</param>
    /// <param name="contractVersion">Exact contract version when a payload is required.</param>
    /// <param name="classification">Exact approved classification when a payload is required.</param>
    /// <param name="retentionPolicyId">Exact retention policy identity when a payload is required.</param>
    public DurableFlowEventContract(
        bool payloadRequired,
        string? contractName = null,
        string? contractVersion = null,
        DurableDataClassification? classification = null,
        string? retentionPolicyId = null)
    {
        if (payloadRequired !=
            (contractName is not null && contractVersion is not null && classification is not null && retentionPolicyId is not null))
        {
            throw new ArgumentException(
                "A required event payload must declare both contract name and version; a no-payload event declares neither.");
        }

        if (classification is { } definedClassification && !Enum.IsDefined(definedClassification))
        {
            throw new ArgumentOutOfRangeException(nameof(classification));
        }

        PayloadRequired = payloadRequired;
        ContractName = contractName is null ? null : DurableIdentifier.Require(contractName, nameof(contractName), 200);
        ContractVersion = contractVersion is null
            ? null
            : DurableIdentifier.Require(contractVersion, nameof(contractVersion), 100);
        Classification = classification;
        RetentionPolicyId = retentionPolicyId is null
            ? null
            : DurableIdentifier.Require(retentionPolicyId, nameof(retentionPolicyId), 128);
    }

    /// <summary>Gets whether the external event must carry a payload.</summary>
    public bool PayloadRequired { get; }

    /// <summary>Gets the exact payload contract name, or null for a no-payload wait.</summary>
    public string? ContractName { get; }

    /// <summary>Gets the exact payload contract version, or null for a no-payload wait.</summary>
    public string? ContractVersion { get; }

    /// <summary>Gets the exact approved classification, or null for a no-payload wait.</summary>
    public DurableDataClassification? Classification { get; }

    /// <summary>Gets the exact retention policy identity, or null for a no-payload wait.</summary>
    public string? RetentionPolicyId { get; }
}

/// <summary>
/// Binds one typed Flow event callsite to the exact durable payload codec allowed to cross that wait boundary.
/// </summary>
public abstract class DurableFlowEventBinding
{
    /// <summary>Initializes common event binding metadata.</summary>
    /// <param name="callsite">Stable typed Flow event callsite.</param>
    /// <param name="payloadCodec">Exact durable payload codec for the callsite.</param>
    protected DurableFlowEventBinding(IFlowEventCallsite callsite, IDurablePayloadCodec payloadCodec)
    {
        Callsite = callsite ?? throw new ArgumentNullException(nameof(callsite));
        PayloadCodec = payloadCodec ?? throw new ArgumentNullException(nameof(payloadCodec));
        _ = DurableIdentifier.Require(callsite.EventName, nameof(callsite), 200);
        _ = DurableIdentifier.Require(callsite.ContractName, nameof(callsite), 200);
        _ = DurableIdentifier.Require(callsite.ContractVersion, nameof(callsite), 100);
    }

    /// <summary>Gets the exact callsite instance nodes must return when waiting for this event.</summary>
    public IFlowEventCallsite Callsite { get; }

    /// <summary>Gets the exact durable payload codec instance accepted by this event boundary.</summary>
    public IDurablePayloadCodec PayloadCodec { get; }
}

/// <summary>
/// Typed binding between a Flow event callsite and its durable payload codec.
/// </summary>
/// <typeparam name="TPayload">Allowlisted event payload type.</typeparam>
public sealed class DurableFlowEventBinding<TPayload> : DurableFlowEventBinding
{
    /// <summary>Initializes a typed event binding and verifies its contract identity.</summary>
    /// <param name="callsite">Stable typed Flow event callsite.</param>
    /// <param name="payloadCodec">Exact durable payload codec for the callsite.</param>
    public DurableFlowEventBinding(
        FlowEventCallsite<TPayload> callsite,
        IDurablePayloadCodec<TPayload> payloadCodec)
        : base(callsite, payloadCodec)
    {
        if (!string.Equals(callsite.ContractName, payloadCodec.ContractName, StringComparison.Ordinal) ||
            !string.Equals(callsite.ContractVersion, payloadCodec.ContractVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Flow event callsite contract must exactly match its durable payload codec.",
                nameof(payloadCodec));
        }
    }
}

/// <summary>
/// Carries the persistable result of exactly one Flow transition evaluation.
/// </summary>
public sealed record DurableFlowEvaluationResult
{
    /// <summary>
    /// Initializes a durable Flow evaluation result.
    /// </summary>
    public DurableFlowEvaluationResult(
        FlowTransitionKind kind,
        string nodeId,
        DurableEncodedPayload? context,
        string? nextNodeId,
        string? eventName,
        FlowTimeout? timeout,
        FlowFault? fault,
        DurableFlowActivityCommand? activity,
        DurableFlowEventContract? eventContract = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        NodeId = DurableIdentifier.Require(nodeId, nameof(nodeId), 200);
        Context = context;
        NextNodeId = nextNodeId;
        EventName = eventName;
        Timeout = timeout;
        Fault = fault;
        Activity = activity;
        EventContract = eventContract;
    }

    /// <summary>Gets the transition kind.</summary>
    public FlowTransitionKind Kind { get; }

    /// <summary>Gets the node that produced the transition.</summary>
    public string NodeId { get; }

    /// <summary>Gets the encoded resulting context for non-fault transitions.</summary>
    public DurableEncodedPayload? Context { get; }

    /// <summary>Gets the declared next node.</summary>
    public string? NextNodeId { get; }

    /// <summary>Gets the external wait or timeout event name.</summary>
    public string? EventName { get; }

    /// <summary>Gets the optional durable wait timeout.</summary>
    public FlowTimeout? Timeout { get; }

    /// <summary>Gets a process-level Flow fault.</summary>
    public FlowFault? Fault { get; }

    /// <summary>Gets an atomically accepted activity command.</summary>
    public DurableFlowActivityCommand? Activity { get; }

    /// <summary>Gets the exact external-event payload contract for a wait transition.</summary>
    public DurableFlowEventContract? EventContract { get; }
}

/// <summary>
/// Binds a Flow activity callsite to one registered durable work contract.
/// </summary>
/// <typeparam name="TContext">Flow context type.</typeparam>
public abstract class DurableFlowActivityBinding<TContext>
{
    /// <summary>
    /// Initializes common binding metadata.
    /// </summary>
    protected DurableFlowActivityBinding(string callsiteId, DurableWorkRegistration workRegistration)
    {
        CallsiteId = DurableIdentifier.Require(callsiteId, nameof(callsiteId), 200);
        WorkRegistration = workRegistration ?? throw new ArgumentNullException(nameof(workRegistration));
    }

    /// <summary>Gets the stable callsite identifier.</summary>
    public string CallsiteId { get; }

    /// <summary>Gets the registered durable work contract.</summary>
    public DurableWorkRegistration WorkRegistration { get; }

    /// <summary>Gets the immutable activity work contract version.</summary>
    public abstract int WorkContractVersion { get; }

    /// <summary>Gets the immutable activity result contract version.</summary>
    public abstract int ResultContractVersion { get; }

    /// <summary>Encodes work from one evaluated activity request.</summary>
    public abstract DurableEncodedPayload EncodeWork(IFlowActivityRequest<TContext> activity);

    /// <summary>Decodes a persisted result into the typed Flow resume contract.</summary>
    public abstract FlowActivityWorkResult DecodeResult(DurableEncodedPayload result);
}

/// <summary>
/// Typed binding between a Flow callsite and a durable work registration.
/// </summary>
public sealed class DurableFlowActivityBinding<TContext, TWork, TResult> : DurableFlowActivityBinding<TContext>
{
    private readonly FlowActivityCallsite<TWork, TResult> _callsite;
    private readonly IDurablePayloadCodec<TWork> _workCodec;
    private readonly IDurablePayloadCodec<TResult> _resultCodec;

    /// <summary>
    /// Initializes a typed activity binding.
    /// </summary>
    public DurableFlowActivityBinding(
        FlowActivityCallsite<TWork, TResult> callsite,
        DurableWorkRegistration workRegistration,
        IDurablePayloadCodec<TWork> workCodec,
        IDurablePayloadCodec<TResult> resultCodec)
        : base(callsite?.CallsiteId ?? throw new ArgumentNullException(nameof(callsite)), workRegistration)
    {
        _callsite = callsite;
        _workCodec = workCodec ?? throw new ArgumentNullException(nameof(workCodec));
        _resultCodec = resultCodec ?? throw new ArgumentNullException(nameof(resultCodec));
        if (workRegistration.WorkCodec.PayloadType != typeof(TWork)
            || workRegistration.ResultCodec.PayloadType != typeof(TResult))
        {
            throw new ArgumentException("The durable work registration types do not match the Flow activity callsite.", nameof(workRegistration));
        }

        if (!ReferenceEquals(workRegistration.WorkCodec, workCodec) ||
            !ReferenceEquals(workRegistration.ResultCodec, resultCodec))
        {
            throw new ArgumentException(
                "The Flow activity binding must use the exact work and result codec instances owned by its durable work registration.",
                nameof(workRegistration));
        }
    }

    /// <inheritdoc />
    public override int WorkContractVersion => _callsite.WorkContractVersion;

    /// <inheritdoc />
    public override int ResultContractVersion => _callsite.ResultContractVersion;

    /// <inheritdoc />
    public override DurableEncodedPayload EncodeWork(IFlowActivityRequest<TContext> activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!string.Equals(activity.CallsiteId, CallsiteId, StringComparison.Ordinal)
            || activity.WorkType != typeof(TWork)
            || activity.ResultType != typeof(TResult)
            || activity.WorkContractVersion != _callsite.WorkContractVersion
            || activity.ResultContractVersion != _callsite.ResultContractVersion
            || activity.Work is not TWork work)
        {
            throw new InvalidOperationException("Evaluated Flow activity does not match its registered durable binding.");
        }

        return _workCodec.Encode(work);
    }

    /// <inheritdoc />
    public override FlowActivityWorkResult DecodeResult(DurableEncodedPayload result) =>
        _callsite.CreateResult(_resultCodec.Decode(result));
}

/// <summary>
/// Evaluates one registered Flow version through versioned durable codecs.
/// </summary>
public abstract class DurableFlowRegistration
{
    /// <summary>Gets the stable authoring and transition interpretation model.</summary>
    public const string CurrentAuthoringModel = "appsurface.flow-transition/v1";

    /// <summary>Gets the stable Flow definition id.</summary>
    public abstract string FlowId { get; }

    /// <summary>Gets the immutable Flow definition version.</summary>
    public abstract string FlowVersion { get; }

    /// <summary>
    /// Gets the application-owned implementation manifest version for executable node semantics not represented by
    /// graph topology or contract metadata.
    /// </summary>
    public abstract string ImplementationVersion { get; }

    /// <summary>Gets the stable node id used for newly accepted instances.</summary>
    public abstract string StartNodeId { get; }

    /// <summary>Gets the deterministic lowercase SHA-256 definition-manifest fingerprint.</summary>
    public abstract string DefinitionFingerprint { get; }

    /// <summary>Gets the authoring model required to interpret persisted transition history.</summary>
    public virtual string AuthoringModel => CurrentAuthoringModel;

    /// <summary>Gets the Flow context codec.</summary>
    public abstract IDurablePayloadCodec ContextCodec { get; }

    /// <summary>Gets the exact typed external-event bindings declared by this Flow manifest.</summary>
    public abstract IReadOnlyList<DurableFlowEventBinding> EventBindings { get; }

    /// <summary>Gets the exact durable work registrations referenced by activity callsites.</summary>
    public abstract IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations { get; }

    /// <summary>
    /// Evaluates and encodes exactly one transition.
    /// </summary>
    public abstract ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
        DurableFlowEvaluationInput input,
        IDurablePayloadCodecRegistry payloadCodecs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed, reflection-free durable registration for one Flow definition version.
/// </summary>
public sealed class DurableFlowRegistration<TContext> : DurableFlowRegistration
{
    private readonly FlowDefinition<TContext> _definition;
    private readonly IDurablePayloadCodec<TContext> _contextCodec;
    private readonly IFlowTransitionEvaluator<TContext> _evaluator;
    private readonly IReadOnlyDictionary<string, DurableFlowActivityBinding<TContext>> _activityBindings;
    private readonly IReadOnlyDictionary<(string EventName, string ContractName, string ContractVersion), DurableFlowEventBinding>
        _eventBindings;
    private readonly string _definitionFingerprint;

    /// <summary>
    /// Initializes a typed durable Flow registration.
    /// </summary>
    /// <param name="definition">Immutable app-owned Flow graph.</param>
    /// <param name="contextCodec">Exact global codec used for persisted Flow context.</param>
    /// <param name="implementationVersion">Stable implementation identity used for compatibility diagnostics.</param>
    /// <param name="evaluator">Host-neutral transition evaluator.</param>
    /// <param name="activityBindings">Bindings from declared activity callsites to exact global Work registrations.</param>
    /// <param name="eventBindings">Bindings from declared event callsites to exact global payload codecs.</param>
    /// <remarks>
    /// Construct the global <see cref="DurableFlowRegistry"/> with its Work and payload registries before accepting Flow
    /// commands; that registry verifies every binding uses the same registered object identity.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when durable identifiers or binding declarations are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required definition, codec, evaluator, or binding is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when bindings are duplicate, undeclared, or incompatible.</exception>
    public DurableFlowRegistration(
        FlowDefinition<TContext> definition,
        IDurablePayloadCodec<TContext> contextCodec,
        string implementationVersion,
        IFlowTransitionEvaluator<TContext> evaluator,
        IEnumerable<DurableFlowActivityBinding<TContext>>? activityBindings = null,
        IEnumerable<DurableFlowEventBinding>? eventBindings = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _ = DurableIdentifier.Require(_definition.FlowId, "definition.FlowId", 200);
        _ = DurableIdentifier.Require(_definition.Version, "definition.Version", 100);
        _ = DurableIdentifier.Require(_definition.StartNodeId, "definition.StartNodeId", 200);
        foreach (var node in _definition.Nodes)
        {
            _ = DurableIdentifier.Require(node.Key, "definition.NodeId", 200);
            foreach (var target in node.Value.NextNodeIds)
            {
                _ = DurableIdentifier.Require(target, "definition.NextNodeId", 200);
            }
        }

        _contextCodec = contextCodec ?? throw new ArgumentNullException(nameof(contextCodec));
        ImplementationVersion = DurableIdentifier.Require(
            implementationVersion,
            nameof(implementationVersion),
            100);
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _ = DurableIdentifier.Require(_evaluator.EvaluatorId, "evaluator.EvaluatorId", 200);
        _ = DurableIdentifier.Require(_evaluator.EvaluatorVersion, "evaluator.EvaluatorVersion", 100);
        var bindings = new Dictionary<string, DurableFlowActivityBinding<TContext>>(StringComparer.Ordinal);
        foreach (var binding in activityBindings ?? [])
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!bindings.TryAdd(binding.CallsiteId, binding))
            {
                throw new InvalidOperationException($"Flow activity callsite '{binding.CallsiteId}' is bound more than once.");
            }
        }

        _activityBindings = bindings;
        var events = new Dictionary<(string EventName, string ContractName, string ContractVersion), DurableFlowEventBinding>();
        foreach (var binding in eventBindings ?? [])
        {
            ArgumentNullException.ThrowIfNull(binding);
            var identity = (
                binding.Callsite.EventName,
                binding.Callsite.ContractName,
                binding.Callsite.ContractVersion);
            if (!events.TryAdd(identity, binding))
            {
                throw new InvalidOperationException(
                    $"Flow event callsite '{binding.Callsite.EventName}' is bound more than once.");
            }

        }

        _eventBindings = events;
        _definitionFingerprint = ComputeDefinitionFingerprint(
            _definition,
            _contextCodec,
            ImplementationVersion,
            _evaluator,
            events.Values,
            bindings.Values);
    }

    /// <inheritdoc />
    public override string FlowId => _definition.FlowId;

    /// <inheritdoc />
    public override string FlowVersion => _definition.Version;

    /// <inheritdoc />
    public override string ImplementationVersion { get; }

    /// <inheritdoc />
    public override string StartNodeId => _definition.StartNodeId;

    /// <inheritdoc />
    public override string DefinitionFingerprint => _definitionFingerprint;

    /// <inheritdoc />
    public override IDurablePayloadCodec ContextCodec => _contextCodec;

    /// <inheritdoc />
    public override IReadOnlyList<DurableFlowEventBinding> EventBindings => _eventBindings.Values.ToArray();

    /// <inheritdoc />
    public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations =>
        _activityBindings.Values
            .Select(static binding => binding.WorkRegistration)
            .Distinct()
            .ToArray();

    /// <inheritdoc />
    public override async ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
        DurableFlowEvaluationInput input,
        IDurablePayloadCodecRegistry payloadCodecs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(payloadCodecs);
        var context = _contextCodec.Decode(input.Context);
        FlowResumeEvent? resumeEvent = null;
        if (input.ResumeEventName is not null)
        {
            var payload = input.ResumeEventPayload is null
                ? null
                : payloadCodecs.GetRequired(
                    input.ResumeEventPayload.ContractName,
                    input.ResumeEventPayload.ContractVersion).DecodeObject(input.ResumeEventPayload);
            resumeEvent = new FlowResumeEvent(input.ResumeEventName, payload, input.IsTimeout);
        }

        FlowActivityWorkResult? activityResult = null;
        if (input.ActivityCallsiteId is not null)
        {
            var binding = GetBinding(input.ActivityCallsiteId);
            activityResult = binding.DecodeResult(input.ActivityResult!);
        }

        var transition = await _evaluator.EvaluateAsync(
            _definition,
            new FlowTransitionInput<TContext>(input.NodeId, context, resumeEvent, activityResult),
            cancellationToken).ConfigureAwait(false);
        if (transition.Fault is { } fault)
        {
            if (fault.Code is "flow.next-node-invalid" or "flow.outcome-unsupported")
            {
                throw new FlowDefinitionException(
                    $"Durable Flow '{FlowId}' version '{FlowVersion}' produced invariant fault '{fault.Code}'.");
            }

            ValidateFaultCode(fault.Code);
        }

        var encodedContext = transition.Context is null ? null : _contextCodec.Encode(transition.Context);
        DurableFlowActivityCommand? activity = null;
        DurableFlowEventContract? eventContract = null;
        if (transition.Kind == FlowTransitionKind.Wait)
        {
            if (transition.EventCallsite is { } eventCallsite)
            {
                var binding = GetEventBinding(eventCallsite);
                var eventCodec = payloadCodecs.GetRequired(
                    eventCallsite.PayloadType,
                    eventCallsite.ContractName,
                    eventCallsite.ContractVersion);
                if (!ReferenceEquals(eventCodec, binding.PayloadCodec))
                {
                    throw new InvalidOperationException(
                        $"Flow '{FlowId}' version '{FlowVersion}' event '{eventCallsite.EventName}' does not use the exact globally registered payload codec.");
                }

                eventContract = new DurableFlowEventContract(
                    payloadRequired: true,
                    eventCallsite.ContractName,
                    eventCallsite.ContractVersion,
                    eventCodec.Classification,
                    eventCodec.RetentionPolicyId);
            }
            else
            {
                eventContract = new DurableFlowEventContract(payloadRequired: false);
            }
        }

        if (transition.Activity is not null)
        {
            var binding = GetBinding(transition.Activity.CallsiteId);
            activity = new DurableFlowActivityCommand(
                binding.CallsiteId,
                transition.Activity.ResultContractVersion,
                binding.WorkRegistration.WorkName,
                binding.WorkRegistration.WorkVersion,
                binding.WorkRegistration.ProviderSafety,
                binding.EncodeWork(transition.Activity));
        }

        return new DurableFlowEvaluationResult(
            transition.Kind,
            transition.NodeId,
            encodedContext,
            transition.NextNodeId,
            transition.EventName,
            transition.Timeout,
            transition.Fault,
            activity,
            eventContract);
    }

    private DurableFlowActivityBinding<TContext> GetBinding(string callsiteId) =>
        _activityBindings.TryGetValue(callsiteId, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Flow '{FlowId}' version '{FlowVersion}' activity callsite '{callsiteId}' is not durably registered.");

    private DurableFlowEventBinding GetEventBinding(IFlowEventCallsite callsite) =>
        _eventBindings.TryGetValue((callsite.EventName, callsite.ContractName, callsite.ContractVersion), out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Flow '{FlowId}' version '{FlowVersion}' event callsite '{callsite.EventName}' is not declared in its durable implementation manifest.");

    private static void ValidateFaultCode(string code)
    {
        try
        {
            _ = DurableIdentifier.Require(code, nameof(code), 120);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Durable Flow fault codes must contain 1 to 120 ASCII letters, digits, hyphens, underscores, periods, or colons.",
                exception);
        }
    }

    private static string ComputeDefinitionFingerprint(
        FlowDefinition<TContext> definition,
        IDurablePayloadCodec<TContext> contextCodec,
        string implementationVersion,
        IFlowTransitionEvaluator<TContext> evaluator,
        IEnumerable<DurableFlowEventBinding> eventBindings,
        IEnumerable<DurableFlowActivityBinding<TContext>> bindings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "durable-flow-definition-manifest-v2");
        Append(hash, CurrentAuthoringModel);
        Append(hash, definition.FlowId);
        Append(hash, definition.Version);
        Append(hash, implementationVersion);
        Append(hash, definition.StartNodeId);
        AppendCodec(hash, "context-codec", contextCodec);
        Append(hash, "evaluator");
        Append(hash, evaluator.EvaluatorId);
        Append(hash, evaluator.EvaluatorVersion);
        foreach (var node in definition.Nodes.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            Append(hash, "node");
            Append(hash, node.Key);
            foreach (var target in node.Value.NextNodeIds.OrderBy(static value => value, StringComparer.Ordinal))
            {
                Append(hash, "edge");
                Append(hash, node.Key);
                Append(hash, target);
            }
        }

        foreach (var binding in eventBindings
            .OrderBy(static item => item.Callsite.EventName, StringComparer.Ordinal)
            .ThenBy(static item => item.Callsite.ContractName, StringComparer.Ordinal)
            .ThenBy(static item => item.Callsite.ContractVersion, StringComparer.Ordinal))
        {
            Append(hash, "external-event");
            Append(hash, binding.Callsite.EventName);
            Append(hash, binding.Callsite.ContractName);
            Append(hash, binding.Callsite.ContractVersion);
            AppendCodec(hash, "event-codec", binding.PayloadCodec);
        }

        foreach (var binding in bindings.OrderBy(static item => item.CallsiteId, StringComparer.Ordinal))
        {
            Append(hash, "activity");
            Append(hash, binding.CallsiteId);
            Append(hash, binding.WorkContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, binding.ResultContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, binding.WorkRegistration.WorkName);
            Append(hash, binding.WorkRegistration.WorkVersion);
            Append(
                hash,
                ((int)binding.WorkRegistration.ProviderSafety).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendCodec(hash, "activity-work-codec", binding.WorkRegistration.WorkCodec);
            AppendCodec(hash, "activity-result-codec", binding.WorkRegistration.ResultCodec);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendCodec(IncrementalHash hash, string role, IDurablePayloadCodec codec)
    {
        Append(hash, role);
        Append(hash, codec.ContractName);
        Append(hash, codec.ContractVersion);
        Append(
            hash,
            ((int)codec.Classification).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, codec.RetentionPolicyId);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// Resolves immutable durable Flow registrations by id and version.
/// </summary>
public interface IDurableFlowRegistry
{
    /// <summary>Gets a required Flow registration.</summary>
    DurableFlowRegistration GetRequired(string flowId, string flowVersion);
}

/// <summary>
/// Immutable durable Flow registry built at host startup.
/// </summary>
public sealed class DurableFlowRegistry : IDurableFlowRegistry
{
    private readonly IReadOnlyDictionary<(string Id, string Version), DurableFlowRegistration> _registrations;

    /// <summary>
    /// Initializes a registry and rejects duplicate Flow versions.
    /// </summary>
    private DurableFlowRegistry(IEnumerable<DurableFlowRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var map = new Dictionary<(string Id, string Version), DurableFlowRegistration>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!map.TryAdd((registration.FlowId, registration.FlowVersion), registration))
            {
                throw new InvalidOperationException(
                    $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' is durably registered more than once.");
            }
        }

        _registrations = map;
    }

    /// <summary>
    /// Initializes a registry and verifies every activity binding points at the exact globally registered work
    /// contract before a host can accept or execute Flow work.
    /// </summary>
    /// <param name="registrations">Registered Flow definitions.</param>
    /// <param name="workRegistry">Authoritative global durable work registry.</param>
    private DurableFlowRegistry(
        IEnumerable<DurableFlowRegistration> registrations,
        IDurableWorkRegistry workRegistry)
        : this(registrations)
    {
        ArgumentNullException.ThrowIfNull(workRegistry);
        foreach (var registration in _registrations.Values)
        {
            foreach (var activityWork in registration.ActivityWorkRegistrations)
            {
                var global = workRegistry.GetRequired(activityWork.WorkName, activityWork.WorkVersion);
                if (!ReferenceEquals(global, activityWork))
                {
                    throw new InvalidOperationException(
                        $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' activity work '{activityWork.WorkName}' version '{activityWork.WorkVersion}' is not the exact global durable work registration.");
                }
            }
        }
    }

    /// <summary>
    /// Initializes a registry and also verifies every context and typed event boundary uses the exact globally
    /// registered payload codec instance.
    /// </summary>
    /// <param name="registrations">Registered Flow definitions.</param>
    /// <param name="workRegistry">Authoritative global durable work registry.</param>
    /// <param name="payloadCodecs">Authoritative global payload codec registry.</param>
    public DurableFlowRegistry(
        IEnumerable<DurableFlowRegistration> registrations,
        IDurableWorkRegistry workRegistry,
        IDurablePayloadCodecRegistry payloadCodecs)
        : this(registrations, workRegistry)
    {
        ArgumentNullException.ThrowIfNull(payloadCodecs);
        foreach (var registration in _registrations.Values)
        {
            var contextCodec = payloadCodecs.GetRequired(
                registration.ContextCodec.PayloadType,
                registration.ContextCodec.ContractName,
                registration.ContextCodec.ContractVersion);
            if (!ReferenceEquals(contextCodec, registration.ContextCodec))
            {
                throw new InvalidOperationException(
                    $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' does not use the exact globally registered context codec.");
            }

            foreach (var eventBinding in registration.EventBindings)
            {
                var eventCodec = payloadCodecs.GetRequired(
                    eventBinding.PayloadCodec.PayloadType,
                    eventBinding.PayloadCodec.ContractName,
                    eventBinding.PayloadCodec.ContractVersion);
                if (!ReferenceEquals(eventCodec, eventBinding.PayloadCodec))
                {
                    throw new InvalidOperationException(
                        $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' event '{eventBinding.Callsite.EventName}' does not use the exact globally registered payload codec.");
                }
            }
        }
    }

    /// <inheritdoc />
    public DurableFlowRegistration GetRequired(string flowId, string flowVersion)
    {
        var id = DurableIdentifier.Require(flowId, nameof(flowId), 200);
        var version = DurableIdentifier.Require(flowVersion, nameof(flowVersion), 100);
        return _registrations.TryGetValue((id, version), out var registration)
            ? registration
            : throw new InvalidOperationException($"Flow '{id}' version '{version}' is not durably registered.");
    }
}
