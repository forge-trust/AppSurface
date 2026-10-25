using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionWorkflowCoverageTests
{
    [Theory]
    [InlineData(CloudRunExecutionState.Running, "ASDEPLOY182")]
    [InlineData(CloudRunExecutionState.Succeeded, "ASDEPLOY181")]
    public async Task RunAsync_AtDeadlineKeepsRunningVisibleButHidesLateTerminalState(CloudRunExecutionState state, string expectedCode)
    {
        using var bundle = BundleDirectory.Create();
        var clock = new ManualClock();
        var observer = new Observer((request, _) =>
        {
            clock.Elapsed = request.Timeout;
            return Task.FromResult(new CloudRunExecutionObservation("job", request.ExecutionResourceName, state, true, DateTimeOffset.UnixEpoch, state == CloudRunExecutionState.Running ? "ASDEPLOY182" : "ASDEPLOY183"));
        });
        var result = await Workflow(observer, clock).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=deadline semantics and avoids late terminal success; fails_when=Running is suppressed or late success is accepted; no_claim=observation cadence
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.Equal(state == CloudRunExecutionState.Running, result.Observation is not null);
        Assert.Equal(TimeSpan.FromSeconds(10), observer.Request!.Timeout);
    }

    [Fact]
    public async Task RunAsync_MapsDeploymentValidationDiagnosticWithoutProviderText()
    {
        using var bundle = BundleDirectory.Create();
        var exception = new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY177", "safe problem", "safe cause", "safe fix"));
        var observer = new Observer((_, _) => throw exception);
        var result = await Workflow(observer, new ManualClock()).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=stable deployment diagnostic propagation; fails_when=validation code is replaced or exception detail escapes; no_claim=diagnostic correctness
        Assert.Equal("ASDEPLOY177", result.DiagnosticCode);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task RunAsync_ObserverCancellationWithoutCallerCancellationBecomesUnknown()
    {
        using var bundle = BundleDirectory.Create();
        var observer = new Observer((_, _) => throw new OperationCanceledException("provider detail"));
        var result = await Workflow(observer, new ManualClock()).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=internal timeout/cancel normalization; fails_when=observer cancellation escapes or leaks details; no_claim=provider status
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task RunAsync_ClampsPollIntervalToRemainingBundleBudget()
    {
        using var bundle = BundleDirectory.Create();
        var observer = new Observer((request, _) => Task.FromResult(new CloudRunExecutionObservation("job", request.ExecutionResourceName, CloudRunExecutionState.Running, true, DateTimeOffset.UnixEpoch, "ASDEPLOY182")));
        var clock = new SetOnCallClock(13, TimeSpan.FromSeconds(9));
        var result = await Workflow(observer, clock).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=remaining total deadline is forwarded as a bounded poll cadence after local loading; fails_when=poll interval exceeds remaining timeout; no_claim=provider polling cadence
        Assert.Equal("ASDEPLOY182", result.DiagnosticCode);
        Assert.Equal(TimeSpan.FromSeconds(1), observer.Request!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), observer.Request.PollInterval);
    }

    [Fact]
    public async Task RunAsync_LocalBundleReadBudgetExpiryBecomesUnknown()
    {
        using var bundle = BundleDirectory.Create();
        var observer = new Observer((_, _) => throw new InvalidOperationException("observer must not run"));
        var clock = new SetOnCallClock(2, TimeSpan.FromSeconds(10));
        var result = await Workflow(observer, clock).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=local bundle work shares the finite observation deadline; fails_when=timeout escapes or is reported as bundle corruption; no_claim=filesystem throughput
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
        Assert.Null(result.Observation);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_RejectsOwnershipMarkerAtMaximumReadSize()
    {
        using var bundle = BundleDirectory.Create();
        File.WriteAllText(TestPathUtils.PathUnder(bundle.Path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), new string('x', 128));
        var observer = new Observer((_, _) => throw new InvalidOperationException("invalid marker must not reach observer"));
        var result = await Workflow(observer, new ManualClock()).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=marker read bound rejects a full 128-byte buffer without decoding it; fails_when=full marker is accepted or provider observes; no_claim=marker contents beyond the read limit
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_NonexistentBundleDirectoryIsLocalFailure()
    {
        var path = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-missing-bundle-" + Guid.NewGuid().ToString("N"));
        var observer = new Observer((_, _) => throw new InvalidOperationException("missing bundle must not reach observer"));
        var result = await Workflow(observer, new ManualClock()).RunAsync(path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=missing local bundle detection; fails_when=observer runs or absent bundle is classified as valid; no_claim=path existence after this invocation
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_SymlinkedBundleDirectoryIsRejected()
    {
        using var bundle = BundleDirectory.Create();
        var link = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-bundle-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, bundle.Path);
        var observer = new Observer((_, _) => throw new InvalidOperationException("symlinked bundle must not reach observer"));
        try
        {
            var result = await Workflow(observer, new ManualClock()).RunAsync(link, "job", Execution, "10s", "2s", CancellationToken.None);

            // protects=leaf bundle directory reparse-point admission; fails_when=workflow traverses a linked bundle root; no_claim=concurrent path replacement
            Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
            Assert.Null(observer.Request);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task RunAsync_SymlinkedOwnershipMarkerIsRejected()
    {
        using var bundle = BundleDirectory.Create();
        var markerPath = TestPathUtils.PathUnder(bundle.Path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName);
        File.Delete(markerPath);
        File.CreateSymbolicLink(markerPath, TestPathUtils.PathUnder(bundle.Path, "deployment-intent.v1.json"));
        var observer = new Observer((_, _) => throw new InvalidOperationException("symlinked marker must not reach observer"));
        var result = await Workflow(observer, new ManualClock()).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=ownership marker reparse-point rejection; fails_when=workflow opens the marker target; no_claim=publisher identity
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_LocalBundleFinishingAtDeadlineDoesNotStartObserver()
    {
        using var bundle = BundleDirectory.Create();
        File.Delete(TestPathUtils.PathUnder(bundle.Path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName));
        var observer = new Observer((_, _) => throw new InvalidOperationException("expired budget must not reach observer"));
        var clock = new SetOnCallClock(16, TimeSpan.FromSeconds(10));
        var result = await Workflow(observer, clock).RunAsync(bundle.Path, "job", Execution, "10s", "2s", CancellationToken.None);

        // protects=deadline reached after local loading but before observation; fails_when=observer starts with no remaining allowance; no_claim=filesystem timing
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    private const string Execution = "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1";
    private static CloudRunExecutionWorkflow Workflow(Observer observer, TimeProvider clock) => new(observer, clock);

    private sealed class Observer(Func<CloudRunExecutionObservationRequest, CancellationToken, Task<CloudRunExecutionObservation>> operation) : IGcpCloudRunExecutionObserver
    {
        public CloudRunExecutionObservationRequest? Request { get; private set; }
        public Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return operation(request, cancellationToken);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private sealed class SetOnCallClock(int call, TimeSpan elapsed) : TimeProvider
    {
        private int _calls;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _calls) == call) _ticks = elapsed.Ticks;
            return _ticks;
        }
    }

    private sealed class BundleDirectory : IDisposable
    {
        private BundleDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public static BundleDirectory Create()
        {
            var path = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            foreach (var name in new[] { "deployment-intent.v1.json", "gcp-cloud-run-migration.tf.json", "gcp-cloud-run-migration.plan.json" })
                File.WriteAllText(TestPathUtils.PathUnder(path, name), "{}");
            File.WriteAllText(TestPathUtils.PathUnder(path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), "gcp-cloud-run\n");
            return new(path);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
