namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Reverse cleanup procedure tests using command data and local barriers only; no account utility or owner is created.</summary>
public sealed class SupervisionAccountCleanupTests
{
    [Fact]
    public async Task EmptyAndPartialReversalDataDoNoWorkAndReturnOnlyActualFaultState()
    {
        var attempts = 0;
        Assert.False(await SupervisionAccountCleanup.RunAsync([], _ => { attempts++; return Task.CompletedTask; },
            () => { }, (_, _) => { }));
        Assert.Equal(0, attempts);

        var command = Command(Names.Worker);
        Assert.False(await SupervisionAccountCleanup.RunAsync([command], _ => { attempts++; return Task.CompletedTask; },
            () => { }, (_, _) => { }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OriginalAttemptBarrierCanRemainPendingUntilItsActualTaskCompletes()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        var running = SupervisionAccountCleanup.RunAsync([Command(Names.Worker)],
            async _ => await barrier.Task.ConfigureAwait(false), () => settled++, (_, _) => { });
        try
        {
            Assert.False(running.IsCompleted);
            Assert.Equal(0, settled);
        }
        finally
        {
            barrier.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(await running);
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task SyncAndAsyncFaultsAreCapturedOnceAndLaterCommandsStillRun()
    {
        var commands = new[] { Command(Names.Worker), Command(Names.Subject) };
        var attempts = new List<string>();
        var captured = new List<Exception>();
        Exception? firstFailure = null;
        var settled = 0;
        var failed = await SupervisionAccountCleanup.RunAsync(commands, command =>
        {
            attempts.Add(command.Arguments[^1]);
            return attempts.Count == 1 ? throw new IOException("sync") : Task.FromException(new TimeoutException("async"));
        }, () => settled++, (_, error) =>
        {
            firstFailure ??= error;
            captured.Add(error);
        });

        Assert.True(failed);
        Assert.Equal(commands.Select(command => command.Arguments[^1]), attempts);
        Assert.Equal(2, settled);
        Assert.IsType<IOException>(firstFailure);
        Assert.Collection(captured, first => Assert.IsType<IOException>(first), second => Assert.IsType<TimeoutException>(second));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("access")]
    public async Task ConstructedFatalClassExceptionsStillAllowTheNextOriginalCommand(string kind)
    {
        Exception failure = kind switch
        {
            "memory" => new OutOfMemoryException(),
            "stack" => new StackOverflowException(),
            "access" => new AccessViolationException(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var attempts = 0;
        var captured = new List<Exception>();
        var settled = 0;
        var failed = await SupervisionAccountCleanup.RunAsync(
            [Command(Names.Worker), Command(Names.Subject)],
            _ =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(failure) : Task.CompletedTask;
            }, () => settled++, (_, error) => captured.Add(error));

        Assert.True(failed);
        Assert.Equal(2, attempts);
        Assert.Equal(2, settled);
        Assert.Collection(captured, error => Assert.Same(failure, error));
    }

    [Fact]
    public async Task SettlementFailureStopsFutureDispatchAfterTheCurrentFault()
    {
        var attempts = 0;
        var settled = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SupervisionAccountCleanup.RunAsync(
            [Command(Names.Worker), Command(Names.Subject)],
            _ => { attempts++; return Task.CompletedTask; }, () =>
            {
                settled++;
                throw new InvalidOperationException("settlement");
            }, (_, _) => { }));
        Assert.Equal(1, attempts);
        Assert.Equal(1, settled);
    }

    [Theory]
    [InlineData("commands")]
    [InlineData("attempt")]
    [InlineData("settled")]
    [InlineData("capture")]
    public async Task InputsAreValidatedBeforeAnyCallbackOrEnumerationWork(string nullInput)
    {
        var attempts = 0;
        var settled = 0;
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => nullInput switch
        {
            "commands" => SupervisionAccountCleanup.RunAsync(null!, _ =>
            {
                attempts++;
                return Task.CompletedTask;
            }, () => settled++, (_, _) => { }),
            "attempt" => SupervisionAccountCleanup.RunAsync([Command(Names.Worker)], null!, () => settled++, (_, _) => { }),
            "settled" => SupervisionAccountCleanup.RunAsync([Command(Names.Worker)], _ => Task.CompletedTask, null!, (_, _) => { }),
            "capture" => SupervisionAccountCleanup.RunAsync([Command(Names.Worker)], _ => Task.CompletedTask, () => settled++, null!),
            _ => throw new ArgumentOutOfRangeException(nameof(nullInput)),
        });
        Assert.Equal(nullInput switch
        {
            "commands" => "originalCommands", "attempt" => "originalAttempt", "settled" => "requireSettledAfterAttempt",
            "capture" => "captureFailure", _ => throw new ArgumentOutOfRangeException(nameof(nullInput)),
        }, error.ParamName);
        Assert.Equal(0, attempts);
        Assert.Equal(0, settled);
    }

    private static readonly LinuxRunAccountNames Names = LinuxRunAccountNames.Create(
        Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574"));

    private static LinuxRunAccountCommand Command(string name) =>
        LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, name);
}
