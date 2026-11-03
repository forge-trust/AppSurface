using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable actual-task/barrier ownership controls; no socket, peer identity or native admission is issued.</summary>
public sealed class SupervisionAcceptOwnershipTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SuccessfulResultIsRetainedBeforePublicationAndReleaseIsSharedExactlyOnce()
    {
        var item = new Item();
        var closed = 0;
        await using var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(item), () => { },
            _ => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; });
        Assert.Same(item, await owner.AcceptAsync(default).WaitAsync(Guard));
        var release = owner.ReleaseAsync(item);
        Assert.Same(release, owner.ReleaseAsync(item));
        await release.WaitAsync(Guard);
        Assert.Same(release, owner.ReleaseAsync(item));
        Assert.Equal(1, closed);
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task PendingAcceptIsRegisteredBeforeSynchronousDelegateReentry()
    {
        var item = new Item();
        var calls = 0;
        SupervisionAcceptOwnership<Item>? owner = null;
        owner = new(_ =>
        {
            Interlocked.Increment(ref calls);
            Reject(() => owner!.AcceptAsync(default));
            return Task.FromResult(item);
        }, () => { }, _ => ValueTask.CompletedTask);
        try { Assert.Same(item, await owner.AcceptAsync(default).WaitAsync(Guard)); }
        finally { await owner.DisposeAsync().AsTask().WaitAsync(Guard); }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentAcceptContendersCannotDispatchWhileOriginalTaskIsPending()
    {
        var entered = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        {
            Interlocked.Increment(ref calls); entered.SetResult(); return actual.Task;
        }, () => { }, _ => ValueTask.CompletedTask);
        var pending = owner.AcceptAsync(default);
        try
        {
            await entered.Task.WaitAsync(Guard);
            var contenders = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                Reject(() => owner.AcceptAsync(default)))).ToArray();
            await Task.WhenAll(contenders).WaitAsync(Guard);
            Assert.Equal(1, calls);
        }
        finally
        {
            actual.TrySetResult(new());
            await pending.WaitAsync(Guard);
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CancelIgnoringAcceptReturnsLateResourceOnlyToItsJoinedCloser()
    {
        var entered = Gate();
        var listenerClosed = Gate();
        var closeEntered = Gate();
        var closeRelease = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new Item();
        var closes = 0;
        using var stop = new CancellationTokenSource();
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => listenerClosed.SetResult(), async value =>
            {
                Assert.Same(item, value); Interlocked.Increment(ref closes);
                closeEntered.SetResult(); await closeRelease.Task;
            });
        var accept = owner.AcceptAsync(stop.Token);
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            stop.Cancel();
            await listenerClosed.Task.WaitAsync(Guard);
            disposal = owner.DisposeAsync().AsTask();
            Assert.True(owner.IsClosed);
            Assert.False(accept.IsCompleted);
            Assert.False(disposal.IsCompleted);
            actual.SetResult(item);
            await closeEntered.Task.WaitAsync(Guard);
            Assert.False(accept.IsCompleted);
            Assert.False(disposal.IsCompleted);
            Reject(() => owner.AcceptAsync(default));
        }
        finally
        {
            actual.TrySetResult(item); closeRelease.TrySetResult();
            await RejectAsync(accept);
            await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        Assert.Equal(1, closes);
        Assert.False(owner.Failed); // Joined cancellation is shutdown, not a fabricated native success.
    }

    [Fact]
    public async Task DisposeClosesListenerBeforeJoiningIgnoredAcceptAndSharesActualCompletion()
    {
        var entered = Gate();
        var listenerClosed = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => listenerClosed.SetResult(), _ => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; });
        var accept = owner.AcceptAsync(default);
        Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            dispose = owner.DisposeAsync().AsTask();
            Assert.Same(dispose, owner.DisposeAsync().AsTask());
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(dispose.IsCompleted);
            Assert.False(accept.IsCompleted);
        }
        finally
        {
            actual.TrySetResult(new());
            await RejectAsync(accept);
            await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ConcurrentReleaseAndDisposeJoinTheSameCloseWithoutRetry()
    {
        var entered = Gate(); var finish = Gate();
        var item = new Item(); var calls = 0; var listenerCloses = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(item),
            () => Interlocked.Increment(ref listenerCloses), async _ =>
            { Interlocked.Increment(ref calls); entered.SetResult(); await finish.Task; });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var release = owner.ReleaseAsync(item);
        Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            dispose = owner.DisposeAsync().AsTask();
            Assert.Same(release, owner.ReleaseAsync(item));
            Assert.False(release.IsCompleted);
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
            await release.WaitAsync(Guard);
            await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        Assert.Equal(1, calls);
        Assert.Equal(1, listenerCloses);
    }

    [Fact]
    public async Task FailedCloseIsStickyAndDisposeStillAttemptsEveryOtherRetainedResult()
    {
        var first = new Item(); var second = new Item(); var next = 0;
        var firstCloses = 0; var secondCloses = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(next++ == 0 ? first : second),
            () => { }, value =>
            {
                if (ReferenceEquals(value, first)) { Interlocked.Increment(ref firstCloses); throw new IOException("private-canary"); }
                Interlocked.Increment(ref secondCloses); return ValueTask.CompletedTask;
            });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var failed = owner.ReleaseAsync(first);
        await RejectAsync(failed);
        Assert.True(owner.Failed); Assert.True(owner.IsClosed);
        Assert.Same(failed, owner.ReleaseAsync(first));
        Reject(() => owner.AcceptAsync(default));
        var dispose = owner.DisposeAsync().AsTask();
        await RejectAsync(dispose);
        Assert.Same(dispose, owner.DisposeAsync().AsTask());
        Assert.Equal(1, firstCloses); Assert.Equal(1, secondCloses);
    }

    [Fact]
    public async Task ListenerCloseFailureDoesNotAbandonPendingAcceptOrLateResult()
    {
        var entered = Gate(); var listenerAttempted = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerCalls = 0; var resultCalls = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => { Interlocked.Increment(ref listenerCalls); listenerAttempted.SetResult(); throw new IOException("private-canary"); },
            _ => { Interlocked.Increment(ref resultCalls); return ValueTask.CompletedTask; });
        var accept = owner.AcceptAsync(default);
        Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            dispose = owner.DisposeAsync().AsTask();
            await listenerAttempted.Task.WaitAsync(Guard);
            Assert.False(dispose.IsCompleted); Assert.False(accept.IsCompleted);
        }
        finally
        {
            actual.TrySetResult(new());
            await RejectAsync(accept);
            await RejectAsync(dispose ?? owner.DisposeAsync().AsTask());
        }
        Assert.Equal(1, listenerCalls); Assert.Equal(1, resultCalls); Assert.True(owner.Failed);
    }

    [Fact]
    public async Task SynchronousAcceptFailureClosesAdmissionAndNeverEchoesOriginalException()
    {
        var listenerCloses = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => throw new IOException("private-canary"),
            () => Interlocked.Increment(ref listenerCloses), _ => ValueTask.CompletedTask);
        await RejectAsync(owner.AcceptAsync(default));
        Assert.True(owner.IsClosed); Assert.True(owner.Failed);
        Reject(() => owner.AcceptAsync(default));
        await RejectAsync(owner.DisposeAsync().AsTask());
        Assert.Equal(1, listenerCloses);
    }

    [Fact]
    public async Task AlreadyCancelledAttemptClosesListenerWithoutDispatch()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        var dispatches = 0; var listenerCloses = 0;
        await using var owner = new SupervisionAcceptOwnership<Item>(_ =>
        { Interlocked.Increment(ref dispatches); return Task.FromResult(new Item()); },
            () => Interlocked.Increment(ref listenerCloses), _ => ValueTask.CompletedTask);
        await RejectAsync(owner.AcceptAsync(token.Token));
        Assert.Equal(0, dispatches); Assert.Equal(1, listenerCloses); Assert.True(owner.IsClosed);
    }

    [Fact]
    public async Task CapacityBoundsLiveResultsAndJoinedReleaseAllowsFurtherSequentialAccepts()
    {
        var dispatches = 0; var closes = 0;
        await using var owner = new SupervisionAcceptOwnership<Item>(_ =>
        { Interlocked.Increment(ref dispatches); return Task.FromResult(new Item()); }, () => { }, _ =>
        { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
        var items = new List<Item>();
        for (var i = 0; i < SupervisionAcceptOwnership<Item>.MaximumLiveResults; i++)
            items.Add(await owner.AcceptAsync(default).WaitAsync(Guard));
        Reject(() => owner.AcceptAsync(default)); Assert.Equal(32, dispatches);
        var release = owner.ReleaseAsync(items[0]); await release.WaitAsync(Guard);
        items.Add(await owner.AcceptAsync(default).WaitAsync(Guard));
        Assert.Same(release, owner.ReleaseAsync(items[0]));
        await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        Assert.Equal(33, dispatches); Assert.Equal(33, closes); Assert.False(owner.Failed);
    }

    [Fact]
    public async Task UnknownNullAndEquivalentResourcesNeverDispatchClose()
    {
        var item = new Item(); var closes = 0;
        await using var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(item), () => { },
            _ => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        Reject(() => owner.ReleaseAsync(null!)); Reject(() => owner.ReleaseAsync(new Item()));
        Assert.Equal(0, closes);
        await owner.ReleaseAsync(item).WaitAsync(Guard);
        await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        Reject(() => owner.ReleaseAsync(new Item())); Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullActualTaskOrResultFailsClosed(bool nullTask)
    {
        var owner = new SupervisionAcceptOwnership<Item>(_ => nullTask ? null! : Task.FromResult<Item>(null!),
            () => { }, _ => ValueTask.CompletedTask);
        await RejectAsync(owner.AcceptAsync(default));
        Assert.True(owner.Failed); Assert.True(owner.IsClosed);
        await RejectAsync(owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task RepeatedActualResourceRejectsAndClosesItsExistingOwnershipExactlyOnce()
    {
        var item = new Item(); var closes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(item), () => { },
            _ => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        await RejectAsync(owner.AcceptAsync(default));
        Assert.Equal(1, closes); Assert.True(owner.Failed);
        await RejectAsync(owner.DisposeAsync().AsTask());
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task EmptyOwnerDisposalIsSharedAndClosesListenerOnlyOnce()
    {
        var closes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(new Item()),
            () => Interlocked.Increment(ref closes), _ => ValueTask.CompletedTask);
        var first = owner.DisposeAsync().AsTask(); Assert.Same(first, owner.DisposeAsync().AsTask());
        await first.WaitAsync(Guard);
        Assert.True(owner.IsClosed); Assert.False(owner.Failed); Assert.Equal(1, closes);
        Reject(() => owner.AcceptAsync(default)); Reject(() => owner.ReleaseAsync(new Item()));
    }

    [Fact]
    public async Task DisposeDispatchesEveryCloseBeforeWaitingAndCannotDetachTheLastOne()
    {
        var first = new Item(); var second = new Item(); var next = 0;
        var firstEntered = Gate(); var secondEntered = Gate(); var finish = Gate();
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(next++ == 0 ? first : second),
            () => { }, async value =>
            {
                if (ReferenceEquals(value, first))
                {
                    firstEntered.SetResult(); await finish.Task; throw new IOException("private-canary");
                }
                secondEntered.SetResult(); await finish.Task;
            });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var disposal = owner.DisposeAsync().AsTask();
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(Guard);
            Assert.False(disposal.IsCompleted);
        }
        finally { finish.TrySetResult(); await RejectAsync(disposal); }
        Assert.True(owner.Failed);
    }

    [Fact]
    public async Task OriginalFaultedAcceptIsObservedAndSanitizedBeforeItsCallerCompletes()
    {
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Gate(); var listenerCloses = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => Interlocked.Increment(ref listenerCloses), _ => ValueTask.CompletedTask);
        var accept = owner.AcceptAsync(default);
        try { await entered.Task.WaitAsync(Guard); Assert.False(accept.IsCompleted); }
        finally
        {
            actual.TrySetException(new IOException("private-canary"));
            await RejectAsync(accept);
            await RejectAsync(owner.DisposeAsync().AsTask());
        }
        Assert.True(owner.Failed); Assert.Equal(1, listenerCloses);
    }

    [Fact]
    public void MissingActualOperationsCannotConstructBookkeeping()
    {
        Assert.Throws<ArgumentNullException>(() => new SupervisionAcceptOwnership<Item>(null!, () => { },
            _ => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new SupervisionAcceptOwnership<Item>(
            _ => Task.FromResult(new Item()), null!, _ => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new SupervisionAcceptOwnership<Item>(
            _ => Task.FromResult(new Item()), () => { }, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncActualCallbackRejectsSelfJoinAfterAwaitWhileExternalOwnerStillJoins(bool duringClose)
    {
        var item = new Item(); var entered = Gate(); var finish = Gate(); var closes = 0;
        SupervisionAcceptOwnership<Item>? owner = null;
        owner = new(async _ =>
        {
            if (!duringClose)
            {
                entered.SetResult(); await finish.Task;
                RejectSelf(owner!, item);
            }
            return item;
        }, () => { }, async _ =>
        {
            Interlocked.Increment(ref closes);
            if (duringClose)
            {
                entered.SetResult(); await finish.Task;
                RejectSelf(owner!, item);
            }
        });
        var accept = owner.AcceptAsync(default);
        Task joined = accept;
        Task? disposal = null;
        try
        {
            if (duringClose)
            {
                await accept.WaitAsync(Guard);
                joined = owner.ReleaseAsync(item);
            }
            await entered.Task.WaitAsync(Guard);
            Assert.False(joined.IsCompleted);
            if (duringClose)
            {
                disposal = owner.DisposeAsync().AsTask();
                Assert.False(disposal.IsCompleted);
            }
        }
        finally
        {
            finish.TrySetResult();
            await joined.WaitAsync(Guard);
            await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        Assert.Equal(1, closes); Assert.False(owner.Failed);
    }

    [Fact]
    public async Task ActualListenerCloseRejectsSelfDisposalBeforeReturningSharedTask()
    {
        var item = new Item(); var closes = 0;
        SupervisionAcceptOwnership<Item>? owner = null;
        owner = new(_ => Task.FromResult(item), () =>
        {
            Interlocked.Increment(ref closes);
            RejectSelf(owner!, item);
        }, _ => ValueTask.CompletedTask);
        await owner.AcceptAsync(default).WaitAsync(Guard);
        await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        Assert.Equal(1, closes); Assert.False(owner.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalIoFaultAfterListenerCloseStillFailsSharedDisposal(bool cancel)
    {
        var entered = Gate(); var listenerClosed = Gate(); var closes = 0;
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => { Interlocked.Increment(ref closes); listenerClosed.SetResult(); }, _ => ValueTask.CompletedTask);
        var accept = owner.AcceptAsync(stop.Token);
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (cancel) stop.Cancel();
            else disposal = owner.DisposeAsync().AsTask();
            await listenerClosed.Task.WaitAsync(Guard);
            disposal ??= owner.DisposeAsync().AsTask();
            Assert.True(owner.IsClosed); Assert.False(owner.Failed);
            Assert.False(accept.IsCompleted); Assert.False(disposal.IsCompleted);
        }
        finally
        {
            actual.TrySetException(new IOException("private-canary"));
            await RejectAsync(accept);
            disposal ??= owner.DisposeAsync().AsTask();
            await RejectAsync(disposal);
        }
        Assert.True(owner.Failed); Assert.Equal(1, closes);
        Assert.Same(disposal, owner.DisposeAsync().AsTask());
        await RejectAsync(owner.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntentionalShutdownMarkerAfterClosureRejectsAcceptWithoutMarkingFailure(bool cancel)
    {
        var entered = Gate(); var listenerClosed = Gate(); var closes = 0;
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => { Interlocked.Increment(ref closes); listenerClosed.SetResult(); }, _ => ValueTask.CompletedTask);
        var accept = owner.AcceptAsync(stop.Token);
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (cancel) stop.Cancel();
            else disposal = owner.DisposeAsync().AsTask();
            await listenerClosed.Task.WaitAsync(Guard);
            disposal ??= owner.DisposeAsync().AsTask();
            Assert.False(accept.IsCompleted); Assert.False(disposal.IsCompleted);
        }
        finally
        {
            actual.TrySetException(new SupervisionAcceptShutdownException());
            await RejectAsync(accept);
            await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        Assert.True(owner.IsClosed); Assert.False(owner.Failed); Assert.Equal(1, closes);
    }

    [Fact]
    public async Task PrematureShutdownMarkerIsAnUnexpectedFailureWithoutInnerException()
    {
        var marker = new SupervisionAcceptShutdownException();
        Assert.Null(marker.InnerException);
        var owner = new SupervisionAcceptOwnership<Item>(_ => throw marker, () => { },
            _ => ValueTask.CompletedTask);
        await RejectAsync(owner.AcceptAsync(default));
        Assert.True(owner.IsClosed); Assert.True(owner.Failed);
        await RejectAsync(owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task AdmissionClosePreservesPublishedConnectionAndSharesOriginalCompletion()
    {
        var item = new Item(); var listenerCloses = 0; var resultCloses = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(item),
            () => Interlocked.Increment(ref listenerCloses), _ =>
            { Interlocked.Increment(ref resultCloses); return ValueTask.CompletedTask; });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var close = owner.CloseAdmissionAsync();
        Assert.Same(close, owner.CloseAdmissionAsync());
        var contenders = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => new TaskHolder(owner.CloseAdmissionAsync())))).WaitAsync(Guard);
        Assert.All(contenders, row => Assert.Same(close, row.Task));
        await close.WaitAsync(Guard);
        Assert.True(owner.IsClosed); Assert.False(owner.Failed);
        Assert.Equal(1, listenerCloses); Assert.Equal(0, resultCloses);
        Reject(() => owner.AcceptAsync(default));
        await owner.ReleaseAsync(item).WaitAsync(Guard);
        await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        Assert.Equal(1, resultCloses);
    }

    [Fact]
    public async Task ExitAckWaitsForIgnoredAcceptAndLateCloseWhileExitConnectionRemainsOwned()
    {
        var exit = new Item(); var late = new Item(); var calls = 0; var exitCloses = 0;
        var entered = Gate(); var listenerClosed = Gate(); var lateEntered = Gate(); var lateFinish = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult(exit);
            entered.SetResult(); return actual.Task;
        }, () => listenerClosed.SetResult(), async item =>
        {
            if (ReferenceEquals(item, late)) { lateEntered.SetResult(); await lateFinish.Task; }
            else { Assert.Same(exit, item); Interlocked.Increment(ref exitCloses); }
        });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var pending = owner.AcceptAsync(default);
        var sequence = await ExitReadySequenceAsync();
        var claim = sequence.ClaimExit(); var writes = 0;
        Task? ack = null;
        async Task WriteAfterAdmissionAsync()
        {
            await owner.CloseAdmissionAsync();
            Assert.Equal(0, exitCloses);
            Interlocked.Increment(ref writes);
            await owner.ReleaseAsync(exit);
            sequence.CompleteWrite(claim, true);
        }
        try
        {
            await entered.Task.WaitAsync(Guard);
            ack = WriteAfterAdmissionAsync();
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(ack.IsCompleted); Assert.Equal(0, writes); Assert.False(sequence.ExitAcknowledged);
            actual.SetResult(late);
            await lateEntered.Task.WaitAsync(Guard);
            Assert.False(ack.IsCompleted); Assert.Equal(0, writes); Assert.Equal(0, exitCloses);
        }
        finally
        {
            _ = owner.CloseAdmissionAsync();
            actual.TrySetResult(late); lateFinish.TrySetResult();
            await RejectAsync(pending);
            if (ack is not null) await ack.WaitAsync(Guard);
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
        Assert.Equal(1, writes); Assert.Equal(1, exitCloses); Assert.True(sequence.ExitAcknowledged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionCloseFaultCannotReachAckAndRemainsSticky(bool listenerFault)
    {
        var entered = Gate(); var closed = Gate(); var writes = 0;
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => { closed.SetResult(); if (listenerFault) throw new IOException("private-canary"); },
            _ => ValueTask.CompletedTask);
        var pending = owner.AcceptAsync(default);
        async Task AckAsync() { await owner.CloseAdmissionAsync(); Interlocked.Increment(ref writes); }
        Task? ack = null;
        try
        {
            await entered.Task.WaitAsync(Guard); ack = AckAsync();
            await closed.Task.WaitAsync(Guard); Assert.False(ack.IsCompleted);
        }
        finally
        {
            if (listenerFault) actual.TrySetException(new SupervisionAcceptShutdownException());
            else actual.TrySetException(new IOException("private-canary"));
            await RejectAsync(pending);
            if (ack is not null) await RejectAsync(ack);
            await RejectAsync(owner.DisposeAsync().AsTask());
        }
        Assert.Equal(0, writes); Assert.True(owner.Failed);
        await RejectAsync(owner.CloseAdmissionAsync());
    }

    [Fact]
    public async Task AdmissionLateCloseFailurePreventsAckButFullDisposeClosesPublishedConnection()
    {
        var current = new Item(); var late = new Item(); var calls = 0; var currentCloses = 0;
        var entered = Gate(); var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        { if (++calls == 1) return Task.FromResult(current); entered.SetResult(); return actual.Task; },
            () => { }, item =>
            {
                if (ReferenceEquals(item, late)) throw new IOException("private-canary");
                Interlocked.Increment(ref currentCloses); return ValueTask.CompletedTask;
            });
        await owner.AcceptAsync(default).WaitAsync(Guard);
        var pending = owner.AcceptAsync(default); await entered.Task.WaitAsync(Guard);
        var admission = owner.CloseAdmissionAsync(); actual.SetResult(late);
        await RejectAsync(pending); await RejectAsync(admission);
        Assert.Equal(0, currentCloses); Assert.True(owner.Failed);
        await RejectAsync(owner.DisposeAsync().AsTask());
        Assert.Equal(1, currentCloses);
    }

    [Fact]
    public async Task FailedExitWriteAfterAdmissionJoinCannotCommitSuccessfulExit()
    {
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(new Item()), () => { },
            _ => ValueTask.CompletedTask);
        var sequence = await ExitReadySequenceAsync(); var exit = sequence.ClaimExit();
        await owner.CloseAdmissionAsync().WaitAsync(Guard);
        Reject(() => sequence.CompleteWrite(exit, false));
        Assert.False(sequence.ExitAcknowledged); Assert.True(sequence.Failed);
        await owner.DisposeAsync().AsTask().WaitAsync(Guard);
    }

    [Fact]
    public async Task AdmissionAndFullDisposeBothJoinTheSameBlockedOriginalAccept()
    {
        var entered = Gate(); var closed = Gate(); var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SupervisionAcceptOwnership<Item>(_ => { entered.SetResult(); return actual.Task; },
            () => closed.SetResult(), _ => ValueTask.CompletedTask);
        var pending = owner.AcceptAsync(default); await entered.Task.WaitAsync(Guard);
        var admission = owner.CloseAdmissionAsync(); var disposal = owner.DisposeAsync().AsTask();
        try
        {
            await closed.Task.WaitAsync(Guard);
            Assert.Same(admission, owner.CloseAdmissionAsync());
            Assert.False(admission.IsCompleted); Assert.False(disposal.IsCompleted);
        }
        finally
        {
            actual.TrySetException(new SupervisionAcceptShutdownException());
            await RejectAsync(pending); await admission.WaitAsync(Guard); await disposal.WaitAsync(Guard);
        }
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task AdmittedPrecheckFaultDuringClosureRemainsFailureBeforeAnyAck()
    {
        var entered = Gate(); var release = Gate(); var listenerClosed = Gate(); var writes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(async _ =>
        {
            entered.SetResult(); await release.Task;
            // An unexpected actual-procedure fault, not a fabricated live process or native check.
            throw LinuxProcessIdentity.Rejected();
        }, () => listenerClosed.SetResult(), _ => ValueTask.CompletedTask);
        var pending = owner.AcceptAsync(default);
        async Task AckAsync() { await owner.CloseAdmissionAsync(); Interlocked.Increment(ref writes); }
        Task? ack = null;
        try
        {
            await entered.Task.WaitAsync(Guard); ack = AckAsync();
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(ack.IsCompleted); Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult(); await RejectAsync(pending);
            if (ack is not null) await RejectAsync(ack);
            await RejectAsync(owner.DisposeAsync().AsTask());
        }
        Assert.Equal(0, writes); Assert.True(owner.Failed);
    }

    [Fact]
    public async Task ClosingAdmissionJoinsReservedAcceptBeforeItsCallbackCanDispatch()
    {
        var gate = new object(); var calls = 0; var closes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        { Interlocked.Increment(ref calls); return Task.FromResult(new Item()); },
            () => Interlocked.Increment(ref closes), _ => ValueTask.CompletedTask);
        SupervisionAcceptOwnership<Item>.RegisteredAccept accept;
        SupervisionAcceptOwnership<Item>.RegisteredAdmissionClose close;
        lock (gate)
        {
            accept = owner.RegisterAccept(default);
            close = owner.RegisterAdmissionClose();
            Assert.Equal(0, calls); Assert.Equal(0, closes);
            Assert.False(accept.Task.IsCompleted); Assert.False(close.Task.IsCompleted);
        }
        close.Dispatch();
        try
        {
            Assert.False(close.Task.IsCompleted); // Closure retains the original not-yet-dispatched accept.
            Assert.Equal(0, calls);
        }
        finally
        {
            accept.Dispatch();
            await RejectAsync(accept.Task);
            await close.Task.WaitAsync(Guard);
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
        Assert.Equal(0, calls); Assert.Equal(1, closes); Assert.False(owner.Failed);
        Reject(accept.Dispatch); Reject(close.Dispatch);
    }

    [Fact]
    public async Task ReservedNativeProcedureCallbacksRunAfterEnclosingGateRelease()
    {
        var gate = new object(); var entered = Gate(); var closed = Gate();
        var actual = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        void RequireUnlocked()
        {
            Assert.False(Monitor.IsEntered(gate));
            var acquired = Monitor.TryEnter(gate);
            try { Assert.True(acquired); }
            finally { if (acquired) Monitor.Exit(gate); }
        }
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        { RequireUnlocked(); entered.SetResult(); return actual.Task; },
            () => { RequireUnlocked(); closed.SetResult(); }, _ => ValueTask.CompletedTask);
        SupervisionAcceptOwnership<Item>.RegisteredAccept accept;
        lock (gate)
        {
            accept = owner.RegisterAccept(default);
            Assert.False(entered.Task.IsCompleted);
        }
        accept.Dispatch();
        SupervisionAcceptOwnership<Item>.RegisteredAdmissionClose? close = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            lock (gate)
            {
                close = owner.RegisterAdmissionClose();
                Assert.False(closed.Task.IsCompleted);
            }
            close.Dispatch();
            await closed.Task.WaitAsync(Guard);
            Assert.False(close.Task.IsCompleted);
        }
        finally
        {
            actual.TrySetException(new SupervisionAcceptShutdownException());
            await RejectAsync(accept.Task);
            if (close is not null) await close.Task.WaitAsync(Guard);
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task RepeatedClosureReservationsShareJoinWithoutDetachingReservedAccept()
    {
        var closes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ => Task.FromResult(new Item()),
            () => Interlocked.Increment(ref closes), _ => ValueTask.CompletedTask);
        var accept = owner.RegisterAccept(default);
        var first = owner.RegisterAdmissionClose(); var second = owner.RegisterAdmissionClose();
        Assert.Same(first.Task, second.Task);
        second.Dispatch(); // Dispatching this redundant gate cannot release the original reservation.
        Assert.False(first.Task.IsCompleted); Assert.Equal(0, closes);
        first.Dispatch();
        var disposal = owner.DisposeAsync().AsTask();
        try { Assert.False(first.Task.IsCompleted); Assert.False(disposal.IsCompleted); }
        finally
        {
            accept.Dispatch(); await RejectAsync(accept.Task);
            await first.Task.WaitAsync(Guard); await disposal.WaitAsync(Guard);
        }
        Assert.Equal(1, closes); Assert.False(owner.Failed);
    }

    // Only detached procedure join data, not Linux identity, kernel exit, lease or proof.
    private static async Task<SupervisionControlSequence> ExitReadySequenceAsync()
    {
        var ledger = new SupervisionWorkRegistry();
        var sequence = new SupervisionControlSequence(ledger, _ =>
            Task.FromResult(new SupervisionControlJoinFacts(true, true, true)));
        sequence.CompleteWrite(sequence.ClaimReady(), true);
        await sequence.StopAsync(default).WaitAsync(Guard);
        sequence.CompleteWrite(sequence.ClaimWait(), true);
        return sequence;
    }

    private static void RejectSelf(SupervisionAcceptOwnership<Item> owner, Item item)
    {
        Reject(() => owner.AcceptAsync(default));
        Reject(() => owner.RegisterAccept(default));
        Reject(() => owner.ReleaseAsync(item));
        Reject(() => owner.CloseAdmissionAsync());
        Reject(() => owner.RegisterAdmissionClose());
        Reject(() => { _ = owner.DisposeAsync(); });
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }

    private static async Task RejectAsync(Task task)
    {
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () => await task.WaitAsync(Guard));
        Assert.Equal("ASEVD410", error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }

    private sealed record TaskHolder(Task Task);

    private sealed class Item
    {
        public override bool Equals(object? obj) => throw new IOException("private-canary-equality");
        public override int GetHashCode() => throw new IOException("private-canary-hash");
    }
}
