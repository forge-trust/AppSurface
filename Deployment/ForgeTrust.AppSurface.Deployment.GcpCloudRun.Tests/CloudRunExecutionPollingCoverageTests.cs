using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class CloudRunExecutionObserverTests
{
    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        // protects=observer dependency contract; fails_when=null collaborator survives construction; no_claim=provider invocation.
        Assert.Throws<ArgumentNullException>(() => new GcpCloudRunExecutionObserver(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new GcpCloudRunExecutionObserver(new NullResultReader(), null!));
    }

    [Fact]
    public async Task NullRequestThrowsBeforeProviderWork()
    {
        // protects=public observer null-request boundary; fails_when=reader is called with no request; no_claim=valid request behavior.
        var reader = new NullResultReader();
        await Assert.ThrowsAsync<ArgumentNullException>(() => new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(null!));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("missing-logical", "ASDEPLOY172")]
    [InlineData("wrong-scope", "ASDEPLOY172")]
    public async Task NonMatchingBundleBindingReturnsUnknownWithoutReaderWork(string mode, string code)
    {
        // protects=exact declared Job and execution scope binding; fails_when=mismatched identity dispatches gcloud; no_claim=remote execution state.
        using var fixture = new ObserverFixture();
        var request = fixture.Request() with
        {
            LogicalJobId = mode == "missing-logical" ? "other-job" : "schema-preparation",
            ExecutionResourceName = mode == "wrong-scope"
                ? "projects/demo-project/locations/europe-west1/jobs/schema-preparation/executions/schema-preparation-run"
                : fixture.Request().ExecutionResourceName
        };
        var reader = new NullResultReader();
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(request);
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task InvalidNonNullBundleReturnsUnknownWithoutReaderWork()
    {
        // protects=non-null bundle semantic validation; fails_when=empty artifact set reaches reader; no_claim=publisher authenticity.
        using var fixture = new ObserverFixture();
        var request = fixture.Request() with { Deployment = new DeploymentRenderResult("gcp-cloud-run", []) };
        var reader = new NullResultReader();
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(request);
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY171", result.DiagnosticCode);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task NullReaderResultMapsToSafeReaderFailure()
    {
        // protects=reader contract defense at observer boundary; fails_when=null result causes dereference or success; no_claim=reader implementation quality.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new NullResultReader(), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY179", result.DiagnosticCode);
    }

    [Fact]
    public async Task ReaderOperationCancellationWithoutCallerCancellationMapsToDeadline()
    {
        // protects=observer-owned budget cancellation classification; fails_when=internal cancellation escapes as caller cancellation; no_claim=caller cancellation behavior.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new InternalCancellationReader(), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
    }

    [Fact]
    public async Task ReaderGenericFailureMapsToSafeFailure()
    {
        // protects=reader exception redaction; fails_when=provider exception details escape observer result; no_claim=provider diagnosis.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new ThrowingReader(), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY179", result.DiagnosticCode);
    }

    private sealed class NullResultReader : IBoundedGcloudExecutionReader
    {
        public int Calls { get; private set; }
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<BoundedGcloudExecutionReadResult>(null!);
        }
    }

    private sealed class InternalCancellationReader : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<BoundedGcloudExecutionReadResult>(new CancellationToken(canceled: true));
    }

    private sealed class ThrowingReader : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic reader failure");
    }
}
