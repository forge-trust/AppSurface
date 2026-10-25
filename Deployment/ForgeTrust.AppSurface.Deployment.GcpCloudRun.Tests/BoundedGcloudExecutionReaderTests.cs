using System.Text;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class BoundedGcloudExecutionReaderTests
{
    [Fact]
    public async Task ReadAsyncRejectsNonCanonicalResourceWithoutLaunching()
    {
        var reader = new BoundedGcloudExecutionReader("missing-gcloud", TimeProvider.System);

        var result = await reader.ReadAsync("projects/p/locations/r/jobs/j", TimeSpan.FromSeconds(1));

        Assert.Equal(BoundedGcloudExecutionReadOutcome.ReadFailure, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void ResultDefensivelyCopiesOutputBytes()
    {
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
    public async Task ReadAsyncRejectsInvalidTimeoutBeforeProviderAccess()
    {
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
        var reader = new BoundedGcloudExecutionReader(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-gcloud"), TimeProvider.System);

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
        // represented as complete; no_claim=all OS schedules fit a tiny cleanup allowance.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var slow = NativeFixture.Create("exec sleep 5");
        var result = await new BoundedGcloudExecutionReader(slow.Path, TimeProvider.System)
            .ReadAsync(Resource(), TimeSpan.FromSeconds(2));
        Assert.Equal(BoundedGcloudExecutionReadOutcome.Deadline, result.Outcome);
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appsurface-gcloud-" + Guid.NewGuid().ToString("N"));
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
