using System.Text;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class CloudRunExecutionCommandCoverageTests
{
    [Fact]
    public void Constructor_RejectsNullWorkflow()
    {
        // protects=command dependency invariant; fails_when=command can be created without its workflow; no_claim=workflow behavior
        Assert.Throws<ArgumentNullException>(() => new CloudRunExecutionCommand(null!));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessDeliversJsonAndSetsProcessExitCode()
    {
        var original = Environment.ExitCode;
        using var bundle = BundleDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var observer = new Observer(request => new(request.LogicalJobId, request.ExecutionResourceName, CloudRunExecutionState.Succeeded, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183"));
        try
        {
            Environment.ExitCode = 23;
            var command = Command(observer, bundle.Path);
            command.Json = true;
            await command.ExecuteAsync(console);

            // protects=command wiring and process exit decision; fails_when=workflow result is not delivered or ExitCode stays stale; no_claim=provider freshness
            Assert.Equal(0, Environment.ExitCode);
            Assert.Contains("\"state\":\"Succeeded\"", console.ReadOutputString());
            Assert.Equal(1, observer.Calls);
        }
        finally { Environment.ExitCode = original; }
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellationDelivers186And130()
    {
        var original = Environment.ExitCode;
        using var bundle = BundleDirectory.Create();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, stdout, stderr);
        var observer = new Observer(request =>
        {
            console.RequestCancellation(TimeSpan.Zero);
            return new(request.LogicalJobId, request.ExecutionResourceName, CloudRunExecutionState.Succeeded, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183");
        });
        try
        {
            Environment.ExitCode = 23;
            var command = Command(observer, bundle.Path);
            command.Json = true;
            await command.ExecuteAsync(console);

            // protects=console cancellation precedence at command boundary; fails_when=success escapes or exit code is not 130; no_claim=remote execution cancellation
            Assert.Equal(130, Environment.ExitCode);
            Assert.Equal(1, observer.Calls);
            Assert.Contains("ASDEPLOY186", Encoding.UTF8.GetString(stdout.ToArray()), StringComparison.Ordinal);
        }
        finally { Environment.ExitCode = original; }
    }

    private static CloudRunExecutionCommand Command(Observer observer, string path) => new(new CloudRunExecutionWorkflow(observer, TimeProvider.System))
    {
        BundleDirectory = path,
        LogicalJobId = "job",
        ExecutionResourceName = "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1",
    };

    private sealed class Observer(Func<CloudRunExecutionObservationRequest, CloudRunExecutionObservation> result) : IGcpCloudRunExecutionObserver
    {
        public int Calls { get; private set; }
        public Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result(request));
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
            var path = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(TestPathUtils.PathUnder(path, "deployment-intent.v1.json"), "{}");
            File.WriteAllText(TestPathUtils.PathUnder(path, "gcp-cloud-run-migration.tf.json"), "{}");
            File.WriteAllText(TestPathUtils.PathUnder(path, "gcp-cloud-run-migration.plan.json"), "{}");
            File.WriteAllText(TestPathUtils.PathUnder(path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), "gcp-cloud-run\n");
            return new(path);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
