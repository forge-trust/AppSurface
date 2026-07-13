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
/// Targets use typed input values so the runtime can require the registered durable codec. Persisted schedules never
/// serialize executable delegates or resolve a target from a CLR type name.
/// </remarks>
public abstract record DurableScheduleTarget
{
    private protected DurableScheduleTarget(DurableScheduleTargetKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets the target subsystem.</summary>
    public DurableScheduleTargetKind Kind { get; }

    internal abstract string RegisteredName { get; }

    internal abstract string RegisteredVersion { get; }

    internal abstract Type InputType { get; }

    internal abstract object InputValue { get; }

    /// <summary>
    /// Creates a target for a registered durable work type.
    /// </summary>
    /// <typeparam name="TWork">Registered work input type.</typeparam>
    /// <param name="workName">Stable registry name; this is not a CLR assembly-qualified name.</param>
    /// <param name="workVersion">Immutable registered work contract version.</param>
    /// <param name="input">Typed input encoded with the work type's registered codec.</param>
    /// <returns>A typed durable work target.</returns>
    public static DurableWorkScheduleTarget<TWork> Work<TWork>(string workName, string workVersion, TWork input)
        where TWork : notnull => new(workName, workVersion, input);

    /// <summary>
    /// Creates a target for an immutable registered Flow version.
    /// </summary>
    /// <typeparam name="TContext">Registered Flow context type.</typeparam>
    /// <param name="flowId">Stable Flow identifier.</param>
    /// <param name="version">Immutable Flow graph version.</param>
    /// <param name="initialContext">Typed initial context encoded with the Flow's registered codec.</param>
    /// <returns>A typed durable Flow target.</returns>
    public static DurableFlowScheduleTarget<TContext> Flow<TContext>(
        string flowId,
        string version,
        TContext initialContext)
        where TContext : notnull => new(flowId, version, initialContext);

    private protected static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value;
    }
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
    /// <param name="workName">Stable work registry name.</param>
    /// <param name="workVersion">Immutable registered work contract version.</param>
    /// <param name="input">Typed work input.</param>
    public DurableWorkScheduleTarget(string workName, string workVersion, TWork input)
        : base(DurableScheduleTargetKind.Work)
    {
        WorkName = RequireText(workName, nameof(workName));
        WorkVersion = RequireText(workVersion, nameof(workVersion));
        Input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>Gets the stable registered work name.</summary>
    public string WorkName { get; }

    /// <summary>Gets the immutable registered work contract version.</summary>
    public string WorkVersion { get; }

    /// <summary>Gets the typed input encoded by the registered work codec.</summary>
    public TWork Input { get; }

    internal override string RegisteredName => WorkName;

    internal override string RegisteredVersion => WorkVersion;

    internal override Type InputType => typeof(TWork);

    internal override object InputValue => Input;
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
    /// <param name="flowId">Stable Flow identifier.</param>
    /// <param name="version">Immutable Flow graph version.</param>
    /// <param name="initialContext">Typed initial context.</param>
    public DurableFlowScheduleTarget(string flowId, string version, TContext initialContext)
        : base(DurableScheduleTargetKind.Flow)
    {
        FlowId = RequireText(flowId, nameof(flowId));
        Version = RequireText(version, nameof(version));
        InitialContext = initialContext ?? throw new ArgumentNullException(nameof(initialContext));
    }

    /// <summary>Gets the stable Flow identifier.</summary>
    public string FlowId { get; }

    /// <summary>Gets the immutable Flow graph version.</summary>
    public string Version { get; }

    /// <summary>Gets the typed initial context encoded by the registered Flow codec.</summary>
    public TContext InitialContext { get; }

    internal override string RegisteredName => FlowId;

    internal override string RegisteredVersion => Version;

    internal override Type InputType => typeof(TContext);

    internal override object InputValue => InitialContext;
}
