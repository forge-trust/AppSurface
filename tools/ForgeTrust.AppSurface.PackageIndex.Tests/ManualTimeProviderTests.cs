namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class ManualTimeProviderTests
{
    [Fact]
    public void Advance_FiresDueTimersInDueTimeAndCreationOrderAndRepeatsByPeriod()
    {
        var time = new ManualTimeProvider();
        var callbacks = new List<string>();

        using var repeating = time.CreateTimer(_ => callbacks.Add($"first-{time.GetUtcNow():ss}"), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        using var sameDueTime = time.CreateTimer(_ => callbacks.Add($"second-{time.GetUtcNow():ss}"), null,
            TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        using var earlier = time.CreateTimer(_ => callbacks.Add($"earlier-{time.GetUtcNow():ss}"), null,
            TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(
            ["earlier-01", "first-02", "second-02", "first-04", "first-06"],
            callbacks);
    }

    [Fact]
    public void Change_UpdatesDueTimeAndRecurringPeriod()
    {
        var time = new ManualTimeProvider();
        var callbacks = new List<TimeSpan>();
        using var timer = time.CreateTimer(_ => callbacks.Add(time.GetUtcNow() - DateTimeOffset.UnixEpoch), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)));
        time.Advance(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(13)],
            callbacks);
    }

    [Fact]
    public void DisabledTimersCanBeEnabledAndSingleShotPeriodsStayDisabledAfterFiring()
    {
        var time = new ManualTimeProvider();
        var callbackCount = 0;
        using var timer = time.CreateTimer(_ => callbackCount++, null,
            Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, callbackCount);
        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, TimeSpan.Zero));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, callbackCount);
        Assert.True(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task DisposeAndDisposeAsyncDisableTimersAndRejectFurtherChanges()
    {
        var time = new ManualTimeProvider();
        var callbackCount = 0;
        var disposed = time.CreateTimer(_ => callbackCount++, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var asynchronouslyDisposed = time.CreateTimer(_ => callbackCount++, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        disposed.Dispose();
        disposed.Dispose();
        Assert.False(disposed.Change(TimeSpan.Zero, TimeSpan.Zero));
        await asynchronouslyDisposed.DisposeAsync();
        await asynchronouslyDisposed.DisposeAsync();
        Assert.False(asynchronouslyDisposed.Change(TimeSpan.Zero, TimeSpan.Zero));

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void CallbackCanChangeAndDisposeItsTimerWithoutAdvanceOverwritingTheChange()
    {
        var time = new ManualTimeProvider();
        var callbacks = new List<TimeSpan>();
        ITimer timer = null!;
        timer = time.CreateTimer(_ =>
        {
            callbacks.Add(time.GetUtcNow() - DateTimeOffset.UnixEpoch);
            if (callbacks.Count == 1)
            {
                Assert.True(timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)));
            }
            else
            {
                timer.Dispose();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)], callbacks);
        Assert.False(timer.Change(TimeSpan.Zero, TimeSpan.Zero));
    }
}
