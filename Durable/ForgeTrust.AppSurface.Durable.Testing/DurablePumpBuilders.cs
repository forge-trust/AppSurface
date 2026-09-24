using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Builds validated bounded pump requests with production defaults.</summary>
public sealed class DurableRuntimePumpRequestBuilder
{
    private int _maximumItems = 32;
    private TimeSpan? _timeBudget;
    private DurableRuntimeSurface _surfaces = DurableRuntimeSurface.All;
    /// <summary>Sets the total item bound.</summary>
    public DurableRuntimePumpRequestBuilder WithMaximumItems(int value) { _maximumItems = value; return this; }
    /// <summary>Sets the pass time budget.</summary>
    public DurableRuntimePumpRequestBuilder WithTimeBudget(TimeSpan value) { _timeBudget = value; return this; }
    /// <summary>Sets selected durable surfaces.</summary>
    public DurableRuntimePumpRequestBuilder WithSurfaces(DurableRuntimeSurface value) { _surfaces = value; return this; }
    /// <summary>Creates the production request, preserving its validation exceptions.</summary>
    public DurableRuntimePumpRequest Build() => new(_maximumItems, _timeBudget, _surfaces);
}

/// <summary>Builds validated bounded pump results with zero-count defaults.</summary>
public sealed class DurableRuntimePumpResultBuilder
{
    private int _discovered, _claimed, _processed, _deferred, _failed;
    private bool _hasMore;
    private DateTimeOffset? _nextDueAtUtc;
    private TimeSpan _elapsed;
    /// <summary>Sets all pass counters.</summary>
    public DurableRuntimePumpResultBuilder WithCounts(int discovered, int claimed, int processed, int deferred, int failed)
    { _discovered = discovered; _claimed = claimed; _processed = processed; _deferred = deferred; _failed = failed; return this; }
    /// <summary>Sets whether more immediately eligible work may remain.</summary>
    public DurableRuntimePumpResultBuilder WithHasMore(bool value) { _hasMore = value; return this; }
    /// <summary>Sets the earliest known future due time.</summary>
    public DurableRuntimePumpResultBuilder WithNextDueAtUtc(DateTimeOffset? value) { _nextDueAtUtc = value; return this; }
    /// <summary>Sets elapsed pass duration.</summary>
    public DurableRuntimePumpResultBuilder WithElapsed(TimeSpan value) { _elapsed = value; return this; }
    /// <summary>Creates the production result, preserving its validation exceptions.</summary>
    public DurableRuntimePumpResult Build() => new(_discovered, _claimed, _processed, _deferred, _failed, _hasMore, _nextDueAtUtc, _elapsed);
}

/// <summary>Builds a validated pump attempt, defaulting to a completed empty pass.</summary>
public sealed class DurableRuntimePumpAttemptBuilder
{
    private DurableRuntimePumpAttemptKind _kind = DurableRuntimePumpAttemptKind.Completed;
    private DurableRuntimePumpResult? _result = new DurableRuntimePumpResultBuilder().Build();
    private string? _problemCode;
    /// <summary>Selects the attempt kind and its valid default result shape.</summary>
    public DurableRuntimePumpAttemptBuilder WithKind(DurableRuntimePumpAttemptKind value)
    {
        _kind = value;
        _result = value == DurableRuntimePumpAttemptKind.Completed
            ? new DurableRuntimePumpResultBuilder().Build()
            : null;
        return this;
    }
    /// <summary>Sets the completed pass result.</summary>
    public DurableRuntimePumpAttemptBuilder WithResult(DurableRuntimePumpResult? value) { _result = value; return this; }
    /// <summary>Sets the provider problem code.</summary>
    public DurableRuntimePumpAttemptBuilder WithProblemCode(string? value) { _problemCode = value; return this; }
    /// <summary>Creates the production attempt, preserving its validation exceptions.</summary>
    public DurableRuntimePumpAttempt Build() => new(_kind, _result, _problemCode);
}
