using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original task/order controls only; no native checkpoint, worker or admission is fabricated.</summary>
public sealed class LinuxN04CheckpointOrderTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task FirstAcceptIsOpenThenReservationBlocksTheNextRegistration()
    {
        var order = new LinuxN04CheckpointOrder();
        var exit = Signal();
        await order.BeforeNextAcceptAsync(exit.Task, default);
        order.Reserve();
        var next = order.BeforeNextAcceptAsync(exit.Task, default);
        Assert.False(next.IsCompleted);
        exit.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => next.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CommitCannotPrecedePreparationOrItsActualCompletion()
    {
        var order = new LinuxN04CheckpointOrder();
        order.Reserve();
        Assert.ThrowsAny<Exception>(() => order.Claim(false));
        order.Claim(true);
        Assert.ThrowsAny<Exception>(() => order.Claim(false));
        order.Complete(true);
        order.Claim(false);
        order.Complete(false);
        Assert.ThrowsAny<Exception>(() => order.Claim(false));
    }

    [Fact]
    public void PreparationReplayAndOutOfOrderCompletionReject()
    {
        var order = new LinuxN04CheckpointOrder();
        Assert.ThrowsAny<Exception>(() => order.Claim(true));
        order.Reserve();
        Assert.ThrowsAny<Exception>(() => order.Complete(true));
        order.Claim(true);
        Assert.ThrowsAny<Exception>(() => order.Claim(true));
        Assert.ThrowsAny<Exception>(() => order.Complete(false));
    }

    [Fact]
    public async Task CommittedDataNeverReopensNextAccept()
    {
        var order = new LinuxN04CheckpointOrder();
        order.Reserve(); order.Claim(true); order.Complete(true); order.Claim(false); order.Complete(false);
        var exit = Signal();
        var next = order.BeforeNextAcceptAsync(exit.Task, default);
        Assert.False(next.IsCompleted);
        order.Close();
        await Assert.ThrowsAnyAsync<Exception>(() => next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.ThrowsAny<Exception>(() => order.Claim(true));
    }

    [Fact]
    public async Task OriginalCancellationUnblocksPauseWithoutCompletingWorkerExit()
    {
        var order = new LinuxN04CheckpointOrder(); order.Reserve();
        var exit = Signal(); using var cancellation = new CancellationTokenSource();
        var next = order.BeforeNextAcceptAsync(exit.Task, cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(exit.Task.IsCompleted);
        order.Close();
        await order.JoinAsync();
    }

    [Fact]
    public async Task CloseJoinsOriginalTaskThatIgnoresCancellation()
    {
        var order = new LinuxN04CheckpointOrder(); order.Reserve(); order.Claim(true);
        var dispatch = Signal(); var entered = Signal(); var release = Signal();
        async Task Original() { await dispatch.Task; entered.SetResult(); await release.Task; }
        var original = Original(); order.Retain(original);
        Assert.False(entered.Task.IsCompleted); // Retained before dispatch, no original callback ran.
        Task? join = null;
        try
        {
            dispatch.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            order.Close(); join = order.JoinAsync();
            Assert.False(join.IsCompleted);
        }
        finally { release.TrySetResult(); order.Close(); await order.JoinAsync(); }
        await join!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(original.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OneFaultDoesNotSkipOtherOriginalJoinOrEchoItsText()
    {
        var order = new LinuxN04CheckpointOrder(); order.Reserve();
        var release = Signal();
        order.Retain(Task.FromException(new IOException("n04-private-canary")));
        order.Retain(release.Task);
        order.Close(); var joined = order.JoinAsync();
        try { Assert.False(joined.IsCompleted); }
        finally { release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => joined.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(order.Failed);
        Assert.DoesNotContain("n04-private-canary", error.ToString());
        Assert.Null(error.InnerException);
        await Assert.ThrowsAnyAsync<Exception>(() => order.JoinAsync());
    }

    [Fact]
    public async Task ClosureRejectsLateTaskRetentionAndNoJoinCanClaimOpenAdmission()
    {
        var order = new LinuxN04CheckpointOrder();
        await Assert.ThrowsAnyAsync<Exception>(() => order.JoinAsync());
        order.Close();
        Assert.ThrowsAny<Exception>(() => order.Retain(Task.CompletedTask));
        Assert.ThrowsAny<Exception>(() => order.Reserve());
        await order.JoinAsync();
    }

    [Fact]
    public void StickyFailureCannotBeClearedByLaterOrderingEvents()
    {
        var order = new LinuxN04CheckpointOrder(); order.Reserve(); order.Claim(true);
        order.MarkFailed();
        Assert.ThrowsAny<Exception>(() => order.Complete(true));
        Assert.ThrowsAny<Exception>(() => order.Claim(false));
        Assert.True(order.Failed);
    }

    [Fact]
    public async Task AlreadyCanceledOriginalTokenRejectsEvenBeforeReservation()
    {
        var order = new LinuxN04CheckpointOrder();
        using var token = new CancellationTokenSource(); token.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            order.BeforeNextAcceptAsync(Signal().Task, token.Token));
        Assert.Equal(token.Token, error.CancellationToken);
    }

    [Fact]
    public async Task OnlyTwoOriginalExchangesCanBeRetained()
    {
        var order = new LinuxN04CheckpointOrder();
        order.Retain(Task.CompletedTask); order.Retain(Task.CompletedTask);
        Assert.ThrowsAny<Exception>(() => order.Retain(Task.CompletedTask));
        order.Close(); await order.JoinAsync();
    }
}
