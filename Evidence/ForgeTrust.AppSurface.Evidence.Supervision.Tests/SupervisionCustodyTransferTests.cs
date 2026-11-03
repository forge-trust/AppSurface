using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual task/barrier controls for procedure ordering; these create no native owner or custody proof.</summary>
public sealed class SupervisionCustodyTransferTests
{
    [Fact]
    public async Task AllNodesPreflightBeforeMutationAndSuccessWaitsForActualFinalRecheck()
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var entered = Signal();
        var release = Signal();
        var callbacks = SuccessCallbacks(calls);
        callbacks[4] = async () => { calls.Add(4); entered.SetResult(); await release.Task; };
        var run = Run(transfer, callbacks);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, calls);
            Assert.False(run.IsCompleted);
            Assert.False(transfer.SuccessfulCompleted);
            Assert.False(transfer.Failed);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transfer.SuccessfulCompleted);
        Assert.Equal(SupervisionCustodyFailure.None, transfer.FirstFailure);
    }

    [Fact]
    public async Task CancellationBeforeDispatchSkipsNewWorkButAttemptsBothCleanupCallbacks()
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertClosedAsync(Run(transfer, SuccessCallbacks(calls), cancellation.Token));
        Assert.Equal(new[] { 3, 4 }, calls);
        Assert.Equal(SupervisionCustodyFailure.Cancelled, transfer.FirstFailure);
        Assert.False(transfer.SuccessfulCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IgnoredCancellationJoinsEveryDispatchedWorkPhaseBeforeCleanup(int phase)
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var entered = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var callbacks = SuccessCallbacks(calls);
        callbacks[phase] = async () => { calls.Add(phase); entered.SetResult(); await release.Task; };
        var run = Run(transfer, callbacks, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(run.IsCompleted);
            Assert.Equal(phase == 2, calls.Contains(2));
            Assert.DoesNotContain(3, calls);
            Assert.True(transfer.Failed);
        }
        finally { release.TrySetResult(); }
        await AssertClosedAsync(run);
        Assert.Equal(Enumerable.Range(0, phase + 1).Concat(new[] { 3, 4 }), calls);
        Assert.Equal(SupervisionCustodyFailure.Cancelled, transfer.FirstFailure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public async Task FaultCancellationOrMissingActualTaskNeverSkipsRequiredCleanup(int phase, int kind)
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var callbacks = SuccessCallbacks(calls);
        callbacks[phase] = () =>
        {
            calls.Add(phase);
            return kind switch
            {
                0 => throw new IOException("private-custody-canary"),
                1 => Task.FromException(new IOException("private-custody-canary")),
                3 => Task.FromCanceled(new CancellationToken(true)),
                _ => null!,
            };
        };
        await AssertClosedAsync(Run(transfer, callbacks));
        var expected = phase < 3
            ? Enumerable.Range(0, phase + 1).Concat(new[] { 3, 4 })
            : Enumerable.Range(0, 5);
        Assert.Equal(expected, calls);
        Assert.Equal(kind == 3 ? SupervisionCustodyFailure.Cancelled : FailureFor(phase), transfer.FirstFailure);
        Assert.True(transfer.Failed);
        Assert.False(transfer.SuccessfulCompleted);
    }

    [Fact]
    public async Task MutationFailureRemainsFirstDespiteCloseAndFinalRecheckFailures()
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var callbacks = SuccessCallbacks(calls);
        for (var index = 2; index < 5; index++)
        {
            var phase = index;
            callbacks[index] = () => { calls.Add(phase); throw new IOException("private-custody-canary"); };
        }
        await AssertClosedAsync(Run(transfer, callbacks));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, calls);
        Assert.Equal(SupervisionCustodyFailure.MutationFailed, transfer.FirstFailure);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IgnoredCancellationCannotDetachEitherCleanupTask(int phase)
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var entered = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var callbacks = SuccessCallbacks(calls);
        callbacks[phase] = async () => { calls.Add(phase); entered.SetResult(); await release.Task; };
        var run = Run(transfer, callbacks, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(run.IsCompleted);
            Assert.False(transfer.SuccessfulCompleted);
            if (phase == 3) Assert.DoesNotContain(4, calls);
        }
        finally { release.TrySetResult(); }
        await AssertClosedAsync(run);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, calls);
        Assert.Equal(SupervisionCustodyFailure.Cancelled, transfer.FirstFailure);
    }

    [Fact]
    public async Task CancellationAtFinalRecheckCannotPublishSuccess()
    {
        var transfer = new SupervisionCustodyTransfer();
        using var cancellation = new CancellationTokenSource();
        var callbacks = SuccessCallbacks();
        callbacks[4] = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await AssertClosedAsync(Run(transfer, callbacks, cancellation.Token));
        Assert.Equal(SupervisionCustodyFailure.Cancelled, transfer.FirstFailure);
        Assert.False(transfer.SuccessfulCompleted);
    }

    [Fact]
    public async Task ContentionReservesExactlyOneWholeTaskBeforeAnyCallbackDispatch()
    {
        var transfer = new SupervisionCustodyTransfer();
        var entered = Signal();
        var release = Signal();
        var dispatches = 0;
        Task? retained = null;
        var callbacks = SuccessCallbacks();
        callbacks[0] = async () =>
        {
            Interlocked.Increment(ref dispatches);
            entered.TrySetResult();
            await release.Task;
        };
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try
            {
                var run = Run(transfer, callbacks);
                Interlocked.Exchange(ref retained, run);
                return true;
            }
            catch (EvidenceAdmissionException error)
            {
                Assert.Equal("ASEVD410", error.Code);
                return false;
            }
        })).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var admitted = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, admitted.Count(static value => value));
            Assert.Equal(1, dispatches);
            Assert.False(retained!.IsCompleted);
            Assert.False(transfer.SuccessfulCompleted);
        }
        finally { release.TrySetResult(); }
        await retained!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transfer.SuccessfulCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayAfterSuccessfulOrFailedCompletionNeverDispatchesAgain(bool fail)
    {
        var transfer = new SupervisionCustodyTransfer();
        var calls = new List<int>();
        var callbacks = SuccessCallbacks(calls);
        if (fail) callbacks[1] = () => { calls.Add(1); throw new IOException("private-custody-canary"); };
        var run = Run(transfer, callbacks);
        if (fail) await AssertClosedAsync(run);
        else await run.WaitAsync(TimeSpan.FromSeconds(5));
        var before = calls.ToArray();
        AssertRejected(() => { _ = Run(transfer, callbacks); });
        Assert.Equal(before, calls);
        Assert.Equal(fail, transfer.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackSelfReentryRejectsBeforeSharedTaskWaitEvenAfterAwait(bool asynchronous)
    {
        var transfer = new SupervisionCustodyTransfer();
        var callbacks = SuccessCallbacks();
        callbacks[3] = async () =>
        {
            if (asynchronous) await Task.Yield();
            AssertRejected(() => { _ = Run(transfer, SuccessCallbacks()); });
            // Observe from another task: the native callback must never execute under the owner lock.
            Assert.False(await Task.Run(() => transfer.SuccessfulCompleted).WaitAsync(TimeSpan.FromSeconds(5)));
        };
        await Run(transfer, callbacks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transfer.SuccessfulCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AbsentCallbackDoesNotConsumeTheOnlyAttempt(int phase)
    {
        var transfer = new SupervisionCustodyTransfer();
        var callbacks = SuccessCallbacks();
        callbacks[phase] = null!;
        Assert.Throws<ArgumentNullException>(() => { _ = Run(transfer, callbacks); });
        Assert.False(transfer.Failed);
        Assert.False(transfer.SuccessfulCompleted);
        await Run(transfer, SuccessCallbacks()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transfer.SuccessfulCompleted);
    }

    [Fact]
    public async Task LaterOriginalCancellationRevokesProcedureDataWithoutEnablingReplay()
    {
        var transfer = new SupervisionCustodyTransfer();
        using var cancellation = new CancellationTokenSource();
        var run = Run(transfer, SuccessCallbacks(), cancellation.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transfer.SuccessfulCompleted);
        cancellation.Cancel();
        Assert.True(run.IsCompletedSuccessfully); // The joined historical task does not become native authority.
        Assert.False(transfer.SuccessfulCompleted);
        Assert.True(transfer.Failed);
        Assert.Equal(SupervisionCustodyFailure.Cancelled, transfer.FirstFailure);
        AssertRejected(() => { _ = Run(transfer, SuccessCallbacks()); });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Func<Task>[] SuccessCallbacks(List<int>? calls = null) => Enumerable.Range(0, 5)
        .Select(index => (Func<Task>)(() => { calls?.Add(index); return Task.CompletedTask; })).ToArray();

    private static Task Run(SupervisionCustodyTransfer transfer, Func<Task>[] callbacks,
        CancellationToken token = default) => transfer.RunAsync(callbacks[0], callbacks[1], callbacks[2],
            callbacks[3], callbacks[4], token);

    private static SupervisionCustodyFailure FailureFor(int phase) => phase switch
    {
        0 => SupervisionCustodyFailure.SettlementValidationFailed,
        1 => SupervisionCustodyFailure.PreflightFailed,
        2 => SupervisionCustodyFailure.MutationFailed,
        3 => SupervisionCustodyFailure.LocalCloseFailed,
        _ => SupervisionCustodyFailure.FinalNativeRecheckFailed,
    };

    private static EvidenceAdmissionException AssertRejected(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString());
        return error;
    }

    private static async Task AssertClosedAsync(Task run)
    {
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString());
    }
}
