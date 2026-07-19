using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunWatchdogTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1ms", 1)]
    [InlineData("12s", 12_000)]
    [InlineData("7m", 420_000)]
    [InlineData("720h", 2_592_000_000)]
    public void DurationTryParse_ShouldAcceptExactGrammar(string value, long expectedMilliseconds)
    {
        var result = CoverageRunWatchdogDuration.TryParse(value, out var duration);

        Assert.True(result);
        Assert.Equal(expectedMilliseconds, duration.TotalMilliseconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("01s")]
    [InlineData("1")]
    [InlineData("1S")]
    [InlineData("1.5s")]
    [InlineData("-1s")]
    [InlineData("+1s")]
    [InlineData(" 1s")]
    [InlineData("1s ")]
    [InlineData("721h")]
    [InlineData("999999999999999999999999999999999h")]
    public void DurationTryParse_ShouldRejectMalformedOrOutOfRangeValues(string? value)
    {
        Assert.False(CoverageRunWatchdogDuration.TryParse(value, out _));
    }

    [Fact]
    public void OptionsValidate_ShouldRejectZeroNoProgressTimeoutButAllowDisabledHeartbeat()
    {
        var valid = new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Off, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var invalid = valid with { NoProgressTimeout = TimeSpan.Zero };

        valid.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
    }

    [Fact]
    public void Evaluate_ShouldNotAgeQueuedOperationAndShouldClassifyAtEqualityBoundary()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Warn);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Queued);
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.Empty(supervisor.Evaluate().NewlyStale);

        supervisor.Transition("project-a", CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var evaluation = supervisor.Evaluate();

        var stale = Assert.Single(evaluation.NewlyStale);
        Assert.Equal(TimeSpan.FromMinutes(10), stale.NoProgress);
        Assert.Equal(TimeSpan.FromMinutes(10), stale.Elapsed);
    }

    [Fact]
    public void ObserveOutput_ShouldResetOnlyOwningClockAndRearmWarning()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Warn);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        supervisor.Register(Project("project-b", 1), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var first = supervisor.Evaluate();
        Assert.Equal(["project-a", "project-b"], first.NewlyStale.Select(item => item.Identity));

        Assert.Empty(supervisor.Evaluate().NewlyStale);
        supervisor.ObserveOutput("project-b", 42);
        time.Advance(TimeSpan.FromMinutes(10));
        var second = supervisor.Evaluate();

        var rearmed = Assert.Single(second.NewlyStale);
        Assert.Equal("project-b", rearmed.Identity);
        Assert.Equal(42, rearmed.OutputBytes);
        Assert.Equal(2, rearmed.ProgressSequence);
    }

    [Fact]
    public void Evaluate_ShouldUseWorkflowThenProjectExecutionOrder()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Warn);
        supervisor.Register(Project("later", 2), CoverageRunWatchdogOperationState.Running);
        supervisor.Register(new("build", CoverageRunWatchdogOperationKind.Build), CoverageRunWatchdogOperationState.Running);
        supervisor.Register(Project("earlier", 1), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));

        var stale = supervisor.Evaluate().NewlyStale;

        Assert.Equal(["build", "earlier", "later"], stale.Select(item => item.Identity));
    }

    [Fact]
    public void TryClaimTerminal_ShouldRejectCandidateAfterProgress()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var candidate = Assert.Single(supervisor.Evaluate().NewlyStale);
        supervisor.ObserveOutput("project-a", 1);

        var claimed = supervisor.TryClaimTerminal(candidate, out var terminal);

        Assert.False(claimed);
        Assert.Null(terminal);
    }

    [Fact]
    public void TryClaimTerminal_ShouldAllowOnlyFirstCauseAndCloseMutations()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        supervisor.Register(Project("project-b", 1), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var candidates = supervisor.Evaluate().NewlyStale;

        Assert.True(supervisor.TryClaimTerminal(candidates[0], out var first));
        Assert.False(supervisor.TryClaimTerminal(candidates[1], out var retained));
        Assert.Same(first, retained);
        Assert.Equal("project-a", supervisor.TerminalCause?.Identity);
        Assert.Throws<InvalidOperationException>(() => supervisor.ObserveOutput("project-b", 1));
        Assert.Throws<InvalidOperationException>(() =>
            supervisor.Register(Project("project-c", 2), CoverageRunWatchdogOperationState.Queued));
    }

    [Fact]
    public void TryComplete_ShouldPreventALaterWatchdogClaim()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var candidate = Assert.Single(supervisor.Evaluate().NewlyStale);

        Assert.True(supervisor.TryComplete(out var terminal, out var externalCancellation));
        Assert.Null(terminal);
        Assert.False(externalCancellation);
        Assert.False(supervisor.TryClaimTerminal(candidate, out terminal));
        Assert.Null(terminal);
    }

    [Fact]
    public void TryComplete_ShouldPreserveAnEarlierWatchdogClaim()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var candidate = Assert.Single(supervisor.Evaluate().NewlyStale);

        Assert.True(supervisor.TryClaimTerminal(candidate, out var claimed));
        Assert.False(supervisor.TryComplete(out var terminal, out var externalCancellation));
        Assert.Same(claimed, terminal);
        Assert.False(externalCancellation);
    }

    [Fact]
    public void ExternalCancellation_ShouldWinOnlyWhenItClaimsBeforeCompletion()
    {
        var time = new ManualTimeProvider();
        var canceledFirst = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        canceledFirst.ClaimExternalCancellation();

        Assert.False(canceledFirst.TryComplete(out var terminal, out var externalCancellation));
        Assert.Null(terminal);
        Assert.True(externalCancellation);

        var completedFirst = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        Assert.True(completedFirst.TryComplete(out terminal, out externalCancellation));
        completedFirst.ClaimExternalCancellation();
        Assert.True(completedFirst.TryComplete(out terminal, out externalCancellation));
        Assert.False(externalCancellation);
    }

    [Fact]
    public void Evaluate_ShouldKeepHeartbeatIndependentFromOffMode()
    {
        var time = new ManualTimeProvider();
        var options = new CoverageRunWatchdogOptions(
            CoverageRunWatchdogMode.Off,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10));
        var supervisor = new CoverageRunWatchdogSupervisor(time, options);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromSeconds(30));

        var evaluation = supervisor.Evaluate();

        Assert.True(evaluation.HeartbeatDue);
        Assert.Empty(evaluation.NewlyStale);
        Assert.False(supervisor.Evaluate().HeartbeatDue);
    }

    [Fact]
    public void GetNextDelay_ShouldChooseEarlierOperationDeadlineAndReachZeroExactly()
    {
        var time = new ManualTimeProvider();
        var options = new CoverageRunWatchdogOptions(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(10));
        var supervisor = new CoverageRunWatchdogSupervisor(time, options);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(TimeSpan.FromMinutes(6), supervisor.GetNextDelay());

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(TimeSpan.Zero, supervisor.GetNextDelay());
    }

    [Fact]
    public void GetNextDelay_ShouldChooseEarlierHeartbeatAndResetAfterEvaluation()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Warn);
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(10), supervisor.GetNextDelay());
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(supervisor.Evaluate().HeartbeatDue);
        Assert.Equal(TimeSpan.FromSeconds(30), supervisor.GetNextDelay());
    }

    [Fact]
    public void GetNextDelay_ShouldIgnoreQueuedAndLatchedOperationsAndCapAtOneDay()
    {
        var time = new ManualTimeProvider();
        var options = new CoverageRunWatchdogOptions(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromDays(30));
        var supervisor = new CoverageRunWatchdogSupervisor(time, options);
        supervisor.Register(Project("queued", 0), CoverageRunWatchdogOperationState.Queued);

        Assert.Equal(TimeSpan.FromHours(24), supervisor.GetNextDelay());

        supervisor.Register(Project("running", 1), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromDays(30));
        Assert.Equal(TimeSpan.Zero, supervisor.GetNextDelay());
        Assert.Single(supervisor.Evaluate().NewlyStale);
        Assert.Equal(TimeSpan.FromHours(24), supervisor.GetNextDelay());
    }

    [Fact]
    public void Snapshot_ShouldUseMonotonicTimeAndDisplayUtcIndependently()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Warn);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromSeconds(5), utcAdvance: TimeSpan.FromHours(-3));

        var snapshot = Assert.Single(supervisor.Snapshot().Operations);

        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.NoProgress);
        Assert.Equal(ManualTimeProvider.Epoch, snapshot.LastProgressAtUtc);
    }

    [Fact]
    public void ArtifactSerializer_ShouldEmitNormativeNamesAndLowercaseEnums()
    {
        var artifact = CreateArtifact([CreateIncidentOperation("tests/A.Tests/A.Tests.csproj")]);

        var bytes = CoverageRunWatchdogArtifactSerializer.Serialize(artifact);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("warn", root.GetProperty("watchdogMode").GetString());
        Assert.Equal("project", root.GetProperty("primary").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("concurrentlyStale").ValueKind);
        Assert.DoesNotContain("secret-value", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactSerializer_ShouldDropConcurrentDetailsThenTrailingRecordsToFitBudget()
    {
        var operations = Enumerable.Range(0, 600)
            .Select(index => CreateIncidentOperation($"tests/{new string('p', 200)}/{index}.Tests.csproj"))
            .ToArray();
        var artifact = CreateArtifact(operations);

        var bytes = CoverageRunWatchdogArtifactSerializer.Serialize(artifact);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        Assert.True(bytes.Length <= CoverageRunWatchdogArtifactSerializer.MaximumBytes);
        Assert.True(root.GetProperty("concurrentlyStaleOmitted").GetInt32() > 0);
        Assert.True(root.GetProperty("concurrentlyStale").GetArrayLength() < operations.Length);
        foreach (var operation in root.GetProperty("concurrentlyStale").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, operation.GetProperty("log").ValueKind);
            Assert.Empty(operation.GetProperty("command").GetProperty("options").EnumerateArray());
        }
    }

    [Fact]
    public void ArtifactSerializer_ShouldRejectUnsupportedSchema()
    {
        var artifact = CreateArtifact([]) with { SchemaVersion = 2 };

        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageRunWatchdogArtifactSerializer.Serialize(artifact));
    }

    [Fact]
    public async Task ArtifactWriter_ShouldAtomicallyReplaceDestination()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Join(directory.Path, "coverage-watchdog.json");
        await File.WriteAllTextAsync(destination, "old");
        var writer = new CoverageRunWatchdogArtifactWriter(TimeProvider.System);

        var result = await writer.WriteAsync(destination, CreateArtifact([]), CancellationToken.None);

        Assert.True(result.Written);
        Assert.Null(result.Detail);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ArtifactWriter_ShouldAllowOnlyOneOutstandingWrite()
    {
        var storage = new ControlledArtifactStorage();
        var delay = new ControlledDelay();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, delay);

        var first = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        var busy = await writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        storage.Complete(commit: true);

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.WriterBusy, busy);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Success, await first);
        Assert.Equal(1, storage.CallCount);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldRevokeLateCommitAfterTimeoutAndPermitRetryOnlyAfterCompletion()
    {
        var storage = new ControlledArtifactStorage();
        var delay = new ControlledDelay();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, delay);
        var first = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);

        delay.Complete();
        var timedOut = await first;
        var busy = await writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        storage.Complete(commit: true);
        await storage.CurrentTask;
        var timedOutPermission = storage.Permission;
        storage.CommitImmediately = true;
        var retry = await writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.TimedOut, timedOut);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.WriterBusy, busy);
        Assert.False(timedOutPermission?.WasCommitted);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Success, retry);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldRevokeLateCommitAndPropagateCancellation()
    {
        var storage = new ControlledArtifactStorage();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, new ControlledDelay());
        using var cancellation = new CancellationTokenSource();
        var write = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        storage.Complete(commit: true);
        await storage.CurrentTask;

        Assert.False(storage.Permission?.WasCommitted);
    }

    [Fact]
    public async Task Runtime_ShouldWakeForRegisteredOperationWhenHeartbeatsAreDisabledAndReportArtifactFailure()
    {
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Warn,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            artifactWriter: new StubArtifactWriter(new(false, "forced-failure")));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        await WaitForAsync(() => console.ReadErrorString().Contains("ASCOV122", StringComparison.Ordinal));
        runtime.Transition("project-a", CoverageRunWatchdogOperationState.Complete);
        await runtime.CompleteAsync();

        Assert.Contains("forced-failure", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("Coverage watchdog warning", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldPromoteBootstrapIncidentWhenCanonicalOutputIsBound()
    {
        using var output = new TemporaryDirectory();
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Warn,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);
        runtime.Register(
            new CoverageRunWatchdogOperation(CoverageRunWatchdogOperationIds.Discovery, CoverageRunWatchdogOperationKind.Discovery),
            CoverageRunWatchdogOperationState.Running);
        await WaitForAsync(() => console.ReadOutputString().Contains("Coverage watchdog warning", StringComparison.Ordinal));

        await runtime.BindCanonicalOutputAsync(output.Path);
        runtime.Transition(CoverageRunWatchdogOperationIds.Discovery, CoverageRunWatchdogOperationState.Complete);
        await runtime.CompleteAsync();

        var artifactPath = Path.Join(output.Path, "coverage-watchdog.json");
        Assert.True(File.Exists(artifactPath));
        using var artifact = JsonDocument.Parse(await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal("warning", artifact.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("discovery", artifact.RootElement.GetProperty("primary").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Runtime_ShouldNotCreateBootstrapDirectoryWhenClassificationIsOff()
    {
        using var output = new TemporaryDirectory();
        var blockingFile = Path.Join(output.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Off, TimeSpan.Zero, TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            bootstrapDirectory: Path.Join(blockingFile, "bootstrap"));

        await runtime.CompleteAsync();

        Assert.True(File.Exists(blockingFile));
        Assert.DoesNotContain("ASCOV122", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldDegradeBootstrapCreationFailureWithoutReplacingWarnOutcome()
    {
        using var output = new TemporaryDirectory();
        var blockingFile = Path.Join(output.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Warn, TimeSpan.Zero, TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            bootstrapDirectory: Path.Join(blockingFile, "bootstrap"));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        await WaitForAsync(() => console.ReadErrorString().Contains("ASCOV122", StringComparison.Ordinal));
        runtime.Transition("project-a", CoverageRunWatchdogOperationState.Complete);
        await runtime.CompleteAsync();

        Assert.Contains("bootstrap-unavailable", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldPreserveBootstrapArtifactWhenCanonicalPromotionFails()
    {
        using var output = new TemporaryDirectory();
        var bootstrap = Path.Join(output.Path, "bootstrap");
        var blockingCanonical = Path.Join(output.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingCanonical, "block");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Warn, TimeSpan.Zero, TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            bootstrapDirectory: bootstrap);
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        await WaitForAsync(() => console.ReadOutputString().Contains("Coverage watchdog warning", StringComparison.Ordinal));

        await runtime.BindCanonicalOutputAsync(blockingCanonical);
        await WaitForAsync(() => console.ReadErrorString().Contains("ASCOV122", StringComparison.Ordinal));
        runtime.Transition("project-a", CoverageRunWatchdogOperationState.Complete);
        await runtime.CompleteAsync();

        Assert.Contains("bootstrap-promotion-failed", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(bootstrap, "coverage-watchdog.json")));
    }

    [Fact]
    public async Task Runtime_ShouldPreserveExit124WhenTerminalArtifactWriteFails()
    {
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            artifactWriter: new StubArtifactWriter(new(false, "forced-failure")));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, runtime.RunCancellationToken).WaitAsync(TimeSpan.FromSeconds(5)));
        var exception = await Assert.ThrowsAsync<CommandException>(runtime.ThrowIfWatchdogTerminalAsync);
        await WaitForAsync(() => console.ReadErrorString().Contains("ASCOV122", StringComparison.Ordinal));

        Assert.Equal(124, exception.ExitCode);
        Assert.Contains("ASCOV121", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASCOV122", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("forced-failure", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldPublishTerminalOutcomeBeforeBlockingCancellationCallbackCompletes()
    {
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = runtime.RunCancellationToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<CommandException>(
                () => runtime.ThrowIfWatchdogTerminalAsync().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(124, exception.ExitCode);
        }
        finally
        {
            releaseCallback.TrySetResult();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The watchdog condition was not observed.");
    }

    private static CoverageRunWatchdogSupervisor CreateSupervisor(
        TimeProvider time,
        CoverageRunWatchdogMode mode)
        => new(time, new CoverageRunWatchdogOptions(mode, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)));

    private static CoverageRunWatchdogOperation Project(string identity, int index)
        => new(identity, CoverageRunWatchdogOperationKind.Project, index, $"tests/{identity}.csproj", $"projects/{identity}/dotnet-test.log");

    private static CoverageRunWatchdogArtifact CreateArtifact(
        IReadOnlyList<CoverageRunWatchdogIncidentOperation> concurrent)
        => new(
            1,
            1,
            "warning",
            null,
            CoverageRunWatchdogMode.Warn,
            30_000,
            600_000,
            600_000,
            ManualTimeProvider.Epoch,
            CreateIncidentOperation("tests/Primary.Tests/Primary.Tests.csproj"),
            concurrent,
            0,
            new CoverageRunWatchdogCleanup("not-requested", null));

    private static CoverageRunWatchdogIncidentOperation CreateIncidentOperation(string project)
        => new(
            CoverageRunWatchdogOperationKind.Project,
            project,
            CoverageRunWatchdogOperationState.Running,
            600_000,
            600_000,
            ManualTimeProvider.Epoch,
            17,
            4_218,
            $"projects/{project.GetHashCode(StringComparison.Ordinal):x}/dotnet-test.log",
            new CoverageRunWatchdogCommand("dotnet", ["test", "--configuration"]));

    private sealed class ManualTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset Epoch = new(2026, 7, 19, 16, 0, 0, TimeSpan.Zero);

        private long _timestamp;
        private DateTimeOffset _utcNow = Epoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed, TimeSpan? utcAdvance = null)
        {
            _timestamp = checked(_timestamp + elapsed.Ticks);
            _utcNow += utcAdvance ?? elapsed;
        }
    }

    private sealed class ControlledDelay : ICoverageRunWatchdogDelay
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => _completion.Task;

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class ControlledArtifactStorage : ICoverageRunWatchdogArtifactStorage
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public CoverageRunWatchdogCommitPermission? Permission { get; private set; }

        public Task CurrentTask => _completion.Task;

        public bool CommitImmediately { get; set; }

        public Task WriteTemporaryAndCommitAsync(
            string destinationPath,
            ReadOnlyMemory<byte> bytes,
            CoverageRunWatchdogCommitPermission permission,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Permission = permission;
            if (CommitImmediately && permission.TryBeginCommit())
            {
                permission.CompleteCommit();
                return Task.CompletedTask;
            }

            return _completion.Task;
        }

        public void Complete(bool commit)
        {
            if (commit && Permission?.TryBeginCommit() == true)
            {
                Permission.CompleteCommit();
            }

            _completion.TrySetResult();
        }
    }

    private sealed class StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult result) : ICoverageRunWatchdogArtifactWriter
    {
        public Task<CoverageRunWatchdogArtifactWriteResult> WriteAsync(
            string destinationPath,
            CoverageRunWatchdogArtifact artifact,
            CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"appsurface-watchdog-writer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
