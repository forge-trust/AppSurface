namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Deterministic scheduling controls; none constructs an owner, kernel identity or custody.</summary>
public sealed class SupervisionTeardownDeadlineTests
{
    [Fact]
    public void ExplicitBeginIsRequiredAndNullClockRejects()
    {
        Assert.Throws<ArgumentNullException>(() => new SupervisionTeardownDeadline(null!));
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        Assert.False(deadline.IsStarted);
        Reject(() => { _ = deadline.Remaining; });
        Reject(() => { _ = deadline.Token; });
        Assert.Equal(0, clock.CreatedTimers);
    }

    [Theory]
    [InlineData(3, 10, 3)]
    [InlineData(20, 10, 10)]
    [InlineData(10, 10, 10)]
    public void FirstReservationUsesTheLowerBound(int original, int reserved, int expected)
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(original), TimeSpan.FromSeconds(reserved));
        Assert.True(deadline.IsStarted);
        Assert.Equal(TimeSpan.FromSeconds(expected), deadline.Remaining);
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(expected) - TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.FromTicks(1), deadline.Remaining);
        Assert.False(token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(token.IsCancellationRequested);
        Reject(() => { _ = deadline.Remaining; });
        Reject(() => { _ = deadline.Token; });
    }

    [Fact]
    public void RepeatedBeginCannotExtendOrReplaceTheFirstSchedule()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(4));
        deadline.Begin(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        Assert.Equal(token, deadline.Token);
        Assert.Equal(1, clock.CreatedTimers);
        clock.Advance(TimeSpan.FromSeconds(6));
        deadline.Begin(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        Reject(() => { _ = deadline.Token; });
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Fact]
    public async Task ConcurrentBeginKeepsOneReservationAndOneTimer()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 16).Select(async _ =>
        {
            await start.Task;
            deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(1, clock.CreatedTimers);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Remaining);
    }

    [Fact]
    public void ReservationPrecedesTimerProviderReentryAndSetupConsumesTheSameTime()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        clock.OnCreate = () =>
        {
            deadline.Begin(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10));
            clock.Advance(TimeSpan.FromSeconds(3), fireTimers: false);
        };
        deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.Equal(1, clock.CreatedTimers);
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Remaining);
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void CollectionExitCustodyAndAccountsConsumeOneSchedule()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        foreach (var phaseSeconds in new[] { 4, 3, 5, 8 })
        {
            clock.Advance(TimeSpan.FromSeconds(phaseSeconds));
            deadline.Begin(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(20));
        }
        Assert.True(token.IsCancellationRequested);
        Reject(() => { _ = deadline.Remaining; });
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(3601, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    [InlineData(10, 661)]
    public void InvalidBoundsRejectBeforeAcquisition(int original, int reserved)
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => deadline.Begin(
            TimeSpan.FromSeconds(original), TimeSpan.FromSeconds(reserved)));
        Assert.Equal(0, clock.CreatedTimers);
        Assert.False(deadline.IsStarted);
        Reject(() => { _ = deadline.Remaining; });
        deadline.Begin(TimeSpan.FromSeconds(3600), TimeSpan.FromSeconds(660));
        Assert.Equal(TimeSpan.FromSeconds(660), deadline.Remaining);
    }

    [Fact]
    public void LocalLinkedCancellationCannotCancelTheOwnedSource()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var caller = new CancellationTokenSource();
        using var local = CancellationTokenSource.CreateLinkedTokenSource(token, caller.Token);
        caller.Cancel();
        Assert.True(local.IsCancellationRequested);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(10), deadline.Remaining);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void UtcChangesCannotRenewMonotonicTime()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        clock.Utc = clock.Utc.AddYears(-10);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        clock.Utc = clock.Utc.AddYears(20);
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        clock.Advance(TimeSpan.FromSeconds(6));
        Reject(() => { _ = deadline.Token; });
    }

    [Fact]
    public void DelayedTimerCannotMakeAnExpiredGetterUsable()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(10), fireTimers: false);
        Assert.False(token.IsCancellationRequested);
        Reject(() => { _ = deadline.Remaining; });
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ObservedMonotonicRegressionPermanentlyRejects()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        clock.Timestamp -= TimeSpan.FromSeconds(1).Ticks;
        Reject(() => { _ = deadline.Remaining; });
        Assert.True(token.IsCancellationRequested);
        Reject(() => deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.Throws<InvalidOperationException>(() => deadline.Dispose());
    }

    [Fact]
    public void DisposeCancelsBorrowersStopsTimerAndRejectsLaterUse()
    {
        var clock = new ManualClock();
        var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        deadline.Dispose();
        deadline.Dispose();
        Assert.True(deadline.IsStarted);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, clock.DisposedTimers);
        Assert.Throws<ObjectDisposedException>(() => { _ = deadline.Remaining; });
        Assert.Throws<ObjectDisposedException>(() => { _ = deadline.Token; });
        Assert.Throws<ObjectDisposedException>(() => deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void DisposeBeforeBeginCreatesNoTimerAndCannotReopen()
    {
        var clock = new ManualClock();
        var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Dispose();
        Assert.False(deadline.IsStarted);
        Assert.Throws<ObjectDisposedException>(() => deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.Equal(0, clock.CreatedTimers);
    }

    [Fact]
    public void TimerAcquisitionFailureCannotBeRetriedOrEchoItsException()
    {
        var clock = new ManualClock { OnCreate = () => throw new IOException("private-timer-canary") };
        var deadline = new SupervisionTeardownDeadline(clock);
        Reject(() => deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Reject(() => deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.True(deadline.IsStarted);
        Assert.Equal(1, clock.CreatedTimers);
        Assert.Throws<InvalidOperationException>(() => deadline.Dispose());
    }

    [Fact]
    public void ExpiryDuringTimerSetupRetainsTheClaimAndCannotReopen()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        clock.OnCreate = () => clock.Advance(TimeSpan.FromSeconds(10), fireTimers: false);
        Reject(() => deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)));
        Assert.True(deadline.IsStarted);
        deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Reject(() => { _ = deadline.Token; });
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Fact]
    public void EarlyTimerCallbackRearmsOnlyToTheOriginalExpiry()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.FireEarly();
        Assert.Equal(TimeSpan.FromSeconds(8), deadline.Remaining);
        Assert.False(token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Fact]
    public void PreBeginBorrowersReceiveLateExpiryWithoutStartingOrRenewingTheSchedule()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        var root = deadline.Cancellation;
        using var caller = new CancellationTokenSource();
        using var observations = CancellationTokenSource.CreateLinkedTokenSource(root, caller.Token);
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(root, caller.Token);
        using var acceptedIo = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
        Assert.True(root.CanBeCanceled);
        Assert.Equal(root, deadline.Cancellation);
        Assert.False(deadline.IsStarted);
        Assert.Equal(0, clock.CreatedTimers);
        Reject(() => { _ = deadline.Remaining; });
        Reject(() => { _ = deadline.Token; });
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(root.IsCancellationRequested);
        Assert.False(deadline.IsStarted);
        Assert.Equal(0, clock.CreatedTimers);

        deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(4));
        Assert.Equal(root, deadline.Token);
        Assert.Equal(root, deadline.Cancellation);
        clock.Advance(TimeSpan.FromSeconds(4), fireTimers: false);
        Assert.False(root.IsCancellationRequested);
        Reject(() => { _ = deadline.Cancellation; }); // Inspection must enforce delayed timer expiry.
        Assert.True(observations.IsCancellationRequested);
        Assert.True(requests.IsCancellationRequested);
        Assert.True(acceptedIo.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        deadline.Begin(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(20));
        Reject(() => { _ = deadline.Cancellation; });
        Assert.True(deadline.IsStarted);
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Fact]
    public void PreBeginLocalCancellationIsIsolatedAndUnavailableHoldersRejectBorrowing()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        var root = deadline.Cancellation;
        using var caller = new CancellationTokenSource();
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(root);
        using (var local = CancellationTokenSource.CreateLinkedTokenSource(root, caller.Token))
        {
            caller.Cancel();
            Assert.True(local.IsCancellationRequested);
            Assert.False(root.IsCancellationRequested);
            Assert.False(requests.IsCancellationRequested);
        }
        Assert.Equal(root, deadline.Cancellation);
        Assert.False(deadline.IsStarted);
        Assert.Equal(0, clock.CreatedTimers);
        deadline.Begin(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(root.IsCancellationRequested);
        Assert.True(requests.IsCancellationRequested);

        var failed = new SupervisionTeardownDeadline(new ManualClock
        { OnCreate = () => throw new IOException("private-timer-canary") });
        var failedBorrower = failed.Cancellation;
        Reject(() => failed.Begin(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        Reject(() => { _ = failed.Cancellation; });
        Assert.True(failedBorrower.IsCancellationRequested);
        Assert.True(failed.IsStarted);
        Assert.Throws<InvalidOperationException>(() => failed.Dispose());
        failed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = failed.Cancellation; });

        using var unstarted = new SupervisionTeardownDeadline(new ManualClock());
        var unstartedBorrower = unstarted.Cancellation;
        unstarted.Dispose();
        Assert.True(unstartedBorrower.IsCancellationRequested);
        Assert.False(unstarted.IsStarted);
        Assert.Throws<ObjectDisposedException>(() => { _ = unstarted.Cancellation; });
    }

    [Fact]
    public async Task LateExpiryRequestsAbortButStillJoinsTheOriginalIgnoringOperation()
    {
        var clock = new ManualClock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Cancellation);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> OriginalOperation()
        {
            entered.SetResult();
            return await release.Task; // This intentional procedure ignores cancellation until released.
        }
        var original = OriginalOperation();
        var joined = SupervisionOperationJoin.RunAsync(original,
            () => { abortRequested.TrySetResult(); }, observation.Token);
        await entered.Task;
        Assert.False(deadline.IsStarted);
        Assert.Equal(0, clock.CreatedTimers);
        deadline.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        await abortRequested.Task;
        Assert.True(observation.IsCancellationRequested);
        Assert.False(original.IsCompleted);
        Assert.False(joined.IsCompleted);
        release.SetResult("joined");
        Assert.Equal("joined", await original);
        await Assert.ThrowsAsync<OperationCanceledException>(() => joined);
        Assert.True(original.IsCompletedSuccessfully);
        Assert.True(joined.IsCompleted);
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("The teardown schedule is unavailable.", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        internal long Timestamp { get; set; }
        internal DateTimeOffset Utc { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        internal Action? OnCreate { get; set; }
        internal int CreatedTimers { get; private set; }
        internal int DisposedTimers { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            CreatedTimers++;
            OnCreate?.Invoke();
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            Timestamp += elapsed.Ticks;
            if (fireTimers)
                foreach (var timer in _timers.ToArray()) timer.FireDue();
        }
        internal void FireEarly() { foreach (var timer in _timers.ToArray()) timer.FireEarly(); }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long? _due;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Timestamp + dueTime.Ticks;
            return true;
        }
        internal void FireDue()
        {
            if (_disposed || _due is not long due || clock.Timestamp < due) return;
            _due = null;
            callback(state);
        }
        internal void FireEarly()
        {
            if (!_disposed) { _due = null; callback(state); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            clock.DisposedTimers++;
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
