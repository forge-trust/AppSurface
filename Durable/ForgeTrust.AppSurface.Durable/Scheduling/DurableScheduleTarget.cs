namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies the durable subsystem started by a schedule occurrence.
/// </summary>
public enum DurableScheduleTargetKind
{
    /// <summary>A registered durable work executor.</summary>
    Work = 0,

    /// <summary>A registered immutable Flow definition version.</summary>
    Flow = 1,
}

/// <summary>
/// Describes the registered durable target started by each schedule occurrence.
/// </summary>
/// <remarks>
/// Targets encode typed input values immediately through an explicitly supplied durable codec. Persisted schedules never
/// serialize executable delegates or resolve a target from a CLR type name.
/// Registered names and versions accept only ASCII letters, digits, hyphens, underscores, periods, and colons. Empty,
/// whitespace-only, control-containing, and other-character values are rejected.
/// </remarks>
public abstract record DurableScheduleTarget
{
    private protected DurableScheduleTarget(DurableScheduleTargetKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets the target subsystem.</summary>
    public DurableScheduleTargetKind Kind { get; }

    /// <summary>Gets the provider-readable registered Work name or Flow id.</summary>
    public abstract string RegisteredName { get; }

    /// <summary>Gets the provider-readable immutable Work or Flow version.</summary>
    public abstract string RegisteredVersion { get; }

    /// <summary>Gets the immutable encoded input a provider persists without inspecting the CLR generic type.</summary>
    public abstract DurableEncodedPayload EncodedInput { get; }

    /// <summary>
    /// Creates a target for a registered durable work type.
    /// </summary>
    /// <typeparam name="TWork">Registered work input type.</typeparam>
    /// <param name="workName">Stable registry name of at most 200 durable-identifier characters; this is not a CLR assembly-qualified name.</param>
    /// <param name="workVersion">Immutable registered work contract version of at most 100 durable-identifier characters.</param>
    /// <param name="input">Typed input encoded immediately.</param>
    /// <param name="codec">The exact registered durable codec for the input.</param>
    /// <returns>A typed durable work target.</returns>
    public static DurableWorkScheduleTarget<TWork> Work<TWork>(
        string workName,
        string workVersion,
        TWork input,
        IDurablePayloadCodec<TWork> codec)
        where TWork : notnull => new(workName, workVersion, input, codec);

    /// <summary>
    /// Creates a target for an immutable registered Flow version.
    /// </summary>
    /// <typeparam name="TContext">Registered Flow context type.</typeparam>
    /// <param name="flowId">Stable Flow identifier of at most 200 durable-identifier characters.</param>
    /// <param name="version">Immutable Flow graph version of at most 100 durable-identifier characters.</param>
    /// <param name="initialContext">Typed initial context encoded immediately.</param>
    /// <param name="codec">The exact registered durable codec for the context.</param>
    /// <returns>A typed durable Flow target.</returns>
    public static DurableFlowScheduleTarget<TContext> Flow<TContext>(
        string flowId,
        string version,
        TContext initialContext,
        IDurablePayloadCodec<TContext> codec)
        where TContext : notnull => new(flowId, version, initialContext, codec);
}

/// <summary>
/// A schedule target that enqueues a registered durable work input.
/// </summary>
/// <typeparam name="TWork">Registered work input type.</typeparam>
public sealed record DurableWorkScheduleTarget<TWork> : DurableScheduleTarget
    where TWork : notnull
{
    /// <summary>
    /// Initializes a typed durable work target.
    /// </summary>
    /// <param name="workName">Stable work registry name of at most 200 durable-identifier characters.</param>
    /// <param name="workVersion">Immutable registered work contract version of at most 100 durable-identifier characters.</param>
    /// <param name="input">Typed work input.</param>
    /// <param name="codec">The exact registered codec.</param>
    public DurableWorkScheduleTarget(
        string workName,
        string workVersion,
        TWork input,
        IDurablePayloadCodec<TWork> codec)
        : base(DurableScheduleTargetKind.Work)
    {
        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
        ArgumentNullException.ThrowIfNull(input);
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        EncodedInputPayload = Codec.Encode(input);
    }

    /// <summary>Gets the stable registered work name, limited to 200 durable-identifier characters.</summary>
    public string WorkName { get; }

    /// <summary>Gets the immutable registered work contract version, limited to 100 durable-identifier characters.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the exact codec used to encode the immutable target input.</summary>
    public IDurablePayloadCodec<TWork> Codec { get; }

    /// <summary>Gets the immutable encoded target input.</summary>
    public DurableEncodedPayload EncodedInputPayload { get; }

    /// <inheritdoc />
    public override string RegisteredName => WorkName;

    /// <inheritdoc />
    public override string RegisteredVersion => WorkVersion;

    /// <inheritdoc />
    public override DurableEncodedPayload EncodedInput => EncodedInputPayload;
}

/// <summary>
/// A schedule target that starts an immutable registered Flow version.
/// </summary>
/// <typeparam name="TContext">Registered Flow context type.</typeparam>
public sealed record DurableFlowScheduleTarget<TContext> : DurableScheduleTarget
    where TContext : notnull
{
    /// <summary>
    /// Initializes a typed durable Flow target.
    /// </summary>
    /// <param name="flowId">Stable Flow identifier of at most 200 durable-identifier characters.</param>
    /// <param name="version">Immutable Flow graph version of at most 100 durable-identifier characters.</param>
    /// <param name="initialContext">Typed initial context.</param>
    /// <param name="codec">The exact registered codec.</param>
    public DurableFlowScheduleTarget(
        string flowId,
        string version,
        TContext initialContext,
        IDurablePayloadCodec<TContext> codec)
        : base(DurableScheduleTargetKind.Flow)
    {
        FlowId = DurableIdentifier.Require(flowId, nameof(flowId), 200);
        Version = DurableIdentifier.Require(version, nameof(version), 100);
        ArgumentNullException.ThrowIfNull(initialContext);
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        EncodedInitialContext = Codec.Encode(initialContext);
    }

    /// <summary>Gets the stable Flow identifier, limited to 200 durable-identifier characters.</summary>
    public string FlowId { get; }

    /// <summary>Gets the immutable Flow graph version, limited to 100 durable-identifier characters.</summary>
    public string Version { get; }

    /// <summary>Gets the exact codec used to encode the immutable initial context.</summary>
    public IDurablePayloadCodec<TContext> Codec { get; }

    /// <summary>Gets the immutable encoded initial context.</summary>
    public DurableEncodedPayload EncodedInitialContext { get; }

    /// <inheritdoc />
    public override string RegisteredName => FlowId;

    /// <inheritdoc />
    public override string RegisteredVersion => Version;

    /// <inheritdoc />
    public override DurableEncodedPayload EncodedInput => EncodedInitialContext;
}
