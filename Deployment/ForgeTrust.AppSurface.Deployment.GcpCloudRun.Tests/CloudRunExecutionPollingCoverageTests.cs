using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class CloudRunExecutionObserverTests
{
    [Fact]
    public async Task EvidenceReceiptCannotRenewBudgetBeforeClassificationReturns()
    {
        // protects=original monotonic allowance across evidence receipt and classification;
        // fails_when=a late terminal response escapes as success; no_claim=provider execution timing.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.ExecutionJson()), new ReceiptExpiryClock())
            .ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(1)));
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
    }

    [Fact]
    public async Task BudgetEndingAfterRunningClassificationReturnsThatReceiptWithoutDelayOrRead()
    {
        // protects=clean idle boundary after a matched Running classification;
        // fails_when=the observer delays or reads beyond its original budget; no_claim=remote state at return.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.RunningJson());
        var result = await new GcpCloudRunExecutionObserver(reader, new BoundaryClock())
            .ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(1)));
        Assert.Equal(CloudRunExecutionState.Running, result.State);
        Assert.Equal("ASDEPLOY182", result.DiagnosticCode);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.ObservedAt);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task UnsupportedReadAfterRunningCannotReusePriorUidAsProof()
    {
        // protects=every poll supplies fresh complete identity and configuration evidence;
        // fails_when=previous matching evidence masks an unsupported next response; no_claim=last remote state persists.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.RunningJson(), "{}");
        var result = await new GcpCloudRunExecutionObserver(reader, new DelayCompletingClock())
            .ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromTicks(1)));
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
        Assert.Equal(2, reader.Calls);
    }

    private sealed class ReceiptExpiryClock : TimeProvider
    {
        private bool _received;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _received ? TimeSpan.TicksPerSecond : 0;
        public override DateTimeOffset GetUtcNow() { _received = true; return DateTimeOffset.UnixEpoch; }
    }

    private sealed class BoundaryClock : TimeProvider
    {
        private int _reads;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Increment(ref _reads) >= 5 ? TimeSpan.TicksPerSecond : 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    [Theory]
    [InlineData(1L, 1L, true)]
    [InlineData(0L, 1L, false)]
    [InlineData(1L, 0L, false)]
    [InlineData(-1L, 1L, false)]
    [InlineData(1L, -1L, false)]
    [InlineData(1L, 2L, false)]
    [InlineData(42949672940000L, 1L, true)]
    [InlineData(42949672940001L, 1L, false)]
    public void ObservationDurationBoundaryRequiresPositiveOrderedTimerRepresentableTicks(long timeoutTicks, long intervalTicks, bool expected)
    {
        // protects=finite positive ordered timer admission including the inclusive maximum;
        // fails_when=tiny valid ticks are rounded up or out-of-contract timers are admitted; no_claim=OS scheduling accuracy.
        Assert.Equal(expected, ExecutionResource.ValidDurations(TimeSpan.FromTicks(timeoutTicks), TimeSpan.FromTicks(intervalTicks)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("projects/9223372036854775808/locations/us-central1/jobs/job/executions/run")]
    [InlineData("projects/demo-project/locations/us-central1/jobs/job/executions/run/extra")]
    public void ExecutionResourceParserRejectsMissingOverflowAndNonCanonicalIdentity(string? value)
    {
        // protects=canonical full resource and signed-64-bit numeric namespace admission;
        // fails_when=missing, overflowing, or suffixed identities survive parsing; no_claim=provider existence.
        Assert.False(ExecutionResource.TryParse(value, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void ObservationCopyKeepsTheOriginalSelectedIdentityImmutable()
    {
        // protects=public value contract for independently copied observations; fails_when=copying changes the original identities;
        // no_claim=constructing an observation establishes provider evidence.
        var original = new CloudRunExecutionObservation("first", "first-resource", CloudRunExecutionState.Unknown, false, DateTimeOffset.UnixEpoch, "ASDEPLOY170");
        var copy = original with { LogicalJobId = "second", ExecutionResourceName = "second-resource" };
        Assert.Equal("first", original.LogicalJobId);
        Assert.Equal("first-resource", original.ExecutionResourceName);
        Assert.Equal("second", copy.LogicalJobId);
        Assert.Equal("second-resource", copy.ExecutionResourceName);
    }

    [Theory]
    [InlineData("projects/demo-project/locations/us-central1/jobs/schema-preparation/executions/schema-preparation-run\n", "schema-preparation")]
    [InlineData("projects/demo-project/locations/us-central1/jobs/schema-preparation/executions/schema-preparation-run", "schema-preparation\n")]
    public async Task CanonicalRequestRejectsTrailingNewlineWithoutProviderAccess(string execution, string logicalId)
    {
        // protects=complete canonical identity admission; fails_when=regex end-of-line admits trailing newline;
        // no_claim=syntactically valid identity proves provider existence.
        using var fixture = new ObserverFixture();
        var reader = new NullResultReader();
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(
            fixture.Request() with { ExecutionResourceName = execution, LogicalJobId = logicalId });
        Assert.Equal("ASDEPLOY170", result.DiagnosticCode);
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(0, reader.Calls);
    }

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
