using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-task and deterministic cancellation controls; no native listener or identity is fabricated.</summary>
public sealed class SupervisionAcceptedResourceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringValidationCannotInterruptChecksAndLateResultIsClosed(bool closeAdmission)
    {
        using var stop = new CancellationTokenSource();
        var entered = Gate();
        var release = Gate();
        var closed = Gate();
        var item = new Item();
        var checks = 0;
        var closes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(token =>
            SupervisionAcceptedResource.CompleteAsync(Task.FromResult(item), (accepted, checkToken) =>
            {
                Assert.Same(item, accepted);
                Interlocked.Increment(ref checks); // Owner/name checks before the workspace boundary.
                entered.SetResult();
                release.Task.GetAwaiter().GetResult(); // Deterministic original validation procedure.
                checkToken.ThrowIfCancellationRequested();
                Assert.False(checkToken.CanBeCanceled);
                Interlocked.Increment(ref checks); // Workspace/worker/peer checks after cancellation.
                return accepted;
            }, _ => throw new InvalidOperationException("Successful validation must transfer to its owner.")),
            () => closed.TrySetResult(), _ => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
        var pending = owner.AcceptAsync(stop.Token);
        Task? admission = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (closeAdmission) admission = owner.CloseAdmissionAsync();
            stop.Cancel();
            await closed.Task.WaitAsync(Guard);
            Assert.False(pending.IsCompleted);
            if (admission is not null) Assert.False(admission.IsCompleted);
            release.TrySetResult();
            await RejectAsync(pending);
            if (admission is not null) await admission.WaitAsync(Guard);
            Assert.Equal(2, checks);
            Assert.Equal(1, closes);
            Assert.False(owner.Failed);
        }
        finally
        {
            release.TrySetResult();
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task ExitWriteWaitsForValidationJoinWithoutClosingThePublishedExitConnection()
    {
        var exitConnection = new Item();
        var late = new Item();
        var entered = Gate(); var release = Gate(); var listenerClosed = Gate();
        var checks = 0; var calls = 0; var lateCloses = 0; var exitCloses = 0; var writes = 0;
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult(exitConnection);
            return SupervisionAcceptedResource.CompleteAsync(Task.FromResult(late), (accepted, token) =>
            {
                entered.SetResult(); release.Task.GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested(); Interlocked.Increment(ref checks); return accepted;
            }, _ => throw new InvalidOperationException());
        }, () => listenerClosed.TrySetResult(), item =>
        {
            if (ReferenceEquals(item, late)) Interlocked.Increment(ref lateCloses);
            else Interlocked.Increment(ref exitCloses);
            return ValueTask.CompletedTask;
        });
        Assert.Same(exitConnection, await owner.AcceptAsync(default).WaitAsync(Guard));
        var pending = owner.AcceptAsync(default);
        Task? ack = null;
        async Task WriteAsync()
        {
            await owner.CloseAdmissionAsync();
            Assert.Equal(0, exitCloses);
            Interlocked.Increment(ref writes);
            await owner.ReleaseAsync(exitConnection);
        }
        try
        {
            await entered.Task.WaitAsync(Guard);
            ack = WriteAsync();
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(ack.IsCompleted); Assert.Equal(0, writes);
            release.TrySetResult();
            await RejectAsync(pending);
            await ack.WaitAsync(Guard);
            Assert.Equal(1, checks); Assert.Equal(1, writes);
            Assert.Equal(1, lateCloses); Assert.Equal(1, exitCloses);
            Assert.False(owner.Failed);
        }
        finally
        {
            release.TrySetResult();
            if (ack is not null) await ack.WaitAsync(Guard);
            await owner.DisposeAsync().AsTask().WaitAsync(Guard);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityOrDeadlineRejectionStillClosesResourceAndPoisonsAdmission(bool cancelledOwner)
    {
        var entered = Gate(); var release = Gate();
        var closes = 0; var writes = 0;
        using var expired = new CancellationTokenSource(); expired.Cancel();
        var owner = new SupervisionAcceptOwnership<Item>(_ =>
            SupervisionAcceptedResource.CompleteAsync<Item, Item>(Task.FromResult(new Item()), (accepted, token) =>
            {
                Assert.False(token.CanBeCanceled);
                entered.SetResult(); release.Task.GetAwaiter().GetResult();
                if (cancelledOwner) expired.Token.ThrowIfCancellationRequested();
                throw LinuxControlListenerPolicy.Invalid();
            }, _ => Interlocked.Increment(ref closes)), () => { }, _ => ValueTask.CompletedTask);
        var pending = owner.AcceptAsync(default);
        Task? ack = null;
        async Task WriteAsync() { await owner.CloseAdmissionAsync(); Interlocked.Increment(ref writes); }
        try
        {
            await entered.Task.WaitAsync(Guard);
            ack = WriteAsync(); release.TrySetResult();
            await RejectAsync(pending); await RejectAsync(ack);
            Assert.True(owner.Failed); Assert.Equal(1, closes); Assert.Equal(0, writes);
        }
        finally
        {
            release.TrySetResult();
            await RejectAsync(owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task OriginalAcceptIsJoinedAndSuccessfulTransferDoesNotCloseResource()
    {
        var original = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0; var closes = 0; var item = new Item();
        var completion = SupervisionAcceptedResource.CompleteAsync(original.Task, (accepted, token) =>
        {
            Assert.False(token.CanBeCanceled); Interlocked.Increment(ref checks); return accepted;
        }, _ => Interlocked.Increment(ref closes));
        Assert.False(completion.IsCompleted); Assert.Equal(0, checks);
        original.SetResult(item);
        Assert.Same(item, await completion.WaitAsync(Guard));
        Assert.Equal(1, checks); Assert.Equal(0, closes);
    }

    [Fact]
    public async Task OriginalNetworkCancellationRunsNoValidationOrTransfer()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var checks = 0; var closes = 0;
        var completion = SupervisionAcceptedResource.CompleteAsync(Task.FromCanceled<Item>(stop.Token),
            (accepted, _) => { Interlocked.Increment(ref checks); return accepted; }, _ => Interlocked.Increment(ref closes));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await completion.WaitAsync(Guard));
        Assert.Equal(0, checks); Assert.Equal(0, closes);
    }

    [Fact]
    public async Task RejectedResourceCloseFailureCannotReturnSuccessfulTransfer()
    {
        var closes = 0;
        var task = SupervisionAcceptedResource.CompleteAsync<Item, Item>(Task.FromResult(new Item()),
            (_, _) => throw LinuxControlListenerPolicy.Invalid(),
            _ => { Interlocked.Increment(ref closes); throw new IOException("close-canary"); });
        await Assert.ThrowsAsync<IOException>(async () => await task.WaitAsync(Guard));
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task MissingOriginalResourceCannotReachValidationOrClose()
    {
        var checks = 0; var closes = 0;
        var task = SupervisionAcceptedResource.CompleteAsync(Task.FromResult<Item>(null!),
            (accepted, _) => { Interlocked.Increment(ref checks); return accepted; }, _ => Interlocked.Increment(ref closes));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await task.WaitAsync(Guard));
        Assert.Equal(0, checks); Assert.Equal(0, closes);
    }

    [Fact]
    public async Task EndpointInspectionAndActualCloseUseTheSameGate()
    {
        var gate = new object(); var entered = Gate(); var release = Gate(); var closeStarted = Gate();
        var closed = 0; var calls = 0;
        var inspection = Task.Run(() =>
        {
            lock (gate)
            {
                Assert.Equal(0, Volatile.Read(ref closed));
                entered.SetResult(); release.Task.GetAwaiter().GetResult();
                Assert.Equal(0, calls); Assert.Equal(0, Volatile.Read(ref closed));
            }
        });
        Task? close = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            close = Task.Run(() =>
            {
                closeStarted.SetResult();
                SupervisionAcceptedResource.CloseUnderGate(gate, ref closed, () =>
                {
                    Assert.True(Monitor.IsEntered(gate)); Interlocked.Increment(ref calls);
                });
            });
            await closeStarted.Task.WaitAsync(Guard);
            Assert.False(close.IsCompleted); Assert.Equal(0, calls);
        }
        finally
        {
            release.TrySetResult();
            await inspection.WaitAsync(Guard);
            if (close is not null) await close.WaitAsync(Guard);
        }
        Assert.Equal(1, closed); Assert.Equal(1, calls);
        SupervisionAcceptedResource.CloseUnderGate(gate, ref closed, () => throw new InvalidOperationException());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedActualCloseStaysLatchedAndCannotRetry()
    {
        var gate = new object(); var closed = 0; var calls = 0;
        Assert.Throws<IOException>(() => SupervisionAcceptedResource.CloseUnderGate(gate, ref closed, () =>
        {
            Assert.True(Monitor.IsEntered(gate)); Interlocked.Increment(ref calls); throw new IOException();
        }));
        SupervisionAcceptedResource.CloseUnderGate(gate, ref closed, () => Interlocked.Increment(ref calls));
        Assert.Equal(1, closed); Assert.Equal(1, calls);
    }

    private sealed class Item;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task RejectAsync(Task task)
    {
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () => await task.WaitAsync(Guard));
        Assert.Equal("ASEVD410", error.Code);
    }
}
