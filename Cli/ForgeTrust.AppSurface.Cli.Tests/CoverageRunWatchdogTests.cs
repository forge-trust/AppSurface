using System.Diagnostics;
using System.Text;
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
    [InlineData("3000000000000h")]
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
    public void OptionsValidate_ShouldRejectInvalidModeAndHeartbeatBounds()
    {
        var valid = CoverageRunWatchdogOptions.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { Mode = (CoverageRunWatchdogMode)999 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { HeartbeatInterval = TimeSpan.FromMilliseconds(-1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { HeartbeatInterval = CoverageRunWatchdogDuration.Maximum + TimeSpan.FromMilliseconds(1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { NoProgressTimeout = CoverageRunWatchdogDuration.Maximum + TimeSpan.FromMilliseconds(1) }).Validate());
    }

    [Fact]
    public void Supervisor_ShouldRejectInvalidRegistrationAndIgnoreOutputOutsideActiveStates()
    {
        var supervisor = CreateSupervisor(new ManualTimeProvider(), CoverageRunWatchdogMode.Warn);
        var blank = Project(" ", 0);
        var queued = Project("queued", 1);
        var complete = Project("complete", 2);

        Assert.Throws<ArgumentException>(() => supervisor.Register(blank, CoverageRunWatchdogOperationState.Queued));
        supervisor.Register(queued, CoverageRunWatchdogOperationState.Queued);
        Assert.Throws<InvalidOperationException>(() => supervisor.Register(queued, CoverageRunWatchdogOperationState.Queued));
        supervisor.Register(complete, CoverageRunWatchdogOperationState.Complete);
        Assert.False(supervisor.ObserveOutput("queued", 0));
        Assert.False(supervisor.ObserveOutput("queued", 10));
        Assert.False(supervisor.ObserveOutput("complete", 10));
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
    public void TryClaimTerminal_ShouldRevalidateConcurrentStallsAtClaimTime()
    {
        var time = new ManualTimeProvider();
        var supervisor = CreateSupervisor(time, CoverageRunWatchdogMode.Fail);
        supervisor.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        supervisor.Register(Project("project-b", 1), CoverageRunWatchdogOperationState.Running);
        time.Advance(TimeSpan.FromMinutes(10));
        var candidates = supervisor.Evaluate().NewlyStale;
        supervisor.ObserveOutput("project-b", 1);

        var claimed = supervisor.TryClaimTerminal(candidates[0], out var terminal, out var terminalEvaluation);

        Assert.True(claimed);
        Assert.Equal("project-a", terminal?.Identity);
        var stale = Assert.Single(Assert.IsType<CoverageRunWatchdogEvaluation>(terminalEvaluation).NewlyStale);
        Assert.Equal("project-a", stale.Identity);
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

        var evaluationSnapshot = supervisor.Snapshot();
        var snapshot = Assert.Single(evaluationSnapshot.Operations);

        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.NoProgress);
        Assert.Equal(ManualTimeProvider.Epoch, snapshot.LastProgressAtUtc);
        Assert.Equal(ManualTimeProvider.Epoch - TimeSpan.FromHours(3), evaluationSnapshot.CapturedAtUtc);
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
        var omitted = root.GetProperty("concurrentlyStaleOmitted").GetInt32();
        Assert.True(omitted > 0);
        var retained = root.GetProperty("concurrentlyStale");
        Assert.InRange(
            retained.GetArrayLength(),
            0,
            CoverageRunWatchdogArtifactSerializer.MaximumConcurrentOperations);
        Assert.Equal(operations.Length, retained.GetArrayLength() + omitted);
        var retainedIndex = 0;
        foreach (var operation in retained.EnumerateArray())
        {
            Assert.Equal(operations[retainedIndex].Project, operation.GetProperty("project").GetString());
            Assert.Equal(JsonValueKind.Null, operation.GetProperty("log").ValueKind);
            Assert.Empty(operation.GetProperty("command").GetProperty("options").EnumerateArray());
            retainedIndex++;
        }
    }

    [Fact]
    public void ArtifactSerializer_ShouldRejectUnsupportedSchema()
    {
        var artifact = CreateArtifact([]) with { SchemaVersion = 2 };

        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageRunWatchdogArtifactSerializer.Serialize(artifact));
    }

    [Fact]
    public void ArtifactSerializer_ShouldValidateDeserializedPayload()
    {
        var bytes = CoverageRunWatchdogArtifactSerializer.Serialize(CreateArtifact([]));
        var artifact = CoverageRunWatchdogArtifactSerializer.Deserialize(bytes);
        var unsupported = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal));

        Assert.Equal(1, artifact.SchemaVersion);
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize("null"u8));
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize("{\"schemaVersion\":1}"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize(unsupported));

        var invalidMode = CreateArtifact([]) with { WatchdogMode = CoverageRunWatchdogMode.Off };
        var invalidCleanup = CreateArtifact([]) with { Cleanup = new CoverageRunWatchdogCleanup("failed", "root-timeout") };
        var invalidPrimary = CreateArtifact([]) with
        {
            Primary = CreateIncidentOperation("tests/Primary.Tests/Primary.Tests.csproj") with
            {
                State = CoverageRunWatchdogOperationState.Complete,
            },
        };
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize(
            CoverageRunWatchdogArtifactSerializer.Serialize(invalidMode)));
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize(
            CoverageRunWatchdogArtifactSerializer.Serialize(invalidCleanup)));
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize(
            CoverageRunWatchdogArtifactSerializer.Serialize(invalidPrimary)));

        var minimalOperation = new CoverageRunWatchdogIncidentOperation(
            CoverageRunWatchdogOperationKind.Project,
            null,
            CoverageRunWatchdogOperationState.Running,
            0,
            0,
            null,
            0,
            0,
            null,
            null);
        var overCap = CreateArtifact(Enumerable.Repeat(
            minimalOperation,
            CoverageRunWatchdogArtifactSerializer.MaximumConcurrentOperations + 1).ToArray());
        var unboundedOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var overCapBytes = JsonSerializer.SerializeToUtf8Bytes(overCap, unboundedOptions);
        Assert.True(overCapBytes.Length <= CoverageRunWatchdogArtifactSerializer.MaximumBytes);
        Assert.Throws<JsonException>(() => CoverageRunWatchdogArtifactSerializer.Deserialize(overCapBytes));
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
    public async Task ArtifactWriter_ShouldConvertSerializationFailureAndRemainAvailable()
    {
        var storage = new ControlledArtifactStorage { CommitImmediately = true };
        var writer = new CoverageRunWatchdogArtifactWriter(storage, new ControlledDelay());
        var oversized = CreateArtifact([]) with
        {
            Primary = CreateIncidentOperation(new string('p', CoverageRunWatchdogArtifactSerializer.MaximumBytes)),
        };

        var failed = await writer.WriteAsync("/owned/coverage-watchdog.json", oversized, CancellationToken.None);
        var retry = await writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Failed, failed);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Success, retry);
        Assert.Equal(1, storage.CallCount);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldArmDeadlineBeforeSynchronousStorageSetup()
    {
        using var storage = new SynchronouslyBlockingArtifactStorage();
        var delay = new ControlledDelay();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, delay);
        var write = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        delay.Complete();

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.TimedOut, await write.WaitAsync(TimeSpan.FromSeconds(5)));
        storage.Release.Set();
        await storage.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, storage.CommitCount);
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
        storage.CommitImmediately = true;
        var retryDeadline = Stopwatch.StartNew();
        CoverageRunWatchdogArtifactWriteResult retry;
        do
        {
            Assert.True(retryDeadline.Elapsed < TimeSpan.FromSeconds(5));
            await Task.Yield();
            retry = await writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        }
        while (retry == CoverageRunWatchdogArtifactWriteResult.WriterBusy);

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.TimedOut, timedOut);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.WriterBusy, busy);
        Assert.Equal(1, storage.CommitCount);
        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Success, retry);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldRevokeLateCommitAndPropagateCancellation()
    {
        var storage = new ControlledArtifactStorage();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, new ControlledDelay());
        using var cancellation = new CancellationTokenSource();
        var write = Task.Run(() => writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), cancellation.Token));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        storage.Complete(commit: true);
        await storage.CurrentTask;

        Assert.Equal(0, storage.CommitCount);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldAwaitAnAlreadyStartedCommitBeforePropagatingCancellation()
    {
        var storage = new BlockingCommitArtifactStorage();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, new ControlledDelay());
        using var cancellation = new CancellationTokenSource();
        var write = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), cancellation.Token);
        storage.ReleaseStaging.TrySetResult("/private/watchdog.tmp");
        await storage.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        Assert.False(write.IsCompleted);
        storage.ReleaseCommit.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(1, storage.CommitCount);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldReportAnAlreadyStartedCommitInsteadOfALateTimeout()
    {
        var storage = new BlockingCommitArtifactStorage();
        var delay = new ControlledDelay();
        var writer = new CoverageRunWatchdogArtifactWriter(storage, delay);
        var write = writer.WriteAsync("/owned/coverage-watchdog.json", CreateArtifact([]), CancellationToken.None);
        storage.ReleaseStaging.TrySetResult("/private/watchdog.tmp");
        await storage.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        delay.Complete();
        await Task.Yield();
        Assert.False(write.IsCompleted);
        storage.ReleaseCommit.TrySetResult();

        Assert.Equal(CoverageRunWatchdogArtifactWriteResult.Success, await write);
        Assert.Equal(1, storage.CommitCount);
    }

    [Fact]
    public void ArtifactCommitPermission_ShouldRejectCompletionWithoutAReservation()
    {
        var permission = new CoverageRunWatchdogCommitPermission();

        Assert.Throws<InvalidOperationException>(permission.CompleteCommit);
        Assert.True(permission.TryRevoke());
        Assert.False(permission.TryBeginCommit());
        Assert.False(permission.WasCommitted);
    }

    [Fact]
    public void Runtime_ShouldValidateRequiredDependenciesAndConsoleTimeout()
    {
        using var console = new FakeInMemoryConsole();
        var options = CoverageRunWatchdogOptions.Default;

        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogRuntime(null!, TimeProvider.System, options, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogRuntime(console, null!, options, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogRuntime(console, TimeProvider.System, null!, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            options,
            CancellationToken.None,
            consoleTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogArtifactWriter(null!));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogArtifactWriter(null!, new ControlledDelay()));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogArtifactWriter(new ControlledArtifactStorage(), null!));
        Assert.Throws<ArgumentNullException>(() => new CoverageRunWatchdogDelay(null!));
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
        Assert.DoesNotContain("project=null", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "bootstrap-promotion-failed")]
    [InlineData(true, "bootstrap-promotion-failed")]
    public async Task Runtime_ShouldRejectInvalidBootstrapEvidence(bool oversized, string expectedDetail)
    {
        using var directory = new TemporaryDirectory();
        var bootstrap = Path.Join(directory.Path, "bootstrap");
        var canonical = Path.Join(directory.Path, "canonical");
        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(canonical);
        var bytes = oversized
            ? new byte[CoverageRunWatchdogArtifactSerializer.MaximumBytes + 1]
            : "not-json"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Join(bootstrap, "coverage-watchdog.json"), bytes);
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Off, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            CancellationToken.None,
            bootstrapDirectory: bootstrap);

        await runtime.BindCanonicalOutputAsync(canonical);
        await runtime.CompleteAsync();

        Assert.Contains(expectedDetail, console.ReadErrorString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(bootstrap, "coverage-watchdog.json")));
        Assert.False(File.Exists(Path.Join(canonical, "coverage-watchdog.json")));
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
        Assert.StartsWith("ASCOV121 Coverage run stalled. Cause: Project \"tests/project-a.csproj\" produced no observable progress", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("progress for 0s", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Artifact: unavailable (forced-failure) Cleanup: complete", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASCOV122", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("forced-failure", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldIdentifyOnlyItsPublishedTerminalException()
    {
        using var directory = new TemporaryDirectory();
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            bootstrapDirectory: directory.Path);

        Assert.False(runtime.IsTerminalException(new InvalidOperationException()));
        Assert.Throws<ArgumentNullException>(() => runtime.IsTerminalException(null!));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        var terminal = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runtime.IsTerminalException(terminal));
    }

    [Fact]
    public async Task Runtime_ShouldQuoteTerminalArtifactPathForLogSafety()
    {
        using var directory = new TemporaryDirectory();
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success),
            bootstrapDirectory: Path.Join(directory.Path, "line\nbreak"));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        var exception = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(124, exception.ExitCode);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.Contains("line\\nbreak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldRejectStagedCanonicalCommitAfterTerminalClaim()
    {
        using var directory = new TemporaryDirectory();
        var staged = Path.Join(directory.Path, ".summary.txt.staged");
        var destination = Path.Join(directory.Path, "summary.txt");
        await File.WriteAllTextAsync(staged, "private");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.ThrowsAny<OperationCanceledException>(() => runtime.CommitStagedFile(staged, destination));
        Assert.True(File.Exists(staged));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Runtime_ShouldPreflightAllStagedFilesBeforePromotingASet()
    {
        using var directory = new TemporaryDirectory();
        var firstStaged = Path.Join(directory.Path, ".first.staged");
        var firstDestination = Path.Join(directory.Path, "first.json");
        await File.WriteAllTextAsync(firstStaged, "private");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            CoverageRunWatchdogOptions.Default,
            CancellationToken.None);

        Assert.Throws<CoverageRunStagedCommitPreflightException>(() => runtime.CommitStagedFiles(
            [
                (firstStaged, firstDestination),
                (Path.Join(directory.Path, ".missing.staged"), Path.Join(directory.Path, "second.json")),
            ]));
        await runtime.CompleteAsync();

        Assert.True(File.Exists(firstStaged));
        Assert.False(File.Exists(firstDestination));
    }

    [Fact]
    public async Task Runtime_ShouldRollBackEarlierStagedPromotionsWhenALaterMoveFails()
    {
        using var directory = new TemporaryDirectory();
        var firstStaged = Path.Join(directory.Path, ".first.staged");
        var firstDestination = Path.Join(directory.Path, "first.json");
        var secondStaged = Path.Join(directory.Path, ".second.staged");
        var secondDestination = Path.Join(directory.Path, "missing", "second.json");
        await File.WriteAllTextAsync(firstStaged, "new-first");
        await File.WriteAllTextAsync(firstDestination, "old-first");
        await File.WriteAllTextAsync(secondStaged, "new-second");
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            CoverageRunWatchdogOptions.Default,
            CancellationToken.None);

        Assert.ThrowsAny<IOException>(() => runtime.CommitStagedFiles(
            [(firstStaged, firstDestination), (secondStaged, secondDestination)]));
        await runtime.CompleteAsync();

        Assert.Equal("new-first", await File.ReadAllTextAsync(firstStaged));
        Assert.Equal("old-first", await File.ReadAllTextAsync(firstDestination));
        Assert.Equal("new-second", await File.ReadAllTextAsync(secondStaged));
        Assert.False(File.Exists(secondDestination));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.watchdog-backup", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Runtime_ShouldRenderBoundedProjectIdentityWithoutSplittingASurrogatePair()
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
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success));
        var project = new string('p', 511) + "😀";
        runtime.Register(
            new CoverageRunWatchdogOperation("project:unicode", CoverageRunWatchdogOperationKind.Project, 0, project),
            CoverageRunWatchdogOperationState.Running);

        var terminal = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(124, terminal.ExitCode);
        Assert.Contains(new string('p', 511), terminal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("😀", terminal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldWaitForHealthyBusyConsoleBeforeWritingTerminalDiagnostic()
    {
        using var input = new MemoryStream();
        using var output = new GateWriteStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(input, output, error);
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Fail,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            CancellationToken.None,
            consoleTimeout: TimeSpan.FromSeconds(1),
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success));
        var busyWrite = runtime.WriteOutputLineAsync("earlier output");
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        await Task.Delay(100);
        output.Release.Set();
        var terminal = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));
        await busyWrite;

        Assert.Equal(124, terminal.ExitCode);
        Assert.Contains("ASCOV121", Encoding.UTF8.GetString(error.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldContainRawConsoleWriteFailures()
    {
        using var input = new MemoryStream();
        using var output = new ThrowingWriteStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(input, output, error);
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(CoverageRunWatchdogMode.Off, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        await runtime.WriteOutputAsync("raw output");
        await runtime.CompleteAsync();
    }

    [Fact]
    public async Task Runtime_ShouldCloseProcessRegistrationAndIgnoreLateOutputAfterTerminalClaim()
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
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        var request = runtime.CreateProcessRequest(
            "dotnet",
            ["test"],
            Directory.GetCurrentDirectory(),
            "project-a",
            new CoverageRunSafeCommand("dotnet", ["test"]));
        request.ProcessCompleted?.Invoke();

        await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.ThrowsAny<OperationCanceledException>(() => runtime.CreateProcessRequest(
            "dotnet",
            ["test"],
            Directory.GetCurrentDirectory(),
            "project-a",
            new CoverageRunSafeCommand("dotnet", ["test"])));
        request.ObserveOutput?.Invoke(17);
        using var process = new Process();
        request.ProcessStarted?.Invoke(process);
        request.ProcessCompleted?.Invoke();
    }

    [Fact]
    public async Task Runtime_ShouldNotTransferCleanupOwnershipForAReservedProcessThatHasNotStarted()
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
            artifactWriter: new StubArtifactWriter(CoverageRunWatchdogArtifactWriteResult.Success));
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        var request = runtime.CreateProcessRequest(
            "dotnet",
            ["test"],
            Directory.GetCurrentDirectory(),
            "project-a",
            new CoverageRunSafeCommand("dotnet", ["test"]));

        var terminal = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(124, terminal.ExitCode);
        using var lateProcess = new Process();
        request.ProcessStarted?.Invoke(lateProcess);
        request.ProcessCompleted?.Invoke();
    }

    [Fact]
    public async Task Runtime_ShouldCancelCanonicalBindingWhileAnIncidentWriterOwnsTheGate()
    {
        using var output = new TemporaryDirectory();
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        var writer = new BlockingArtifactWriter();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            new CoverageRunWatchdogOptions(
                CoverageRunWatchdogMode.Warn,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(50)),
            cancellation.Token,
            artifactWriter: writer);
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);
        await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var binding = runtime.BindCanonicalOutputAsync(output.Path);

        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding);
        }
        finally
        {
            writer.Completion.TrySetResult(CoverageRunWatchdogArtifactWriteResult.Failed);
        }
    }

    [Fact]
    public async Task Runtime_ShouldPreserveTerminalClassificationWhenTheArtifactWriterThrows()
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
            artifactWriter: new ThrowingArtifactWriter());
        runtime.Register(Project("project-a", 0), CoverageRunWatchdogOperationState.Running);

        var exception = await runtime.TerminalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(124, exception.ExitCode);
        Assert.Contains("terminal-handler-failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ShouldReturnWhenNoWatchdogTerminalWasClaimed()
    {
        using var console = new FakeInMemoryConsole();
        await using var runtime = new CoverageRunWatchdogRuntime(
            console,
            TimeProvider.System,
            CoverageRunWatchdogOptions.Default,
            CancellationToken.None);

        await runtime.ThrowIfWatchdogTerminalAsync();
        await runtime.CompleteAsync();
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
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _pending = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending.Enqueue(completion);
            }

            return completion.Task;
        }

        public void Complete()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                completion = _pending.Dequeue();
            }

            completion.TrySetResult();
        }
    }

    private sealed class ControlledArtifactStorage : ICoverageRunWatchdogArtifactStorage
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task CurrentTask => _completion.Task;

        public bool CommitImmediately { get; set; }

        public int CommitCount { get; private set; }

        public Task<string> WriteTemporaryAsync(
            string destinationPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CommitImmediately ? Task.FromResult("/private/watchdog.tmp") : _completion.Task;
        }

        public void Complete(bool commit)
        {
            _completion.TrySetResult("/private/watchdog.tmp");
        }

        public void Commit(string temporaryPath, string destinationPath) => CommitCount++;

        public void DeleteTemporary(string temporaryPath) { }
    }

    private sealed class BlockingCommitArtifactStorage : ICoverageRunWatchdogArtifactStorage
    {
        public TaskCompletionSource<string> ReleaseStaging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CommitCount { get; private set; }

        public Task<string> WriteTemporaryAsync(
            string destinationPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
            => ReleaseStaging.Task;

        public void Commit(string temporaryPath, string destinationPath)
        {
            CommitStarted.TrySetResult();
            ReleaseCommit.Task.GetAwaiter().GetResult();
            CommitCount++;
        }

        public void DeleteTemporary(string temporaryPath) { }
    }

    private sealed class SynchronouslyBlockingArtifactStorage : ICoverageRunWatchdogArtifactStorage, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(initialState: false);
        public int CommitCount { get; private set; }

        public Task<string> WriteTemporaryAsync(
            string destinationPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            Release.Wait(cancellationToken);
            return Task.FromResult("/private/watchdog.tmp");
        }

        public void Commit(string temporaryPath, string destinationPath) => CommitCount++;

        public void DeleteTemporary(string temporaryPath) => Deleted.TrySetResult();

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
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

    private sealed class BlockingArtifactWriter : ICoverageRunWatchdogArtifactWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CoverageRunWatchdogArtifactWriteResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CoverageRunWatchdogArtifactWriteResult> WriteAsync(
            string destinationPath,
            CoverageRunWatchdogArtifact artifact,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class GateWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Entered.TrySetResult();
            Release.Wait();
            base.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            Release.Set();
            Release.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("simulated console failure");
    }

    private sealed class ThrowingArtifactWriter : ICoverageRunWatchdogArtifactWriter
    {
        public Task<CoverageRunWatchdogArtifactWriteResult> WriteAsync(
            string destinationPath,
            CoverageRunWatchdogArtifact artifact,
            CancellationToken cancellationToken)
            => throw new IOException("simulated artifact failure");
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
