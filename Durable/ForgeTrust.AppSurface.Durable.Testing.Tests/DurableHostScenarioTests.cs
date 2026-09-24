using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class DurableHostScenarioTests
{
    [Theory]
    [InlineData(DurableRuntimeHealthState.Healthy)]
    [InlineData(DurableRuntimeHealthState.NotStarted)]
    [InlineData(DurableRuntimeHealthState.Stale)]
    [InlineData(DurableRuntimeHealthState.Draining)]
    [InlineData(DurableRuntimeHealthState.Incompatible)]
    [InlineData(DurableRuntimeHealthState.Unavailable)]
    public async Task Assessment_is_advisory_and_admission_runs_once_for_every_health_state(DurableRuntimeHealthState state)
    {
        var snapshot = new DurableHealthSnapshotBuilder().ForState(state).Build();
        var health = new QueuedHealth(snapshot);
        var attempt = Attempt(DurableRuntimePumpAttemptKind.Refused);
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(attempt));
        var scenario = Create(health, admission, new MonotonicTimeProvider());

        var assessment = await scenario.AssessHealthAsync();
        var actual = await scenario.RunDirectPumpOnceAsync();

        Assert.Same(snapshot, assessment.Snapshot);
        Assert.Same(attempt, actual);
        Assert.Equal(1, admission.Calls);
        Assert.Same(assessment, Assert.Single(scenario.PumpInvocations).Assessment);
    }

    [Fact]
    public async Task Missing_assessment_throws_and_one_assessment_can_be_reused()
    {
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Completed)));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, new MonotonicTimeProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunDirectPumpOnceAsync());
        var assessment = await scenario.AssessHealthAsync();
        await scenario.RunDirectPumpOnceAsync();
        await scenario.RunDirectPumpOnceAsync();
        Assert.Equal(2, admission.Calls);
        Assert.All(scenario.PumpInvocations, invocation => Assert.Same(assessment, invocation.Assessment));
    }

    [Fact]
    public async Task Concurrent_assessments_publish_by_completion_and_pumps_capture_published_assessment()
    {
        var first = NewSource<DurableRuntimeHealthSnapshot>();
        var second = NewSource<DurableRuntimeHealthSnapshot>();
        var health = new QueuedHealth(first.Task, second.Task);
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)));
        var scenario = Create(health, admission, new MonotonicTimeProvider());
        var older = scenario.AssessHealthAsync();
        var newer = scenario.AssessHealthAsync();
        var laterSnapshot = Snapshot(DurableRuntimeHealthState.Unavailable);
        second.SetResult(laterSnapshot);
        var laterAssessment = await newer;
        first.SetResult(Snapshot(DurableRuntimeHealthState.Healthy));
        var earlierAssessment = await older;

        Assert.Equal(1, laterAssessment.Sequence);
        Assert.Equal(2, earlierAssessment.Sequence);
        Assert.Same(earlierAssessment, scenario.LatestAssessment);
        await scenario.RunDirectPumpOnceAsync();
        Assert.Same(earlierAssessment, Assert.Single(scenario.PumpInvocations).Assessment);
    }

    [Theory]
    [InlineData(DurableRuntimePumpAttemptKind.Completed)]
    [InlineData(DurableRuntimePumpAttemptKind.Refused)]
    [InlineData(DurableRuntimePumpAttemptKind.Unavailable)]
    [InlineData(DurableRuntimePumpAttemptKind.Incompatible)]
    public async Task Returns_the_exact_admission_attempt(DurableRuntimePumpAttemptKind kind)
    {
        var expected = Attempt(kind);
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(expected));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, new MonotonicTimeProvider());
        await scenario.AssessHealthAsync();
        Assert.Same(expected, await scenario.RunDirectPumpOnceAsync());
    }

    [Fact]
    public async Task Cancellation_before_assessment_prevents_provider_call()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var health = new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy));
        var scenario = Create(health, new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))), new MonotonicTimeProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.AssessHealthAsync(cts.Token));
        Assert.Equal(0, health.Calls);
    }

    [Fact]
    public async Task Cancellation_before_pump_prevents_admission()
    {
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, new MonotonicTimeProvider());
        await scenario.AssessHealthAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunDirectPumpOnceAsync(cts.Token));
        Assert.Equal(0, admission.Calls);
        Assert.Empty(scenario.PumpInvocations);
    }

    [Fact]
    public async Task Observation_timeout_for_health_and_overall_timeout_before_later_pump_are_distinct()
    {
        var clock = new MonotonicTimeProvider();
        var pendingHealth = NewSource<DurableRuntimeHealthSnapshot>();
        var healthScenario = Create(new QueuedHealth(pendingHealth.Task),
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))), clock,
            observation: TimeSpan.FromSeconds(2), overall: TimeSpan.FromSeconds(10));
        var read = healthScenario.AssessHealthAsync();
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        var healthTimeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => read);
        Assert.Equal(DurableScenarioPhase.Health, healthTimeout.Phase);
        Assert.Equal(DurableScenarioTimeoutReason.Observation, healthTimeout.Reason);
        Assert.Null(healthTimeout.Invocation);
        pendingHealth.SetResult(Snapshot(DurableRuntimeHealthState.Healthy));
        await Task.Yield();
        Assert.Null(healthScenario.LatestAssessment);

        var overallClock = new MonotonicTimeProvider();
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)));
        var overallScenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, overallClock,
            observation: TimeSpan.FromSeconds(20), overall: TimeSpan.FromSeconds(3));
        await overallScenario.AssessHealthAsync();
        overallClock.Advance(TimeSpan.FromSeconds(3));
        var overallTimeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => overallScenario.RunDirectPumpOnceAsync());
        Assert.Equal(DurableScenarioPhase.Pump, overallTimeout.Phase);
        Assert.Equal(DurableScenarioTimeoutReason.Overall, overallTimeout.Reason);
        Assert.Null(overallTimeout.Invocation);
        Assert.Equal(0, admission.Calls);
    }

    [Fact]
    public async Task Pump_observation_timeout_retains_handle_and_late_success_and_fault_are_awaitable()
    {
        var clock = new MonotonicTimeProvider();
        var completion = NewSource<DurableRuntimePumpAttempt>();
        var admission = new QueuedAdmission(_ => new ValueTask<DurableRuntimePumpAttempt>(completion.Task));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(10));
        await scenario.AssessHealthAsync();
        var run = scenario.RunDirectPumpOnceAsync();
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var timeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => run);
        var handle = Assert.IsType<DurableScenarioPumpInvocation>(timeout.Invocation);
        Assert.True(timeout.InvocationStarted);
        Assert.True(timeout.ExecutionStatusUnknown);
        Assert.Same(handle, Assert.Single(scenario.PumpInvocations));
        var result = Attempt(DurableRuntimePumpAttemptKind.Completed);
        completion.SetResult(result);
        Assert.Same(result, await handle.Completion);
        Assert.Same(result, await handle.Completion);
        Assert.Equal(1, scenario.ClearCompletedPumpInvocations());
        Assert.Empty(scenario.PumpInvocations);

        var faultClock = new MonotonicTimeProvider();
        var lateFailure = new ApplicationException("late provider failure");
        var pending = NewSource<DurableRuntimePumpAttempt>();
        var faultScenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)),
            new QueuedAdmission(_ => new ValueTask<DurableRuntimePumpAttempt>(pending.Task)), faultClock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(10));
        await faultScenario.AssessHealthAsync();
        var faultRun = faultScenario.RunDirectPumpOnceAsync();
        await faultClock.WaitForTimerAsync();
        faultClock.Advance(TimeSpan.FromSeconds(1));
        var faultTimeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => faultRun);
        pending.SetException(lateFailure);
        Assert.Same(lateFailure, await Assert.ThrowsAsync<ApplicationException>(() => faultTimeout.Invocation!.Completion));
    }

    [Fact]
    public async Task Completion_at_deadline_wins_and_wall_clock_is_irrelevant()
    {
        var clock = new MonotonicTimeProvider();
        var snapshot = Snapshot(DurableRuntimeHealthState.Stale);
        var scenario = Create(new AdvancingHealth(clock, TimeSpan.FromSeconds(1), snapshot),
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))), clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(5));
        clock.AdjustUtc(TimeSpan.FromDays(-30));
        var assessment = await scenario.AssessHealthAsync();
        Assert.Same(snapshot, assessment.Snapshot);
    }

    [Fact]
    public async Task Caller_cancellation_after_invocation_is_forwarded_and_handle_retains_completion()
    {
        using var cts = new CancellationTokenSource();
        var entered = NewSource();
        var admission = new QueuedAdmission(token =>
        {
            Assert.Equal(cts.Token, token);
            entered.SetResult();
            return new ValueTask<DurableRuntimePumpAttempt>(Task.Delay(Timeout.Infinite, token).ContinueWith<DurableRuntimePumpAttempt>(
                _ => throw new OperationCanceledException(token), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        });
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, new MonotonicTimeProvider());
        await scenario.AssessHealthAsync();
        var run = scenario.RunDirectPumpOnceAsync(cts.Token);
        await entered.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var handle = Assert.Single(scenario.PumpInvocations);
        Assert.True(handle.Completion.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.Completion);
    }

    [Fact]
    public async Task Synchronous_admission_exception_is_rethrown_and_faults_retained_handle()
    {
        var failure = new InvalidOperationException("synchronous");
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)),
            new QueuedAdmission(_ => throw failure), new MonotonicTimeProvider());
        await scenario.AssessHealthAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunDirectPumpOnceAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Assert.Single(scenario.PumpInvocations).Completion));
    }

    [Fact]
    public async Task Pump_handle_keeps_exact_request_and_shallow_payload_reference_across_reuse_and_clear()
    {
        var payload = new MutablePayload { Value = "before" };
        var request = new DurableRuntimePumpRequest();
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Completed)));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, new MonotonicTimeProvider(), request);
        await scenario.AssessHealthAsync();
        await scenario.RunDirectPumpOnceAsync();
        var invocation = Assert.Single(scenario.PumpInvocations);
        payload.Value = "after";
        Assert.Same(request, invocation.Request);
        Assert.Equal("after", payload.Value);
        Assert.Equal(1, scenario.ClearCompletedPumpInvocations());
        await scenario.RunDirectPumpOnceAsync();
        Assert.Equal(2, admission.Calls);
        Assert.Single(scenario.PumpInvocations);
    }

    private static DurableHostScenario Create(IDurableRuntimeHealth health, QueuedAdmission admission, MonotonicTimeProvider clock,
        DurableRuntimePumpRequest? request = null, TimeSpan? observation = null, TimeSpan? overall = null) =>
        new(health, admission, request ?? new DurableRuntimePumpRequest(), clock,
            observation ?? TimeSpan.FromSeconds(30), overall ?? TimeSpan.FromMinutes(2));

    private static DurableRuntimeHealthSnapshot Snapshot(DurableRuntimeHealthState state) => new DurableHealthSnapshotBuilder().ForState(state).Build();

    private static DurableRuntimePumpAttempt Attempt(DurableRuntimePumpAttemptKind kind) => kind switch
    {
        DurableRuntimePumpAttemptKind.Completed => new(kind, new(0, 0, 0, 0, 0, false, null, TimeSpan.Zero), null),
        DurableRuntimePumpAttemptKind.Refused => new(kind, null, null),
        DurableRuntimePumpAttemptKind.Unavailable => new(kind, null, DurableProblemCodes.StoreUnavailable),
        DurableRuntimePumpAttemptKind.Incompatible => new(kind, null, DurableProblemCodes.SchemaMissing),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class MutablePayload { public string Value { get; set; } = string.Empty; }

    private sealed class QueuedHealth(params object[] responses) : IDurableRuntimeHealth
    {
        private readonly ConcurrentQueue<object> _responses = new(responses);
        public int Calls { get; private set; }
        public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var response = _responses.TryDequeue(out var next) ? next : throw new InvalidOperationException("No queued health response.");
            return response switch
            {
                DurableRuntimeHealthSnapshot snapshot => ValueTask.FromResult(snapshot),
                Task<DurableRuntimeHealthSnapshot> task => new(task),
                _ => throw new InvalidOperationException("Unsupported health response."),
            };
        }
    }

    private sealed class QueuedAdmission(Func<CancellationToken, ValueTask<DurableRuntimePumpAttempt>> handler) : IDurableRuntimePumpAdmission
    {
        public int Calls { get; private set; }
        public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(DurableRuntimePumpRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(cancellationToken);
        }
    }

    private sealed class AdvancingHealth(MonotonicTimeProvider clock, TimeSpan advance, DurableRuntimeHealthSnapshot snapshot) : IDurableRuntimeHealth
    {
        public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(advance);
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class MonotonicTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private readonly TaskCompletionSource _firstTimerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ticks;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public void AdjustUtc(TimeSpan amount) { lock (_gate) _utcNow += amount; }
        public Task WaitForTimerAsync() => _firstTimerCreated.Task;
        public void Advance(TimeSpan amount)
        {
            FakeTimer[] due;
            lock (_gate)
            {
                _ticks = checked(_ticks + amount.Ticks);
                _utcNow += amount;
                due = _timers.Where(timer => timer.IsDue(_ticks)).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            _firstTimerCreated.TrySetResult();
            return timer;
        }
        private sealed class FakeTimer(MonotonicTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;
            public bool IsDue(long now) => !_disposed && now >= _due;
            public void Fire()
            {
                if (_disposed) return;
                callback(state);
                if (_period == Timeout.InfiniteTimeSpan) _due = long.MaxValue;
                else _due = checked(_due + _period.Ticks);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.GetTimestamp() + Math.Max(0, dueTime.Ticks));
                return true;
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
