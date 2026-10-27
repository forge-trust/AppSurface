namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Procedure ordering controls with ordinary tasks, never fabricated native unit or owner facts.</summary>
public sealed class SupervisionWorkerSettlementObservationTests
{
    [Fact]
    public async Task RejectedObservationWaitsThenRechecksCleanupBeforeTheOriginalPublication()
    {
        var events = new List<string>();
        var reads = 0;
        await SupervisionWorkerSettlementObservation.WaitAsync(
            () => events.Add("guard"),
            () => { events.Add("read"); return Task.FromResult(++reads == 2); },
            () => { events.Add("wait"); return Task.CompletedTask; },
            () => events.Add("publish"));
        Assert.Equal(new[] { "guard", "read", "wait", "guard", "read", "publish" }, events);
    }

    [Fact]
    public async Task OriginalInspectionMustFinishBeforeEitherWaitOrPublication()
    {
        var entered = Signal();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = 0;
        var published = 0;
        var procedure = SupervisionWorkerSettlementObservation.WaitAsync(() => { },
            () => { entered.TrySetResult(); return observed.Task; },
            () => { waits++; return Task.CompletedTask; }, () => published++);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(procedure.IsCompleted);
            Assert.Equal(0, waits);
            Assert.Equal(0, published);
        }
        finally
        {
            observed.TrySetResult(true);
            await procedure.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(0, waits);
        Assert.Equal(1, published);
    }

    [Fact]
    public async Task OriginalWaitMustFinishBeforeASecondInspectionOrPublication()
    {
        var entered = Signal();
        var release = Signal();
        var reads = 0;
        var published = 0;
        var procedure = SupervisionWorkerSettlementObservation.WaitAsync(() => { },
            () => Task.FromResult(++reads == 2),
            () => { entered.TrySetResult(); return release.Task; }, () => published++);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(procedure.IsCompleted);
            Assert.Equal(1, reads);
            Assert.Equal(0, published);
        }
        finally
        {
            release.TrySetResult();
            await procedure.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(2, reads);
        Assert.Equal(1, published);
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("inspect-dispatch")]
    [InlineData("inspect-task")]
    [InlineData("wait-dispatch")]
    [InlineData("wait-task")]
    [InlineData("publish")]
    public async Task OriginalExceptionEscapesWithoutRetryOrLaterPublication(string stage)
    {
        var expected = new IOException("owned procedure failure");
        var events = new List<string>();
        var error = await Assert.ThrowsAsync<IOException>(() => SupervisionWorkerSettlementObservation.WaitAsync(
            () => { events.Add("guard"); if (stage == "guard") throw expected; },
            () =>
            {
                events.Add("inspect");
                if (stage == "inspect-dispatch") throw expected;
                return stage == "inspect-task" ? Task.FromException<bool>(expected) : Task.FromResult(stage == "publish");
            },
            () =>
            {
                events.Add("wait");
                if (stage == "wait-dispatch") throw expected;
                return Task.FromException(expected);
            },
            () => { events.Add("publish"); throw expected; }));
        Assert.Same(expected, error);
        Assert.Equal(1, events.Count(e => e == "guard"));
        Assert.Equal(stage == "publish" ? 1 : 0, events.Count(e => e == "publish"));
        Assert.Equal(stage.StartsWith("wait", StringComparison.Ordinal) ? 1 : 0, events.Count(e => e == "wait"));
    }

    [Fact]
    public async Task OriginalCancellationEscapesWithItsExactTokenWithoutPublishing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SupervisionWorkerSettlementObservation.WaitAsync(cancellation.Token.ThrowIfCancellationRequested,
                () => { calls++; return Task.FromResult(true); }, () => Task.CompletedTask, () => calls++));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("inspect")]
    [InlineData("wait")]
    [InlineData("publish")]
    public async Task MissingBindingRejectsBeforeAnyDispatch(string missing)
    {
        var calls = 0;
        Action guard = () => calls++;
        Func<Task<bool>> inspect = () => { calls++; return Task.FromResult(true); };
        Func<Task> wait = () => { calls++; return Task.CompletedTask; };
        Action publish = () => calls++;
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => SupervisionWorkerSettlementObservation.WaitAsync(
            missing == "guard" ? null! : guard, missing == "inspect" ? null! : inspect,
            missing == "wait" ? null! : wait, missing == "publish" ? null! : publish));
        Assert.Equal(missing switch
        {
            "guard" => "requireCleanup",
            "inspect" => "inspectSelectedUnitAndGroup",
            "wait" => "waitForNextObservation",
            _ => "recordStopped",
        }, error.ParamName);
        Assert.Equal(0, calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
