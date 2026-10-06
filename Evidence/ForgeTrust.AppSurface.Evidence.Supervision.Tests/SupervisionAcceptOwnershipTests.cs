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

    private static void RejectSelf(SupervisionAcceptOwnership<Item> owner, Item item)
    {
        Reject(() => owner.AcceptAsync(default));
        Reject(() => owner.ReleaseAsync(item));
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

    private sealed class Item
    {
        public override bool Equals(object? obj) => throw new IOException("private-canary-equality");
        public override int GetHashCode() => throw new IOException("private-canary-hash");
    }
}
