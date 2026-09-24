using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class FakeRecordingTests
{
    [Theory]
    [InlineData(DurableRuntimePumpAttemptKind.Completed)]
    [InlineData(DurableRuntimePumpAttemptKind.Refused)]
    [InlineData(DurableRuntimePumpAttemptKind.Unavailable)]
    [InlineData(DurableRuntimePumpAttemptKind.Incompatible)]
    public async Task Admission_records_exact_attempt_request_and_limits(DurableRuntimePumpAttemptKind kind)
    {
        var attempt = Attempt(kind);
        var pump = new RecordingDurableRuntimePump { Admission = (_, _) => ValueTask.FromResult(attempt) };
        var request = new DurableRuntimePumpRequest(17, TimeSpan.FromSeconds(3), DurableRuntimeSurface.Flow | DurableRuntimeSurface.Schedule);

        var actual = await pump.TryRunOnceAsync(request);

        Assert.Same(attempt, actual);
        var call = Assert.Single(pump.History);
        Assert.Same(request, call.Request);
        Assert.Equal(17, call.MaximumItems);
        Assert.Equal(TimeSpan.FromSeconds(3), call.TimeBudget);
        Assert.Equal(DurableRuntimeSurface.Flow | DurableRuntimeSurface.Schedule, call.Surfaces);
        Assert.Equal(kind, Assert.IsType<DurableRuntimePumpAttempt>(call.Result).Kind);
        Assert.True(call.IsCompleted);
    }

    [Fact]
    public async Task Defaults_complete_an_empty_pass_for_both_contracts()
    {
        var pump = new RecordingDurableRuntimePump();
        var request = new DurableRuntimePumpRequest();
        var admitted = await pump.TryRunOnceAsync(request);
        var legacy = await pump.RunOnceAsync(request);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, admitted.Kind);
        Assert.Equal(0, admitted.Result!.Processed);
        Assert.Equal(0, legacy.Processed);
        Assert.Equal([DurableRuntimePumpCallKind.Admission, DurableRuntimePumpCallKind.Pump], pump.History.Select(x => x.Kind));
    }

    [Fact]
    public async Task Cancellation_before_delegate_prevents_invocation_and_delegate_exception_is_preserved()
    {
        var invoked = false;
        var pump = new RecordingDurableRuntimePump { Admission = (_, _) => { invoked = true; return ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)); } };
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pump.TryRunOnceAsync(new(), canceled.Token));
        Assert.False(invoked);
        Assert.True(Assert.Single(pump.History).CancellationRequestedAtCompletion);

        var failure = new InvalidOperationException("expected");
        pump.Admission = (_, _) => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await pump.TryRunOnceAsync(new())));
        Assert.Same(failure, pump.History[^1].Exception);
        Assert.DoesNotContain("expected", pump.History[^1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_that_honors_cancellation_records_its_canceled_completion()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new RecordingDurableRuntimePump
        {
            Admission = async (_, token) =>
            {
                entered.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The canceled delay must not complete successfully.");
            },
        };

        var running = pump.TryRunOnceAsync(new(), cancellation.Token).AsTask();
        Assert.Equal(cancellation.Token, await entered.Task);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var call = Assert.Single(pump.History);
        Assert.True(call.IsCompleted);
        Assert.True(call.CancellationRequestedAtCompletion);
        Assert.IsAssignableFrom<OperationCanceledException>(call.Exception);
        Assert.Null(call.Result);
    }

    [Fact]
    public async Task Delegate_that_ignores_cancellation_retains_its_exact_result()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DurableRuntimePumpAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new RecordingDurableRuntimePump
        {
            Admission = (_, token) =>
            {
                entered.SetResult(token);
                return new ValueTask<DurableRuntimePumpAttempt>(release.Task);
            },
        };

        var running = pump.TryRunOnceAsync(new(), cancellation.Token).AsTask();
        Assert.Equal(cancellation.Token, await entered.Task);
        cancellation.Cancel();
        var expected = Attempt(DurableRuntimePumpAttemptKind.Refused);
        release.SetResult(expected);

        Assert.Same(expected, await running);
        var call = Assert.Single(pump.History);
        Assert.True(call.IsCompleted);
        Assert.True(call.CancellationRequestedAtCompletion);
        Assert.Same(expected, call.Result);
        Assert.Null(call.Exception);
    }

    [Fact]
    public async Task History_is_start_ordered_and_clear_excludes_in_flight_calls()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<DurableRuntimePumpAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new RecordingDurableRuntimePump
        {
            Admission = (request, _) => request.MaximumItems == 1
                ? WaitFirst()
                : ValueTask.FromResult(Attempt(DurableRuntimePumpAttemptKind.Refused)),
        };
        ValueTask<DurableRuntimePumpAttempt> WaitFirst() { firstEntered.SetResult(); return new(releaseFirst.Task); }
        var first = pump.TryRunOnceAsync(new(1)).AsTask();
        await firstEntered.Task;
        await pump.TryRunOnceAsync(new(2));
        Assert.Equal(new[] { 1, 2 }, pump.History.Select(call => call.MaximumItems));
        var snapshot = pump.History;
        Assert.False(snapshot[0].IsCompleted);
        Assert.True(snapshot[1].IsCompleted);
        pump.ClearHistory();
        releaseFirst.SetResult(Attempt(DurableRuntimePumpAttemptKind.Completed));
        await first;
        Assert.Equal(2, snapshot.Length);
        Assert.False(snapshot[0].IsCompleted);
        Assert.Empty(pump.History);
    }

    [Fact]
    public async Task Drain_control_records_transitions_and_honors_cancellation()
    {
        var drain = new FakeDurableRuntimeDrainControl();
        await drain.BeginDrainAsync();
        Assert.True(drain.IsDraining);
        await drain.ResumeAsync();
        Assert.False(drain.IsDraining);
        Assert.Equal(new[] { true, false }, drain.History.Select(x => x.IsDraining));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await drain.BeginDrainAsync(canceled.Token));
        Assert.Equal(2, drain.History.Length);
    }

    [Fact]
    public async Task Health_fake_returns_configured_snapshot_and_honors_cancellation()
    {
        var snapshot = new DurableRuntimeHealthSnapshot(DurableRuntimeHealthState.Healthy, null, true, true, 1, 1,
            Guid.NewGuid(), Guid.NewGuid(), "worker", null, DurableRuntimeSurface.Work, DateTimeOffset.UtcNow,
            null, null, null, false, false, 0, null, null);
        var health = new FakeDurableRuntimeHealth(snapshot);
        Assert.Same(snapshot, await health.GetAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await health.GetAsync(canceled.Token));
    }

    private static DurableRuntimePumpAttempt Attempt(DurableRuntimePumpAttemptKind kind) => kind switch
    {
        DurableRuntimePumpAttemptKind.Completed => new(kind, new(0, 0, 0, 0, 0, false, null, TimeSpan.Zero), null),
        DurableRuntimePumpAttemptKind.Refused => new(kind, null, null),
        DurableRuntimePumpAttemptKind.Unavailable => new(kind, null, DurableProblemCodes.StoreUnavailable),
        DurableRuntimePumpAttemptKind.Incompatible => new(kind, null, DurableProblemCodes.SchemaMissing),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
