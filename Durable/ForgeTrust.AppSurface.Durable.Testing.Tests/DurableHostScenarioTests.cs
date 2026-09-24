using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class DurableHostScenarioTests
{
    [Fact]
    public void Constructor_rejects_missing_providers_and_nonpositive_timeouts()
    {
        var health = new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy));
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)));
        var request = new DurableRuntimePumpRequest();

        Assert.Throws<ArgumentNullException>(() => new DurableHostScenario(null!, admission, request));
        Assert.Throws<ArgumentNullException>(() => new DurableHostScenario(health, null!, request));
        Assert.Throws<ArgumentNullException>(() => new DurableHostScenario(health, admission, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableHostScenario(health, admission, request,
            observationTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableHostScenario(health, admission, request,
            overallTimeout: TimeSpan.FromTicks(-1)));
    }

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
    public async Task Null_health_result_fails_without_replacing_latest_assessment()
    {
        var health = new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy), (object)null!);
        var scenario = Create(health,
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))),
            new MonotonicTimeProvider());

        var published = await scenario.AssessHealthAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.AssessHealthAsync());

        Assert.Same(published, scenario.LatestAssessment);
        Assert.Equal(2, health.Calls);
    }

    [Fact]
    public async Task Timed_out_health_fault_is_observed_without_publishing_a_late_assessment()
    {
        var clock = new MonotonicTimeProvider();
        var pending = NewSource<DurableRuntimeHealthSnapshot>();
        var scenario = Create(new QueuedHealth(pending.Task),
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))), clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(5));
        var read = scenario.AssessHealthAsync();
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        var timeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => read);
        var lateFailure = new ApplicationException("late health failure");
        pending.SetException(lateFailure);
        await Assert.ThrowsAsync<ApplicationException>(() => pending.Task);
        await Task.Yield();

        Assert.Equal(DurableScenarioPhase.Health, timeout.Phase);
        Assert.Null(scenario.LatestAssessment);
    }

    [Fact]
    public async Task Provider_cancellation_does_not_publish_health_and_observes_late_fault()
    {
        using var cts = new CancellationTokenSource();
        var pending = NewSource<DurableRuntimeHealthSnapshot>();
        var health = new QueuedHealth(pending.Task);
        var scenario = Create(health,
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))),
            new MonotonicTimeProvider());
        var read = scenario.AssessHealthAsync(cts.Token);
        Assert.Equal(1, health.Calls);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        pending.SetException(new ApplicationException("late canceled health failure"));
        await Task.Yield();
        Assert.Null(scenario.LatestAssessment);
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
    public async Task Observation_budget_expiring_before_health_call_skips_provider()
    {
        var clock = new MonotonicTimeProvider();
        var health = new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy));
        var scenario = Create(health,
            new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused))), clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(10));
        clock.AdvanceOnThirdTimestampRead(TimeSpan.FromSeconds(1));

        var timeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => scenario.AssessHealthAsync());

        Assert.Equal(DurableScenarioTimeoutReason.Observation, timeout.Reason);
        Assert.Null(timeout.Invocation);
        Assert.Equal(0, health.Calls);
    }

    [Fact]
    public async Task Observation_budget_expiring_before_pump_skips_admission_and_retains_no_handle()
    {
        var clock = new MonotonicTimeProvider();
        var admission = new QueuedAdmission(_ => ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)));
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)), admission, clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(10));
        await scenario.AssessHealthAsync();
        clock.AdvanceOnThirdTimestampRead(TimeSpan.FromSeconds(1));

        var timeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => scenario.RunDirectPumpOnceAsync());

        Assert.Equal(DurableScenarioPhase.Pump, timeout.Phase);
        Assert.Equal(DurableScenarioTimeoutReason.Observation, timeout.Reason);
        Assert.Null(timeout.Invocation);
        Assert.Equal(0, admission.Calls);
        Assert.Empty(scenario.PumpInvocations);
    }

    [Fact]
    public async Task Null_late_attempt_faults_retained_handle_and_can_be_cleared()
    {
        var clock = new MonotonicTimeProvider();
        var pending = NewSource<DurableRuntimePumpAttempt>();
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)),
            new QueuedAdmission(_ => new ValueTask<DurableRuntimePumpAttempt>(pending.Task)), clock,
            observation: TimeSpan.FromSeconds(1), overall: TimeSpan.FromSeconds(10));
        await scenario.AssessHealthAsync();
        var run = scenario.RunDirectPumpOnceAsync();
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var timeout = await Assert.ThrowsAsync<DurableScenarioTimeoutException>(() => run);
        var invocation = Assert.IsType<DurableScenarioPumpInvocation>(timeout.Invocation);

        pending.SetResult(null!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.Completion);
        Assert.Contains("null attempt", error.Message);
        Assert.Equal(1, scenario.ClearCompletedPumpInvocations());
        Assert.Empty(scenario.PumpInvocations);
    }

    [Fact]
    public async Task Asynchronous_admission_failure_is_preserved_by_return_and_handle()
    {
        var failure = new ApplicationException("admission failed asynchronously");
        var pending = NewSource<DurableRuntimePumpAttempt>();
        var scenario = Create(new QueuedHealth(Snapshot(DurableRuntimeHealthState.Healthy)),
            new QueuedAdmission(_ => new ValueTask<DurableRuntimePumpAttempt>(pending.Task)), new MonotonicTimeProvider());
        await scenario.AssessHealthAsync();
        var run = scenario.RunDirectPumpOnceAsync();
        pending.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => run));
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => Assert.Single(scenario.PumpInvocations).Completion));
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
            return CancelWhenRequestedAsync(token);
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

        static async ValueTask<DurableRuntimePumpAttempt> CancelWhenRequestedAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The canceled delay must not complete successfully.");
        }
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
                null => ValueTask.FromResult<DurableRuntimeHealthSnapshot>(null!),
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
        private int _timestampReadsUntilAdvance = -1;
        private TimeSpan _advanceOnElapsedRead;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            lock (_gate)
            {
                if (_timestampReadsUntilAdvance > 0 && --_timestampReadsUntilAdvance == 0)
                {
                    _ticks = checked(_ticks + _advanceOnElapsedRead.Ticks);
                    _utcNow += _advanceOnElapsedRead;
                    _timestampReadsUntilAdvance = -1;
                }
                return _ticks;
            }
        }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public void AdjustUtc(TimeSpan amount) { lock (_gate) _utcNow += amount; }
        public void AdvanceOnThirdTimestampRead(TimeSpan amount)
        {
            lock (_gate)
            {
                _advanceOnElapsedRead = amount;
                // The API records one timestamp, then checks overall time before observation time.
                _timestampReadsUntilAdvance = 3;
            }
        }
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
