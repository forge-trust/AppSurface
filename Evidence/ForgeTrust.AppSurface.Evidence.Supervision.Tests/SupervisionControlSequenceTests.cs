using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable sequence/task/write-commit controls; join assertions never represent native protected proof.</summary>
public sealed class SupervisionControlSequenceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static SupervisionControlJoinFacts Joined() => new(true, true, true);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ReadyStopWaitExitCommitsOnlyAfterWritesAndExcludesTheRequestingControlHandler()
    {
        var ledger = new SupervisionWorkRegistry(); var stops = 0;
        using var handler = ledger.BeginControl();
        var sequence = new SupervisionControlSequence(ledger, _ =>
        {
            Assert.True(ledger.IsWorkloadAdmissionClosed);
            Interlocked.Increment(ref stops); return Task.FromResult(Joined());
        });
        var ready = sequence.ClaimReady();
        Assert.False(sequence.ReadyAcknowledged);
        sequence.CompleteWrite(ready, true);
        await sequence.StopAsync(default).WaitAsync(Guard);
        Assert.Equal(1, ledger.ActiveControls); // No handler self-join; worker ownership is separate.
        Assert.False(ledger.IsControlAdmissionClosed);
        var wait = sequence.ClaimWait(); Assert.True(wait.Positive);
        Assert.False(sequence.PositiveWaitAcknowledged);
        Reject(() => sequence.ClaimExit());
        sequence.CompleteWrite(wait, true);
        var exit = sequence.ClaimExit(); Assert.False(sequence.ExitAcknowledged);
        sequence.CompleteWrite(exit, true);
        Assert.True(sequence.ExitAcknowledged); Assert.False(sequence.Failed); Assert.Equal(1, stops);
        Assert.Throws<InvalidOperationException>(() => ledger.BeginWorkload());
        Reject(() => sequence.ClaimReady()); Reject(() => sequence.ClaimWait()); Reject(() => sequence.ClaimExit());
        Reject(() => sequence.CompleteWrite(exit, true));
    }

    [Fact]
    public async Task ConcurrentReadyClaimsHaveExactlyOneWinnerBeforeAnyWriteCommit()
    {
        var sequence = Create(); var winners = new ConcurrentBag<SupervisionControlSequence.Reply>();
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try { winners.Add(sequence.ClaimReady()); }
            catch (EvidenceAdmissionException error) { AssertClosed(error); }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(Guard);
        var ready = Assert.Single(winners);
        Assert.False(sequence.ReadyAcknowledged);
        sequence.CompleteWrite(ready, true); Assert.True(sequence.ReadyAcknowledged);
        await sequence.StopAsync(default).WaitAsync(Guard);
    }

    [Fact]
    public async Task FailedReadyWriteCannotAcknowledgeOrResetButCleanupStopAndNegativeWaitRemainAvailable()
    {
        var sequence = Create(); var ready = sequence.ClaimReady();
        Reject(() => sequence.CompleteWrite(ready, false));
        Assert.True(sequence.Failed); Assert.True(sequence.IsWorkAdmissionClosed);
        Assert.False(sequence.ReadyAcknowledged);
        Reject(() => sequence.CompleteWrite(ready, true)); Reject(() => sequence.ClaimReady());
        await sequence.StopAsync(default).WaitAsync(Guard);
        var wait = sequence.ClaimWait(); Assert.False(wait.Positive);
        sequence.CompleteWrite(wait, true);
        Assert.True(sequence.Failed); Assert.False(sequence.PositiveWaitAcknowledged);
        Reject(() => sequence.ClaimExit());
    }

    [Fact]
    public async Task StopIsSharedAndWaitCannotPublishBeforeOriginalProcedureAndRegistrationJoin()
    {
        var ledger = new SupervisionWorkRegistry(); var workload = ledger.BeginWorkload();
        using var handler = ledger.BeginControl();
        var entered = Gate(); var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstToken = new CancellationTokenSource(); using var otherToken = new CancellationTokenSource();
        var calls = 0;
        var sequence = new SupervisionControlSequence(ledger, token =>
        {
            Assert.Equal(firstToken.Token, token); Assert.True(ledger.IsWorkloadAdmissionClosed);
            Interlocked.Increment(ref calls); entered.SetResult(); return actual.Task;
        });
        var stop = sequence.StopAsync(firstToken.Token);
        try
        {
            Assert.True(sequence.IsWorkAdmissionClosed);
            await entered.Task.WaitAsync(Guard);
            Assert.Same(stop, sequence.StopAsync(otherToken.Token));
            Reject(() => sequence.ClaimWait()); Reject(() => sequence.ClaimExit());
            actual.SetResult(Joined());
            Assert.False(stop.IsCompleted); // Completed assertions do not erase a real registration.
            Assert.Equal(1, ledger.ActiveWorkloads); Assert.Equal(1, ledger.ActiveControls);
        }
        finally { actual.TrySetResult(Joined()); workload.Complete(); await stop.WaitAsync(Guard); }
        Assert.True(sequence.ClaimWait().Positive); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentStopAndWaitJoinAcceptedBlockedWorkWithoutHandlerSelfJoin()
    {
        var ledger = new SupervisionWorkRegistry();
        var workload = ledger.BeginWorkload();
        using var stopHandler = ledger.BeginControl();
        using var waitHandler = ledger.BeginControl();
        var stopEntered = Gate();
        var releaseStop = Gate();
        var sequence = new SupervisionControlSequence(ledger, async _ =>
        {
            Assert.True(ledger.IsWorkloadAdmissionClosed);
            stopEntered.TrySetResult();
            await releaseStop.Task;
            Assert.True(workload.Complete());
            return Joined();
        });

        var stop = sequence.StopAsync(default);
        var wait = sequence.JoinStartedStopAsync();
        try
        {
            await stopEntered.Task.WaitAsync(Guard);
            var concurrentStop = sequence.StopAsync(new CancellationToken(canceled: true));
            Assert.Same(stop, concurrentStop);
            Assert.False(wait.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => ledger.BeginWorkload());
            Assert.Equal(1, ledger.ActiveWorkloads);
            Assert.Equal(2, ledger.ActiveControls);
        }
        finally
        {
            releaseStop.TrySetResult();
            await Task.WhenAll(stop, wait).WaitAsync(Guard);
        }
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(2, ledger.ActiveControls); // Work join excludes both requesting control handlers.
        Assert.True(sequence.ClaimWait().Positive);
    }

    [Fact]
    public async Task CancelIgnoringStopRemainsOwnedUntilBothActualTaskAndLedgerJoinThenFails()
    {
        var ledger = new SupervisionWorkRegistry(); var workload = ledger.BeginWorkload(); var entered = Gate();
        var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var sequence = new SupervisionControlSequence(ledger, _ => { entered.SetResult(); return actual.Task; });
        var stop = sequence.StopAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Guard); cancellation.Cancel();
            Assert.False(stop.IsCompleted); Reject(() => sequence.ClaimWait());
            actual.SetResult(Joined()); Assert.False(stop.IsCompleted);
        }
        finally { actual.TrySetResult(Joined()); workload.Complete(); await RejectAsync(stop); }
        Assert.True(sequence.Failed); Assert.False(sequence.ClaimWait().Positive);
        Assert.Same(stop, sequence.StopAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalStopFailureIsSanitizedWithoutDetachingPendingWorkloads(bool synchronous)
    {
        var ledger = new SupervisionWorkRegistry(); var workload = ledger.BeginWorkload(); var entered = Gate();
        var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new SupervisionControlSequence(ledger, _ =>
        {
            entered.SetResult();
            if (synchronous) throw new IOException("private-canary");
            return actual.Task;
        });
        var stop = sequence.StopAsync(default);
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (!synchronous) actual.SetException(new IOException("private-canary"));
            Assert.False(stop.IsCompleted); Assert.True(sequence.IsWorkAdmissionClosed);
            Reject(() => sequence.ClaimWait());
        }
        finally
        {
            if (!synchronous) actual.TrySetException(new IOException("private-canary"));
            workload.Complete(); await RejectAsync(stop);
        }
        Assert.True(sequence.Failed); Assert.False(sequence.ClaimWait().Positive);
        Assert.Same(stop, sequence.StopAsync(default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachMissingRealProcedureJoinAssertionPreventsPositiveWait(int missing)
    {
        var facts = new SupervisionControlJoinFacts(missing != 0, missing != 1, missing != 2);
        var sequence = new SupervisionControlSequence(new(), _ => Task.FromResult(facts));
        await RejectAsync(sequence.StopAsync(default));
        var wait = sequence.ClaimWait(); Assert.False(wait.Positive);
        sequence.CompleteWrite(wait, true); Assert.True(sequence.Failed);
        Reject(() => sequence.ClaimExit());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOriginalTaskOrJoinRecordFailsClosed(bool nullTask)
    {
        var sequence = new SupervisionControlSequence(new(), _ =>
            nullTask ? null! : Task.FromResult<SupervisionControlJoinFacts>(null!));
        await RejectAsync(sequence.StopAsync(default));
        Assert.True(sequence.Failed); Assert.False(sequence.ClaimWait().Positive);
    }

    [Fact]
    public async Task EarlierWorkFailureDoesNotPreventCleanupOrBecomeResetBySuccessfulStop()
    {
        var ledger = new SupervisionWorkRegistry();
        var sequence = new SupervisionControlSequence(ledger, _ => Task.FromResult(Joined()));
        sequence.CompleteWrite(sequence.ClaimReady(), true);
        ledger.RecordFailure(SupervisionWorkFailure.WorkloadFailed);
        await sequence.StopAsync(default).WaitAsync(Guard);
        var wait = sequence.ClaimWait(); Assert.False(wait.Positive);
        sequence.CompleteWrite(wait, true);
        Assert.True(sequence.Failed); Assert.False(sequence.ReadyAcknowledged);
        Assert.Equal(SupervisionWorkFailure.WorkloadFailed, ledger.FirstFailure);
        Reject(() => sequence.ClaimExit());
    }

    [Fact]
    public async Task ForeignFabricatedNullAndReplayedClaimsCannotCommitAcknowledgements()
    {
        var sequence = Create(); var foreign = Create(); var foreignReady = foreign.ClaimReady();
        Reject(() => sequence.CompleteWrite(foreignReady, true));
        Reject(() => sequence.CompleteWrite(null!, true));
        Reject(() => sequence.CompleteWrite(new(sequence, EvidenceControlOperation.Ready, true), true));
        Assert.False(sequence.ReadyAcknowledged);
        foreign.CompleteWrite(foreignReady, true); // Foreign rejection did not consume the other owner's claim.
        var ready = sequence.ClaimReady(); sequence.CompleteWrite(ready, true);
        Reject(() => sequence.CompleteWrite(ready, true));
        await sequence.StopAsync(default).WaitAsync(Guard);
        await foreign.StopAsync(default).WaitAsync(Guard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWaitOrExitWriteCannotCommitCompletion(bool exitWrite)
    {
        var sequence = Create(); sequence.CompleteWrite(sequence.ClaimReady(), true);
        await sequence.StopAsync(default).WaitAsync(Guard);
        var reply = sequence.ClaimWait(); Assert.True(reply.Positive);
        if (exitWrite)
        {
            sequence.CompleteWrite(reply, true);
            reply = sequence.ClaimExit();
        }
        Assert.False(sequence.ExitAcknowledged);
        Reject(() => sequence.CompleteWrite(reply, false));
        Assert.True(sequence.Failed); Assert.False(sequence.PositiveWaitAcknowledged);
        Assert.False(sequence.ExitAcknowledged);
        Reject(() => sequence.CompleteWrite(reply, true)); Reject(() => sequence.ClaimExit());
    }

    [Fact]
    public async Task FailureBetweenExitClaimAndWriteCommitRejectsThePreviouslyPositiveClaim()
    {
        var sequence = Create(); sequence.CompleteWrite(sequence.ClaimReady(), true);
        await sequence.StopAsync(default).WaitAsync(Guard);
        sequence.CompleteWrite(sequence.ClaimWait(), true);
        var exit = sequence.ClaimExit(); sequence.RecordFailure();
        Reject(() => sequence.CompleteWrite(exit, true));
        Assert.False(sequence.ExitAcknowledged); Assert.True(sequence.Failed);
    }

    [Fact]
    public async Task ClaimedReadyWriteCanCommitAfterStopJoinsWithoutReopeningWork()
    {
        var sequence = Create(); var ready = sequence.ClaimReady();
        var entered = Gate(); var writeJoined = Gate();
        async Task CompleteActualWrite()
        {
            entered.SetResult(); await writeJoined.Task;
            sequence.CompleteWrite(ready, true);
        }
        var write = CompleteActualWrite();
        try
        {
            await entered.Task.WaitAsync(Guard);
            await sequence.StopAsync(default).WaitAsync(Guard);
            Assert.False(write.IsCompleted); Assert.False(sequence.ReadyAcknowledged);
            Assert.True(sequence.IsWorkAdmissionClosed);
            Reject(() => sequence.ClaimReady()); Reject(() => sequence.ClaimExit());
        }
        finally { writeJoined.TrySetResult(); await write.WaitAsync(Guard); }
        Assert.True(sequence.ReadyAcknowledged); Assert.False(sequence.Failed);
        var wait = sequence.ClaimWait(); Assert.True(wait.Positive);
        sequence.CompleteWrite(wait, true);
        sequence.CompleteWrite(sequence.ClaimExit(), true);
        Assert.True(sequence.ExitAcknowledged); Assert.True(sequence.IsWorkAdmissionClosed);
    }

    [Fact]
    public async Task FailedClaimedReadyWriteAfterStopJoinsStillAllowsOnlyFailedCleanup()
    {
        var sequence = Create(); var ready = sequence.ClaimReady();
        var entered = Gate(); var writeJoined = Gate();
        async Task CompleteFailedWrite()
        {
            entered.SetResult(); await writeJoined.Task;
            sequence.CompleteWrite(ready, false);
        }
        var write = CompleteFailedWrite();
        try
        {
            await entered.Task.WaitAsync(Guard);
            await sequence.StopAsync(default).WaitAsync(Guard);
            Assert.False(write.IsCompleted); Assert.False(sequence.ReadyAcknowledged);
        }
        finally { writeJoined.TrySetResult(); await RejectAsync(write); }
        Assert.True(sequence.Failed); Assert.True(sequence.IsWorkAdmissionClosed);
        Assert.False(sequence.ReadyAcknowledged);
        Reject(() => sequence.CompleteWrite(ready, true));
        var wait = sequence.ClaimWait(); Assert.False(wait.Positive);
        sequence.CompleteWrite(wait, true); Reject(() => sequence.ClaimExit());
    }

    [Fact]
    public async Task PositiveWaitWithoutReadyAckCannotClaimExit()
    {
        var sequence = Create(); await sequence.StopAsync(default).WaitAsync(Guard);
        var wait = sequence.ClaimWait(); Assert.True(wait.Positive);
        sequence.CompleteWrite(wait, true); Reject(() => sequence.ClaimExit());
        Assert.False(sequence.ReadyAcknowledged); Assert.False(sequence.ExitAcknowledged);
    }

    [Fact]
    public async Task AsyncStopCallbackRejectsSameOwnerSelfJoinBeforeReturningSharedTask()
    {
        var entered = Gate(); var finish = Gate();
        SupervisionControlSequence? sequence = null;
        sequence = new(new(), async _ =>
        {
            entered.SetResult(); await finish.Task;
            Reject(() => sequence!.StopAsync(default));
            Reject(() => sequence!.ClaimWait());
            return Joined();
        });
        var stop = sequence.StopAsync(default);
        try { await entered.Task.WaitAsync(Guard); Assert.False(stop.IsCompleted); }
        finally { finish.TrySetResult(); await stop.WaitAsync(Guard); }
        Assert.False(sequence.Failed);
    }

    [Fact]
    public async Task WaitJoinRetainsStopUntilOriginalCallbackAndWorkloadRegistrationBothJoin()
    {
        var ledger = new SupervisionWorkRegistry(); var workload = ledger.BeginWorkload();
        using var handler = ledger.BeginControl();
        var entered = Gate();
        var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sequence = new SupervisionControlSequence(ledger, _ =>
        {
            Interlocked.Increment(ref calls); entered.SetResult(); return actual.Task;
        });
        var stop = sequence.StopAsync(default);
        var waitJoin = sequence.JoinStartedStopAsync();
        try
        {
            await entered.Task.WaitAsync(Guard);
            Assert.False(waitJoin.IsCompleted); Reject(() => sequence.ClaimWait());
            actual.SetResult(Joined());
            Assert.False(waitJoin.IsCompleted); Assert.Equal(1, ledger.ActiveWorkloads);
            Assert.Equal(1, ledger.ActiveControls);
        }
        finally
        {
            actual.TrySetResult(Joined()); workload.Complete();
            await stop.WaitAsync(Guard); await waitJoin.WaitAsync(Guard);
        }
        Assert.Equal(1, calls); Assert.True(sequence.ClaimWait().Positive);
        Assert.Equal(1, ledger.ActiveControls); Assert.False(ledger.IsControlAdmissionClosed);
    }

    [Fact]
    public async Task WaitWithoutRegisteredStopRejectsWithoutDispatchOrClosingWork()
    {
        var ledger = new SupervisionWorkRegistry(); var calls = 0;
        var sequence = new SupervisionControlSequence(ledger, _ =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Joined()); });
        await RejectAsync(sequence.JoinStartedStopAsync());
        Assert.Equal(0, calls); Assert.False(sequence.IsWorkAdmissionClosed); Assert.False(sequence.Failed);
        var workload = ledger.BeginWorkload(); workload.Complete();
        Reject(() => sequence.ClaimWait());
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitJoinObservesFailedOrCancelIgnoringStopOnlyAfterOwnedWorkSettles(bool cancelled)
    {
        var ledger = new SupervisionWorkRegistry(); var workload = ledger.BeginWorkload();
        var entered = Gate();
        var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var sequence = new SupervisionControlSequence(ledger, token =>
        {
            Assert.Equal(cancellation.Token, token); entered.SetResult(); return actual.Task;
        });
        var stop = sequence.StopAsync(cancellation.Token);
        var waitJoin = sequence.JoinStartedStopAsync();
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (cancelled) cancellation.Cancel();
            Assert.False(waitJoin.IsCompleted);
            if (cancelled) actual.SetResult(Joined());
            else actual.SetException(new IOException("private-canary"));
            Assert.False(waitJoin.IsCompleted); Assert.Equal(1, ledger.ActiveWorkloads);
            Reject(() => sequence.ClaimWait());
        }
        finally
        {
            if (cancelled) { cancellation.Cancel(); actual.TrySetResult(Joined()); }
            else actual.TrySetException(new IOException("private-canary"));
            workload.Complete(); await waitJoin.WaitAsync(Guard); await RejectAsync(stop);
        }
        Assert.True(sequence.Failed); Assert.True(sequence.IsWorkAdmissionClosed);
        Assert.Same(stop, sequence.StopAsync(default));
        var wait = sequence.ClaimWait(); Assert.False(wait.Positive);
        sequence.CompleteWrite(wait, true);
        Assert.False(sequence.PositiveWaitAcknowledged); Reject(() => sequence.ClaimExit());
        Reject(() => sequence.ClaimWait());
    }

    [Fact]
    public async Task ConcurrentWaitJoinersShareStopButOnlyOneReplyClaimWins()
    {
        var entered = Gate();
        var actual = new TaskCompletionSource<SupervisionControlJoinFacts>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sequence = new SupervisionControlSequence(new(), _ =>
        { Interlocked.Increment(ref calls); entered.SetResult(); return actual.Task; });
        var stop = sequence.StopAsync(default);
        var joiners = Enumerable.Range(0, 8).Select(_ => sequence.JoinStartedStopAsync()).ToArray();
        try
        {
            await entered.Task.WaitAsync(Guard);
            Assert.All(joiners, join => Assert.False(join.IsCompleted));
        }
        finally
        {
            actual.TrySetResult(Joined());
            await Task.WhenAll(joiners.Append(stop)).WaitAsync(Guard);
        }
        Assert.Equal(1, calls); Assert.Same(stop, sequence.StopAsync(default));
        var winners = new ConcurrentBag<SupervisionControlSequence.Reply>();
        var claims = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { winners.Add(sequence.ClaimWait()); }
            catch (EvidenceAdmissionException error) { AssertClosed(error); }
        })).ToArray();
        await Task.WhenAll(claims).WaitAsync(Guard);
        var wait = Assert.Single(winners); Assert.True(wait.Positive);
        Assert.False(sequence.PositiveWaitAcknowledged);
        sequence.CompleteWrite(wait, true); Assert.True(sequence.PositiveWaitAcknowledged);
    }

    [Fact]
    public async Task StopCallbackCannotJoinItsOwnRegisteredStop()
    {
        var entered = Gate(); var finish = Gate();
        SupervisionControlSequence? sequence = null;
        sequence = new(new(), async _ =>
        {
            entered.SetResult(); await finish.Task;
            await RejectAsync(sequence!.JoinStartedStopAsync());
            return Joined();
        });
        var stop = sequence.StopAsync(default);
        var waitJoin = sequence.JoinStartedStopAsync();
        try { await entered.Task.WaitAsync(Guard); Assert.False(waitJoin.IsCompleted); }
        finally
        {
            finish.TrySetResult(); await stop.WaitAsync(Guard); await waitJoin.WaitAsync(Guard);
        }
        Assert.False(sequence.Failed); Assert.True(sequence.ClaimWait().Positive);
    }

    [Fact]
    public void MissingActualOperationsCannotConstructSequence()
    {
        Assert.Throws<ArgumentNullException>(() => new SupervisionControlSequence(null!, _ => Task.FromResult(Joined())));
        Assert.Throws<ArgumentNullException>(() => new SupervisionControlSequence(new(), null!));
    }

    private static SupervisionControlSequence Create() => new(new(), _ => Task.FromResult(Joined()));

    private static void Reject(Action operation) => AssertClosed(Assert.Throws<EvidenceAdmissionException>(operation));
    private static async Task RejectAsync(Task operation) => AssertClosed(
        await Assert.ThrowsAsync<EvidenceAdmissionException>(async () => await operation.WaitAsync(Guard)));
    private static void AssertClosed(EvidenceAdmissionException error)
    {
        Assert.Equal("ASEVD410", error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }
}
