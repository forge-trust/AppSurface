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

    private const string Execution = "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1";
    private static CloudRunExecutionWorkflow Workflow(Observer observer, ManualClock clock) => new(observer, clock);

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
