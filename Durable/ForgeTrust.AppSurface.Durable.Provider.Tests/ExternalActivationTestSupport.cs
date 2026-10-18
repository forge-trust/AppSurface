using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

/// <summary>Builds provider-free observations and controlled dependencies for external-activation tests.</summary>
internal static class ExternalActivationTestSupport
{
    /// <summary>Creates a complete immutable health value while allowing each normative evidence field to vary.</summary>
    internal static DurableRuntimeHealthSnapshot Health(
        DurableRuntimeHealthState state = DurableRuntimeHealthState.Healthy,
        string? problemCode = null,
        bool? schemaCompatible = null,
        bool? epochCompatible = null,
        bool? isDraining = null)
    {
        var defaultCompatibility = state is not DurableRuntimeHealthState.Unavailable
            and not DurableRuntimeHealthState.Incompatible;
        var configuredEpoch = new Guid("e552fc79-cdae-48a3-9542-61f2860ac24c");
        return new DurableRuntimeHealthSnapshot(
            state,
            problemCode,
            schemaCompatible ?? defaultCompatibility,
            epochCompatible ?? defaultCompatibility,
            installedSchemaVersion: 1,
            requiredSchemaVersion: 1,
            configuredRuntimeEpoch: configuredEpoch,
            activeRuntimeEpoch: defaultCompatibility ? configuredEpoch : null,
            workerId: "activation-test-worker",
            workerInstanceId: Guid.Parse("8a7019ec-3231-4512-a9c0-9b7e5aab1643"),
            hostedSurfaces: DurableRuntimeSurface.Work,
            observedAtUtc: DateTimeOffset.UnixEpoch,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: isDraining ?? state == DurableRuntimeHealthState.Draining,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);
    }

    /// <summary>Creates a nonempty aggregate whose every public fact can be distinguished by assertions.</summary>
    internal static DurableRuntimePumpResult PumpResult(
        int discovered = 4,
        int claimed = 3,
        int processed = 2,
        int deferred = 1,
        int failed = 1,
        bool hasMore = true,
        DateTimeOffset? nextDueAtUtc = null,
        TimeSpan? elapsed = null) => new(
            discovered,
            claimed,
            processed,
            deferred,
            failed,
            hasMore,
            nextDueAtUtc ?? new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.FromHours(-4)),
            elapsed ?? TimeSpan.FromTicks(123));
}

/// <summary>Provides a test-owned health delegate and records every observation boundary.</summary>
internal sealed class ExternalActivationHealth(
    Func<CancellationToken, ValueTask<DurableRuntimeHealthSnapshot>> read) : IDurableRuntimeHealth
{
    private int _callCount;

    /// <summary>Gets the number of health observations requested by the service.</summary>
    internal int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Gets every token passed to the health implementation.</summary>
    internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();

    /// <inheritdoc />
    public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        Tokens.Enqueue(cancellationToken);
        return read(cancellationToken);
    }
}

/// <summary>Provides a controlled admission delegate and records authoritative pass requests.</summary>
internal sealed class ExternalActivationAdmission(
    Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<DurableRuntimePumpAttempt>> run)
    : IDurableRuntimePumpAdmission
{
    private int _callCount;
    private readonly ConcurrentQueue<DurableRuntimePumpRequest> _requests = new();
    private readonly ConcurrentQueue<CancellationToken> _tokens = new();

    /// <summary>Gets the number of authoritative admission invocations.</summary>
    internal int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Gets the exact request references passed to admission.</summary>
    internal IReadOnlyList<DurableRuntimePumpRequest> Requests => _requests.ToArray();

    /// <summary>Gets each caller/budget linked token passed to admission.</summary>
    internal IReadOnlyList<CancellationToken> Tokens => _tokens.ToArray();

    /// <inheritdoc />
    public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        _requests.Enqueue(request);
        _tokens.Enqueue(cancellationToken);
        return run(request, cancellationToken);
    }
}

/// <summary>Tracks legacy pump calls so the activation service can prove it uses admission exclusively.</summary>
internal sealed class ExternalActivationLegacyPump : IDurableRuntimePump
{
    private int _callCount;

    /// <summary>Gets the number of calls through the legacy projection.</summary>
    internal int CallCount => Volatile.Read(ref _callCount);

    /// <inheritdoc />
    public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        return ValueTask.FromResult(ExternalActivationTestSupport.PumpResult());
    }
}

/// <summary>Captures safe service log calls and can inject a final-observation exception.</summary>
internal sealed class ExternalActivationLogger : ILogger<DurableExternalActivationService>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    /// <summary>Gets a snapshot of formatted log entries.</summary>
    internal IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

    /// <summary>Gets or sets an exception thrown synchronously by the next logging call.</summary>
    internal Exception? ThrowOnLog { get; set; }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var throwOnLog = ThrowOnLog;
        if (throwOnLog is not null)
        {
            throw throwOnLog;
        }

        _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Extends fake monotonic time with observable or deliberately delayed timer delivery.</summary>
internal sealed class ExternalActivationClock(DateTimeOffset? start = null) : FakeTimeProvider(start ?? DateTimeOffset.UnixEpoch)
{
    private readonly ConcurrentQueue<ExternalActivationTimer> _timers = new();

    /// <summary>Runs synchronously while the service arms its deadline timer.</summary>
    internal Action? OnTimerCreated { get; set; }

    /// <summary>Gets or sets an optional exception raised after an invocation timer is disposed.</summary>
    internal Exception? TimerDisposeException { get; set; }

    /// <summary>Gets the first timer created by an invocation.</summary>
    internal ExternalActivationTimer Timer => _timers.TryPeek(out var timer)
        ? timer
        : throw new InvalidOperationException("No activation timer has been created.");

    /// <summary>Gets a snapshot of every independently owned invocation timer.</summary>
    internal IReadOnlyList<ExternalActivationTimer> Timers => _timers.ToArray();

    /// <summary>Creates a tracked timer and supports deterministic setup-time advancement.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        OnTimerCreated?.Invoke();
        var inner = base.CreateTimer(callback, state, dueTime, period);
        var timer = new ExternalActivationTimer(inner) { ThrowOnDispose = TimerDisposeException };
        _timers.Enqueue(timer);
        return timer;
    }
}

/// <summary>Tracks disposal of one fake-clock timer and can fail without affecting its inner clock registration.</summary>
internal sealed class ExternalActivationTimer(ITimer inner) : ITimer
{
    private int _isDisposed;

    /// <summary>Gets whether the timer has been disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>Gets or sets a synchronous failure raised after disposing the inner timer.</summary>
    internal Exception? ThrowOnDispose { get; set; }

    /// <inheritdoc />
    public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
        inner.Dispose();
        if (ThrowOnDispose is { } exception)
        {
            throw exception;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Captures a selected diagnostic and deliberately withholds callback delivery until asked.</summary>
internal sealed class ExternalActivationDelayedTimer(TimerCallback callback, object? state) : ITimer
{
    private int _isDisposed;

    /// <summary>Gets whether the timer resource was released.</summary>
    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>Delivers the deadline callback once, independent of the clock provider.</summary>
    internal void Fire() => callback(state);

    /// <inheritdoc />
    public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _isDisposed, 1);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Advances fake monotonic time while retaining a timer whose callback is intentionally delayed.</summary>
internal sealed class ExternalActivationDelayedClock(DateTimeOffset? start = null) : FakeTimeProvider(start ?? DateTimeOffset.UnixEpoch)
{
    private ExternalActivationDelayedTimer? _timer;

    /// <summary>Gets the deadline timer created by the service.</summary>
    internal ExternalActivationDelayedTimer Timer => _timer
        ?? throw new InvalidOperationException("No activation timer has been created.");

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _timer = new ExternalActivationDelayedTimer(callback, state);
}

/// <summary>Separates wall-clock adjustment from an unchanged monotonic timestamp for deadline assertions.</summary>
internal sealed class ExternalActivationWallClock : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private ExternalActivationDelayedTimer? _timer;

    /// <summary>Gets the manually created deadline timer.</summary>
    internal ExternalActivationDelayedTimer Timer => _timer
        ?? throw new InvalidOperationException("No activation timer has been created.");

    /// <summary>Changes wall time without advancing the monotonic timestamp.</summary>
    internal void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <inheritdoc />
    public override long GetTimestamp() => 0;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _timer = new ExternalActivationDelayedTimer(callback, state);
}
