using System.Diagnostics;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionWorkflowTests
{
    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        var observer = new RecordingObserver();
        Assert.Throws<ArgumentNullException>(() => new CloudRunExecutionWorkflow(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CloudRunExecutionWorkflow(observer, null!));
    }

    [Fact]
    public async Task RunAsync_InvalidArgumentsReturn170WithoutObserver()
    {
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);
        var result = await workflow.RunAsync(null, "bad id", "latest", "0s", "2s", CancellationToken.None);

        // protects=local argument admission; fails_when=invalid input reaches provider or exposes raw values; no_claim=provider availability
        Assert.Equal("ASDEPLOY170", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_AllowsOptionalMarkerAndPassesRemainingBudget()
    {
        using var bundle = BundleDirectory.Create(includeMarker: false);
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);

        var result = await workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=optional marker and total budget handoff; fails_when=marker is required or budget resets; no_claim=bundle semantic validity
        Assert.Equal("ASDEPLOY180", result.DiagnosticCode);
        Assert.NotNull(observer.Request);
        Assert.True(observer.Request!.Timeout <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task RunAsync_RejectsUnexpectedEntryWithoutCallingObserver()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true);
        File.WriteAllText(TestPathUtils.PathUnder(bundle.Path, "unexpected.txt"), "x");
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);

        var result = await workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=exact bundle entry set; fails_when=unexpected file reaches provider; no_claim=publisher authenticity
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_RejectsOversizedArtifactBeforeObserver()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true);
        using (var stream = File.OpenWrite(TestPathUtils.PathUnder(bundle.Path, "deployment-intent.v1.json")))
            stream.SetLength(4 * 1024 * 1024 + 1);
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);

        var result = await workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=bounded bundle admission; fails_when=oversized input reaches provider observer; no_claim=filesystem race resistance
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingArtifactBeforeObserver()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true);
        File.Delete(TestPathUtils.PathUnder(bundle.Path, "gcp-cloud-run-migration.plan.json"));
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);

        var result = await workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=complete three-artifact admission; fails_when=partial bundle reaches provider observer; no_claim=publisher authenticity
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_PreCancelledDoesNotReadOrCallObserver()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true);
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // protects=caller cancellation precedence; fails_when=cancelled invocation loads bundle or calls provider; no_claim=remote cancellation
        await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", cancellation.Token));
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_WrongMarkerIsRejectedBeforeObserver()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true, marker: "wrong-target");
        var observer = new RecordingObserver();
        var workflow = new CloudRunExecutionWorkflow(observer, TimeProvider.System);
        var result = await workflow.RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=ownership marker admission; fails_when=foreign bundle is observed; no_claim=publisher authenticity
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
    }

    [Fact]
    public async Task RunAsync_AllowsExactFourMiBArtifactAndRejectsOneByteOver()
    {
        using var exact = BundleDirectory.Create(includeMarker: false, artifactBytes: 4 * 1024 * 1024);
        var exactObserver = new RecordingObserver();
        var exactResult = await new CloudRunExecutionWorkflow(exactObserver, TimeProvider.System).RunAsync(exact.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);
        Assert.NotEqual("ASDEPLOY171", exactResult.DiagnosticCode);
        Assert.NotNull(exactObserver.Request);

        using var over = BundleDirectory.Create(includeMarker: false, artifactBytes: 4 * 1024 * 1024 + 1);
        var overObserver = new RecordingObserver();
        var overResult = await new CloudRunExecutionWorkflow(overObserver, TimeProvider.System).RunAsync(over.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=exact artifact boundary; fails_when=one byte over reaches observer or exact limit is rejected; no_claim=JSON semantics
        Assert.Equal("ASDEPLOY171", overResult.DiagnosticCode);
        Assert.Null(overObserver.Request);
    }

    [Fact]
    public async Task RunAsync_SymlinkedArtifactIsRejected()
    {
        using var bundle = BundleDirectory.Create(includeMarker: true);
        var link = TestPathUtils.PathUnder(bundle.Path, "deployment-intent.v1.json");
        var source = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-link-source-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(source, "{}");
        var observer = new RecordingObserver();
        try
        {
            File.Delete(link);
            File.CreateSymbolicLink(link, source);
            Assert.Equal(4, Directory.EnumerateFileSystemEntries(bundle.Path).Count());
            var result = await new CloudRunExecutionWorkflow(observer, TimeProvider.System).RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

            // protects=artifact reparse-point denial with an otherwise valid entry set; fails_when=symlinked
            // artifact is followed; no_claim=adversarial replacement race or directory-target handling.
            Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
            Assert.Null(observer.Request);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task RunAsync_UnexpectedObserverFailureIsSafe189()
    {
        using var bundle = BundleDirectory.Create(includeMarker: false);
        var observer = new RecordingObserver { Failure = new InvalidOperationException("private detail") };
        var result = await new CloudRunExecutionWorkflow(observer, TimeProvider.System).RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", CancellationToken.None);

        // protects=unexpected observer failure redaction; fails_when=exception text or success escapes; no_claim=provider diagnosis
        Assert.Equal("ASDEPLOY189", result.DiagnosticCode);
        Assert.DoesNotContain("private", result.DiagnosticCode);
    }

    [Fact]
    public async Task RunAsync_ObserverCallerCancellationPropagates()
    {
        using var bundle = BundleDirectory.Create(includeMarker: false);
        using var cancellation = new CancellationTokenSource();
        var observer = new RecordingObserver { CancelBeforeReturn = cancellation };

        // protects=caller cancellation precedence; fails_when=valid result is delivered after caller cancellation; no_claim=remote cancellation
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CloudRunExecutionWorkflow(observer, TimeProvider.System).RunAsync(bundle.Path, "job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "60s", "2s", cancellation.Token));
    }

    [Theory]
    [InlineData("deployment-intent.v1.json")]
    [InlineData("gcp-cloud-run-migration.tf.json")]
    [InlineData("gcp-cloud-run-migration.plan.json")]
    [InlineData(DeploymentArtifactBundleWriter.OwnershipMarkerFileName)]
    public async Task RunAsync_RejectsFifoWithoutBlocking(string filename)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var bundle = BundleDirectory.Create(includeMarker: true);
        var candidate = TestPathUtils.PathUnder(bundle.Path, filename);
        File.Delete(candidate);
        Assert.Equal(0, MakeFifo(candidate, Convert.ToUInt32("600", 8)));
        var releaseBlockedReader = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            using var handle = File.Open(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        });
        var observer = new RecordingObserver();
        var stopwatch = Stopwatch.StartNew();

        var result = await new CloudRunExecutionWorkflow(observer, TimeProvider.System).RunAsync(
            bundle.Path,
            "job",
            "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1",
            "60s",
            "2s",
            CancellationToken.None);

        stopwatch.Stop();
        await releaseBlockedReader;

        // protects=bounded no-follow regular-file admission for each bundle input; fails_when=FIFO blocks to the release writer, returns another diagnostic, or calls observer; no_claim=protection from concurrent path replacement
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Null(observer.Request);
        // gstack-timing-ok: FIFO rejection must finish before the independently delayed 1s opener; this bound detects blocking open, not throughput.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(750), $"FIFO rejection took {stopwatch.Elapsed}.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed class RecordingObserver : IGcpCloudRunExecutionObserver
    {
        public CloudRunExecutionObservationRequest? Request { get; private set; }
        public Exception? Failure { get; init; }
        public CancellationTokenSource? CancelBeforeReturn { get; init; }
        public Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Failure is not null) throw Failure;
            CancelBeforeReturn?.Cancel();
            return Task.FromResult(new CloudRunExecutionObservation(request.LogicalJobId, request.ExecutionResourceName, CloudRunExecutionState.Unknown, false, DateTimeOffset.UtcNow, "ASDEPLOY180"));
        }
    }

    private sealed class BundleDirectory : IDisposable
    {
        private BundleDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public static BundleDirectory Create(bool includeMarker, string marker = "gcp-cloud-run", int artifactBytes = 2)
        {
            var path = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-execution-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            foreach (var name in new[] { "deployment-intent.v1.json", "gcp-cloud-run-migration.tf.json", "gcp-cloud-run-migration.plan.json" })
                File.WriteAllBytes(TestPathUtils.PathUnder(path, name), new byte[artifactBytes]);
            if (includeMarker) File.WriteAllText(TestPathUtils.PathUnder(path, DeploymentArtifactBundleWriter.OwnershipMarkerFileName), marker + "\n");
            return new BundleDirectory(path);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
