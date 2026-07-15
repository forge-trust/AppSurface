namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies the persisted shape of a durable schedule.
/// </summary>
public enum DurableScheduleKind
{
    /// <summary>A one-time absolute UTC instant.</summary>
    At = 0,

    /// <summary>A one-time delay anchored to durable acceptance.</summary>
    After = 1,

    /// <summary>A recurring elapsed-UTC interval.</summary>
    Every = 2,

    /// <summary>A calendar recurrence evaluated with a versioned cron dialect and an IANA time zone.</summary>
    Cron = 3,
}

/// <summary>
/// Identifies the versioned public semantics used to evaluate a cron expression.
/// </summary>
/// <remarks>
/// A behavior-changing Cronos upgrade must introduce a new dialect or an explicit persisted-schedule migration. It must
/// never silently reinterpret a schedule stored as <see cref="CronosV1"/>.
/// </remarks>
public enum CronDialect
{
    /// <summary>
    /// Cronos 0.13 five- or six-field syntax, including its day-of-month/day-of-week and daylight-saving semantics.
    /// </summary>
    CronosV1 = 1,
}

/// <summary>
/// Identifies whether a CronosV1 expression contains five fields or includes a leading seconds field.
/// </summary>
public enum CronGrammar
{
    /// <summary>Five fields: minute, hour, day of month, month, and day of week.</summary>
    Standard = 0,

    /// <summary>Six fields with seconds as the first field.</summary>
    IncludeSeconds = 1,
}

/// <summary>
/// Describes when a durable target should run and how overlap and downtime are handled.
/// </summary>
/// <remarks>
/// Definitions are immutable values. The default composition is <see cref="ScheduleOverlapPolicy.QueueOne"/> plus
/// <see cref="ScheduleMisfirePolicy.RunOnce"/>. Use <see cref="WithOverlap"/> or <see cref="WithMisfire"/> to opt a
/// particular schedule into different behavior.
/// </remarks>
public abstract record DurableSchedule
{
    private protected DurableSchedule(DurableScheduleKind kind)
    {
        Kind = kind;
        OverlapPolicy = ScheduleOverlapPolicy.QueueOne;
        MisfirePolicy = ScheduleMisfirePolicy.RunOnce;
    }

    /// <summary>
    /// Gets the persisted schedule shape.
    /// </summary>
    public DurableScheduleKind Kind { get; }

    /// <summary>
    /// Gets the effective overlap policy. The default is <see cref="ScheduleOverlapPolicy.QueueOne"/>.
    /// </summary>
    public ScheduleOverlapPolicy OverlapPolicy { get; private init; }

    /// <summary>
    /// Gets the effective downtime policy. The default is <see cref="ScheduleMisfirePolicy.RunOnce"/>.
    /// </summary>
    public ScheduleMisfirePolicy MisfirePolicy { get; private init; }

    /// <summary>
    /// Creates a schedule that runs once at an absolute instant.
    /// </summary>
    /// <param name="at">The instant to run. It is normalized to UTC.</param>
    /// <returns>An absolute one-time schedule.</returns>
    public static DurableAtSchedule At(DateTimeOffset at) => new(at);

    /// <summary>
    /// Creates a schedule that runs once after durable acceptance.
    /// </summary>
    /// <param name="delay">Positive elapsed delay from the authoritative store acceptance timestamp.</param>
    /// <returns>A delayed one-time schedule.</returns>
    public static DurableAfterSchedule After(TimeSpan delay) => new(delay);

    /// <summary>
    /// Creates an elapsed-UTC recurring schedule.
    /// </summary>
    /// <param name="interval">Positive elapsed interval.</param>
    /// <param name="anchor">
    /// Optional absolute anchor. When omitted, the runtime uses the durable acceptance transaction timestamp.
    /// </param>
    /// <returns>An elapsed-UTC interval schedule.</returns>
    /// <remarks>Use <see cref="Cron"/> for calendar-time recurrence or daylight-saving-aware wall-clock behavior.</remarks>
    public static DurableEverySchedule Every(TimeSpan interval, DateTimeOffset? anchor = null) => new(interval, anchor);

    /// <summary>
    /// Creates a CronosV1 calendar schedule.
    /// </summary>
    /// <param name="expression">Raw Cronos expression. It is persisted without semantic rewriting.</param>
    /// <param name="ianaTimeZoneId">IANA time-zone identifier, for example <c>America/New_York</c>.</param>
    /// <param name="grammar">Five-field or seconds-inclusive grammar.</param>
    /// <returns>A CronosV1 schedule.</returns>
    /// <remarks>
    /// The selected provider validates the expression and time zone before accepting the schedule. Cronos <c>H</c>
    /// fields are expanded from a stable cryptographic hash of the schedule id.
    /// </remarks>
    public static DurableCronSchedule Cron(
        string expression,
        string ianaTimeZoneId,
        CronGrammar grammar = CronGrammar.Standard) =>
        new(expression, ianaTimeZoneId, grammar, CronDialect.CronosV1);

    /// <summary>
    /// Returns a copy with the supplied overlap policy.
    /// </summary>
    /// <param name="policy">Per-schedule overlap behavior.</param>
    /// <returns>A schedule with the new policy.</returns>
    public DurableSchedule WithOverlap(ScheduleOverlapPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return this with { OverlapPolicy = policy };
    }

    /// <summary>
    /// Returns a copy with the supplied downtime recovery policy.
    /// </summary>
    /// <param name="policy">Per-schedule misfire behavior.</param>
    /// <returns>A schedule with the new policy.</returns>
    public DurableSchedule WithMisfire(ScheduleMisfirePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return this with { MisfirePolicy = policy };
    }
}

/// <summary>
/// A one-time schedule at an absolute UTC instant.
/// </summary>
public sealed record DurableAtSchedule : DurableSchedule
{
    /// <summary>
    /// Initializes a new absolute schedule.
    /// </summary>
    /// <param name="at">The instant to run.</param>
    public DurableAtSchedule(DateTimeOffset at)
        : base(DurableScheduleKind.At)
    {
        AtUtc = at.ToUniversalTime();
    }

    /// <summary>Gets the absolute run instant, normalized to UTC.</summary>
    public DateTimeOffset AtUtc { get; }
}

/// <summary>
/// A one-time delay anchored to the authoritative store timestamp of the durable acceptance transaction.
/// </summary>
public sealed record DurableAfterSchedule : DurableSchedule
{
    /// <summary>
    /// Initializes a new delayed schedule.
    /// </summary>
    /// <param name="delay">Positive delay from durable acceptance.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="delay"/> is not positive.</exception>
    public DurableAfterSchedule(TimeSpan delay)
        : base(DurableScheduleKind.After)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be positive.");
        }

        Delay = delay;
    }

    /// <summary>Gets the elapsed delay from durable acceptance.</summary>
    public TimeSpan Delay { get; }
}

/// <summary>
/// An elapsed-UTC recurring schedule.
/// </summary>
public sealed record DurableEverySchedule : DurableSchedule
{
    /// <summary>
    /// Initializes a new interval schedule.
    /// </summary>
    /// <param name="interval">Positive elapsed interval.</param>
    /// <param name="anchor">
    /// Optional absolute anchor. When omitted, the provider uses the durable acceptance transaction timestamp.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="interval"/> is not positive.</exception>
    public DurableEverySchedule(TimeSpan interval, DateTimeOffset? anchor = null)
        : base(DurableScheduleKind.Every)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        }

        Interval = interval;
        AnchorUtc = anchor?.ToUniversalTime();
    }

    /// <summary>Gets the elapsed UTC interval.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Gets the explicit UTC anchor, or <see langword="null"/> to anchor at durable acceptance.</summary>
    public DateTimeOffset? AnchorUtc { get; }
}

/// <summary>
/// A calendar schedule evaluated by a versioned Cronos dialect in an IANA time zone.
/// </summary>
public sealed record DurableCronSchedule : DurableSchedule
{
    /// <summary>
    /// Initializes a new cron schedule.
    /// </summary>
    /// <param name="expression">Raw cron expression.</param>
    /// <param name="ianaTimeZoneId">IANA time-zone identifier.</param>
    /// <param name="grammar">Persisted grammar mode.</param>
    /// <param name="dialect">Persisted public dialect.</param>
    /// <exception cref="ArgumentException">Thrown when a required string is empty or an enum value is unknown.</exception>
    public DurableCronSchedule(
        string expression,
        string ianaTimeZoneId,
        CronGrammar grammar = CronGrammar.Standard,
        CronDialect dialect = CronDialect.CronosV1)
        : base(DurableScheduleKind.Cron)
    {
        Expression = RequireText(expression, nameof(expression), 512);
        IanaTimeZoneId = RequireText(ianaTimeZoneId, nameof(ianaTimeZoneId), 128);

        if (!Enum.IsDefined(grammar))
        {
            throw new ArgumentException("Unknown cron grammar.", nameof(grammar));
        }

        if (!Enum.IsDefined(dialect))
        {
            throw new ArgumentException("Unknown cron dialect.", nameof(dialect));
        }

        Grammar = grammar;
        Dialect = dialect;
    }

    /// <summary>Gets the raw expression exactly as supplied.</summary>
    public string Expression { get; }

    /// <summary>Gets the IANA time-zone identifier.</summary>
    public string IanaTimeZoneId { get; }

    /// <summary>Gets the versioned public cron semantics.</summary>
    public CronDialect Dialect { get; }

    /// <summary>Gets the persisted five- or six-field grammar mode.</summary>
    public CronGrammar Grammar { get; }

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Value must not exceed {maximumLength} characters.", parameterName);
        }

        return value;
    }
}
