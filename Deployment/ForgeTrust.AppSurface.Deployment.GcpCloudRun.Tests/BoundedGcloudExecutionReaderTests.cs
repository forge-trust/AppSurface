using System.Text;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class BoundedGcloudExecutionReaderTests
{
    [Fact]
    public void ExplicitReaderDependenciesRejectMissingLauncherAndClock()
    {
        // protects=bounded-reader construction invariants; fails_when=missing dependencies reach process setup;
        // no_claim=an installed launcher exists or has provider access.
        Assert.Throws<ArgumentNullException>(() => new BoundedGcloudExecutionReader(null!, TimeProvider.System));
        Assert.Throws<ArgumentException>(() => new BoundedGcloudExecutionReader(" ", TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new BoundedGcloudExecutionReader("gcloud", null!));
    }

    [Fact]
    public async Task NativeExpiredCleanupBudgetCannotPublishCapturedOutput()
    {
        // protects=cleanup cannot renew an exhausted original allowance; fails_when=expired cleanup becomes Complete or Deadline;
        // no_claim=the OS can preempt a process start or tree kill at an exact wall-clock instant.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var slow = NativeFixture.Create("exec sleep 5");
        var clock = new CleanupExpiryClock();
        var result = await new BoundedGcloudExecutionReader(slow.Path, clock)
            .ReadAsync(Resource(), TimeSpan.FromMilliseconds(100));
        Assert.Equal(BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    private sealed class CleanupExpiryClock : TimeProvider
    {
        private int _calls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        // Entry and launch see the original allowance; cleanup sees that allowance consumed.
        public override long GetTimestamp() => Interlocked.Increment(ref _calls) <= 2 ? 0 : TimeSpan.TicksPerSecond;
    }

    [Fact]
    public async Task SettledProcessCannotPublishSuccessAfterTheOriginalDeadline()
    {
        // protects=the public bounded reader's original monotonic deadline after process and streams settle;
        // fails_when=late timer delivery permits Complete with expired evidence; no_claim=OS process-start preemption.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var process = NativeFixture.Create("printf '{}'");
        var result = await new BoundedGcloudExecutionReader(process.Path, new DelayedTimerExpiryClock())
            .ReadAsync(Resource(), TimeSpan.FromMilliseconds(100));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.Deadline, result.Outcome);
        Assert.Empty(result.StandardOutput);
    }

    private sealed class DelayedTimerExpiryClock : TimeProvider
    {
        private int _calls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Increment(ref _calls) <= 2 ? 0 : TimeSpan.TicksPerSecond;
        // Model a timer callback delayed by scheduling while monotonic time has already advanced.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task ReadAsyncRejectsNonCanonicalResourceWithoutLaunching()
    {
        // protects=canonical execution admission before process launch; fails_when=an incomplete resource reaches launch or returns Complete; no_claim=the remote resource exists.
        var reader = new BoundedGcloudExecutionReader("missing-gcloud", TimeProvider.System);

        var result = await reader.ReadAsync("projects/p/locations/r/jobs/j", TimeSpan.FromSeconds(1));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.ReadFailure, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void ResultDefensivelyCopiesOutputBytes()
    {
        // protects=immutable captured stdout at both input and getter boundaries; fails_when=caller mutation changes the stored result; no_claim=provider bytes are semantically valid.
        var bytes = new byte[] { (byte)'{' };
        var result = new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, bytes);

        bytes[0] = (byte)'x';
        var returned = result.StandardOutput;
        returned[0] = (byte)'y';

        Assert.Equal((byte)'{', result.StandardOutput[0]);
    }

    [Fact]
    public void CreateStartInfoUsesFixedExactDescribeArguments()
    {
        // protects=read-only exact gcloud invocation and redirected streams; fails_when=mutation or shell execution tokens enter argv; no_claim=the gcloud process ran.
        var reader = new BoundedGcloudExecutionReader("gcloud", TimeProvider.System);

        Assert.True(ExecutionResource.TryParse("projects/myproj/locations/us-central1/jobs/job/executions/execution", out var resource));
        var startInfo = reader.CreateStartInfo(resource!);

        Assert.Equal("gcloud", startInfo.FileName);
        Assert.Equal(
            ["run", "jobs", "executions", "describe", "projects/myproj/locations/us-central1/jobs/job/executions/execution", "--project", "myproj", "--region", "us-central1", "--format=json", "--quiet"],
            startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void WindowsLauncherContainsOnlyValidatedFixedTokens()
    {
        // protects=cmd launcher argument admission; fails_when=selected identity or flags become shell fragments;
        // no_claim=Windows native process cleanup was exercised on this host.
        Assert.True(ExecutionResource.TryParse(Resource(), out var resource));
        var start = new BoundedGcloudExecutionReader("gcloud.cmd", TimeProvider.System).CreateStartInfo(resource!, windows: true);
        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", start.FileName);
        Assert.Equal(new[] { "/d", "/c", "gcloud.cmd run jobs executions describe " + Resource() + " --project myproj --region us-central1 --format=json --quiet" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput && start.RedirectStandardError);
    }

    [Fact]
    public void ProductionConstructionKeepsTheBoundedPublicContractsAvailable()
    {
        // protects=default consumer construction; fails_when=production factories cannot be instantiated without cloud access;
        // no_claim=the tool is installed or provider credentials work.
        Assert.IsAssignableFrom<IBoundedGcloudExecutionReader>(new BoundedGcloudExecutionReader());
        Assert.IsAssignableFrom<IGcpCloudRunExecutionObserver>(GcpCloudRunExecutionObserver.Create());
    }

    [Fact]
    public async Task ReadAsyncRejectsInvalidTimeoutBeforeProviderAccess()
    {
        // protects=positive finite timeout admission; fails_when=zero budget reaches process setup; no_claim=provider availability.
        var reader = new BoundedGcloudExecutionReader("missing-gcloud", TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            Resource(), TimeSpan.Zero));
    }

    [Fact]
    public async Task ReadAsyncRejectsTimeoutAboveTimerMaximum()
    {
        var reader = new BoundedGcloudExecutionReader("missing-gcloud", TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            Resource(), TimeSpan.FromMilliseconds(4_294_967_295d))); // protects timer admission; fails_when platform timer overflow is attempted; no_claim about gcloud availability.
    }

    [Fact]
    public async Task ReadAsyncTinyPositiveBudgetDoesNotLaunchWhenNoCommandAllowanceRemains()
    {
        var reader = new BoundedGcloudExecutionReader("missing-gcloud", TimeProvider.System);

        var result = await reader.ReadAsync(Resource(), TimeSpan.FromTicks(1));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.Deadline, result.Outcome); // protects no rounded-up launch; fails_when expired budget starts a provider process; no_claim about OS process start preemption.
    }

    [Fact]
    public async Task ReadAsyncMissingToolReturnsSafeUnavailableOutcome()
    {
        var reader = new BoundedGcloudExecutionReader(TestPathUtils.PathUnder(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-gcloud"), TimeProvider.System);

        var result = await reader.ReadAsync(Resource(), TimeSpan.FromSeconds(2));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.ToolUnavailable, result.Outcome); // protects launch classification; fails_when missing executable leaks exception; no_claim about provider auth.
    }

    [Fact]
    public async Task NativeReaderAllowsInclusiveOutputLimitsAndStrictUtf8()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var fixture = NativeFixture.Create("printf '%1048576s' '' | tr ' ' A; printf '{\\\"ok\\\":true}' >&2");

        var result = await new BoundedGcloudExecutionReader(fixture.Path, TimeProvider.System)
            .ReadAsync(Resource(), TimeSpan.FromSeconds(10));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.Complete, result.Outcome); // protects inclusive stdout cap; fails_when exact cap is rejected; no_claim about OS scheduling.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1_048_576, result.StandardOutput.Length);
    }

    [Fact]
    public async Task NativeReaderClassifiesOutputOverflowAndInvalidEncoding()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var overflow = NativeFixture.Create("printf '%1048577s' '' | tr ' ' A");
        using var invalid = NativeFixture.Create("printf '\\377'");

        var overflowResult = await new BoundedGcloudExecutionReader(overflow.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromSeconds(10));
        var invalidResult = await new BoundedGcloudExecutionReader(invalid.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromSeconds(10));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.OutputLimit, overflowResult.Outcome); // protects +1 stdout; fails_when truncation is accepted; no_claim about process-tree universality.
        Assert.Equal(BoundedGcloudExecutionReadOutcome.InvalidEncoding, invalidResult.Outcome); // protects strict UTF-8; fails_when invalid bytes parse; no_claim about provider JSON.
    }

    [Fact]
    public async Task NativeReaderAllowsInclusiveStderrAndRejectsStderrOverflow()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var exact = NativeFixture.Create("printf '%65536s' '' | tr ' ' A >&2");
        using var over = NativeFixture.Create("printf '%65537s' '' | tr ' ' A >&2");

        var exactResult = await new BoundedGcloudExecutionReader(exact.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromSeconds(10));
        var overResult = await new BoundedGcloudExecutionReader(over.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromSeconds(10));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.Complete, exactResult.Outcome); // protects inclusive stderr cap; fails_when exact stderr is rejected; no_claim about discarded stderr content.
        Assert.Equal(BoundedGcloudExecutionReadOutcome.OutputLimit, overResult.Outcome); // protects stderr +1; fails_when stderr is unbounded; no_claim about descendant cleanup on unsupported OS.
    }

    [Fact]
    public async Task NativeReaderClassifiesNonzeroExitAndDeadline()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var failure = NativeFixture.Create("printf '{\\\"error\\\":true}'; exit 7");
        using var slow = NativeFixture.Create("exec sleep 5");

        var failureResult = await new BoundedGcloudExecutionReader(failure.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromSeconds(10));
        var deadlineResult = await new BoundedGcloudExecutionReader(slow.Path, TimeProvider.System).ReadAsync(Resource(), TimeSpan.FromMilliseconds(300));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.NonzeroExit, failureResult.Outcome); // protects settled nonzero classification; fails_when exit is treated as success; no_claim about stderr.
        // protects=no success after deadline; fails_when=expired or unsettled capture is accepted;
        // no_claim=the OS can always finish a tree kill inside the 30 ms reserve of a 300 ms call.
        // Native profiling measured a 34 ms tree kill on Darwin, exceeding that reserve. Both outcomes
        // mean Unknown to the observer; an exhausted cleanup allowance must remain CleanupUnconfirmed.
        Assert.Contains(deadlineResult.Outcome, new[] { BoundedGcloudExecutionReadOutcome.Deadline, BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed });
    }

    [Fact]
    public async Task NativeDeadlineWithSettledCleanupReportsDeadline()
    {
        // protects=deadline classification after confirmed cleanup; fails_when=a killed and settled child is
        // represented as complete or unconfirmed; no_claim=all OS schedules fit a tiny real-time cleanup allowance.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var slow = NativeFixture.Create("exec sleep 5");
        var clock = new SettledCleanupClock();
        var read = new BoundedGcloudExecutionReader(slow.Path, clock)
            .ReadAsync(Resource(), TimeSpan.FromSeconds(2));

        // ReadAsync has started the real child before its first asynchronous wait. Expire only the work
        // timer, leaving the original 200 ms of logical time available for real process and pipe cleanup.
        clock.ExpireWork();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.Deadline, result.Outcome);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(1800), TimeSpan.FromMilliseconds(200) }, clock.TimerDueTimes);
        Assert.Empty(result.StandardOutput);
    }

    /// <summary>Drives the real work cancellation callback while holding the original cleanup reserve open.</summary>
    /// <remarks>
    /// This clock isolates confirmed-cleanup classification from OS scheduling. The unchanged real-clock
    /// deadline, expired-cleanup, cancellation and inherited-pipe tests cover actual cleanup uncertainty.
    /// </remarks>
    private sealed class SettledCleanupClock : TimeProvider
    {
        private readonly List<TimeSpan> _timerDueTimes = [];
        private Action? _expireWork;
        private long _timestamp;

        public IReadOnlyList<TimeSpan> TimerDueTimes => _timerDueTimes;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _timerDueTimes.Add(dueTime);
            if (_timerDueTimes.Count == 1)
            {
                _expireWork = () =>
                {
                    Interlocked.Exchange(ref _timestamp, dueTime.Ticks);
                    callback(state);
                };
            }

            // CTS still owns a disposable timer, but only the test advances this clock and fires work.
            return TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
        }

        public void ExpireWork()
        {
            Assert.NotNull(_expireWork);
            _expireWork();
        }
    }

    [Fact]
    public async Task NativeReaderPropagatesPreCancellation()
    {
        if (OperatingSystem.IsWindows()) return; // protects Unix native fixture; no Windows process claim.
        using var fixture = NativeFixture.Create("sleep 5");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoundedGcloudExecutionReader(fixture.Path, TimeProvider.System)
            .ReadAsync(Resource(), TimeSpan.FromSeconds(3), cancellation.Token)); // protects caller precedence; fails_when cancellation becomes a safe result; no_claim about remote cancellation.
    }

    [Fact]
    public async Task NativeReaderCancellationDuringReadPropagatesAfterBoundedCleanup()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var fixture = NativeFixture.Create("dd if=/dev/zero bs=8192 2>/dev/null");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoundedGcloudExecutionReader(fixture.Path, TimeProvider.System)
            .ReadAsync(Resource(), TimeSpan.FromSeconds(3), cancellation.Token)); // protects mid-read cancellation; fails_when capture continues or returns success; no_claim about killing nonconforming descendants.
    }

    [Fact]
    public async Task NativeReaderDoesNotPublishSuccessWhenRootExitsButDescendantHoldsPipe()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // protects Unix native fixture; no Windows/other OS process claim.
        using var fixture = NativeFixture.Create("(sleep 5) & exit 0");

        var result = await new BoundedGcloudExecutionReader(fixture.Path, TimeProvider.System)
            .ReadAsync(Resource(), TimeSpan.FromMilliseconds(300));

        Assert.NotEqual(BoundedGcloudExecutionReadOutcome.Complete, result.Outcome); // protects root-exit/pipe settlement; fails_when descendant-held pipe becomes success; no_claim about universal process-tree cleanup.
    }

    private static string Resource() => "projects/myproj/locations/us-central1/jobs/job/executions/execution";

    private sealed class NativeFixture : IDisposable
    {
        private NativeFixture(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static NativeFixture Create(string body)
        {
            var path = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "appsurface-gcloud-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n", new UTF8Encoding(false));
            SetExecutableOnSupportedUnix(path);
            return new NativeFixture(path);
        }

        private static void SetExecutableOnSupportedUnix(string path)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
