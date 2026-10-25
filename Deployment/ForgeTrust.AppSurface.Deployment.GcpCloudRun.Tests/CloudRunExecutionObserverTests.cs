using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class CloudRunExecutionObserverTests
{
    [Fact]
    public async Task CleanIdleDeadlineRetainsLastRunningReceiptAndDoesNotReadAgain()
    {
        // protects=clean idle deadline semantics; fails_when=the observer dispatches past its allowance or
        // replaces receipt time with the deadline; no_claim=the remote execution is still running at return.
        using var fixture = new ObserverFixture();
        var clock = new DelayCompletingClock();
        var reader = new StubReader(fixture.RunningJson());
        var result = await new GcpCloudRunExecutionObserver(reader, clock).ObserveAsync(
            fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromSeconds(1)));
        Assert.Equal(CloudRunExecutionState.Running, result.State);
        Assert.True(result.ConfigurationMatched);
        Assert.Equal("ASDEPLOY182", result.DiagnosticCode);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.ObservedAt);
        Assert.Equal(1, reader.Calls);
    }

    private sealed class DelayCompletingClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _ticks, dueTime.Ticks);
            var timer = new CompletedTimer();
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return timer;
        }
        private sealed class CompletedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task MatchingV1ExecutionReturnsSucceededAndUsesOneRead()
    {
        // protects=exact execution configuration and terminal success; fails_when=matching evidence is not accepted; no_claim=provider identity is authenticated.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson());
        var observer = new GcpCloudRunExecutionObserver(reader, TimeProvider.System);

        var result = await observer.ObserveAsync(fixture.Request());

        Assert.True(result.State == CloudRunExecutionState.Succeeded, $"diagnostic={result.DiagnosticCode}");
        Assert.True(result.ConfigurationMatched);
        Assert.Equal("ASDEPLOY183", result.DiagnosticCode);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task ChangedUidIsUnknownAndStopsPolling()
    {
        // protects=UID continuity; fails_when=a later execution identity is promoted; no_claim=continuity before first provider response.
        using var fixture = new ObserverFixture();
        var first = fixture.RunningJson();
        var second = first.Replace("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", StringComparison.Ordinal);
        var reader = new StubReader(first, second);
        var observer = new GcpCloudRunExecutionObserver(reader, TimeProvider.System);

        var result = await observer.ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(2), interval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY173", result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Fact]
    public async Task InvalidRequestDoesNotCallProvider()
    {
        // protects=local validation and zero provider calls; fails_when=malformed identity reaches the reader; no_claim=filesystem inputs are authenticated.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson());
        var observer = new GcpCloudRunExecutionObserver(reader, TimeProvider.System);
        var request = fixture.Request() with { ExecutionResourceName = "latest" };

        var result = await observer.ObserveAsync(request);

        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY170", result.DiagnosticCode);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task CallerCancellationWinsBeforeProviderRead()
    {
        // protects=caller cancellation precedence; fails_when=deadline or provider failure replaces cancellation; no_claim=remote execution is cancelled.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson());
        var observer = new GcpCloudRunExecutionObserver(reader, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => observer.ObserveAsync(fixture.Request(), cancellation.Token));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(BoundedGcloudExecutionReadOutcome.ToolUnavailable, "ASDEPLOY178")]
    [InlineData(BoundedGcloudExecutionReadOutcome.OutputLimit, "ASDEPLOY180")]
    [InlineData(BoundedGcloudExecutionReadOutcome.InvalidEncoding, "ASDEPLOY180")]
    [InlineData(BoundedGcloudExecutionReadOutcome.Deadline, "ASDEPLOY181")]
    [InlineData(BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed, "ASDEPLOY187")]
    [InlineData(BoundedGcloudExecutionReadOutcome.ReadFailure, "ASDEPLOY179")]
    [InlineData(BoundedGcloudExecutionReadOutcome.NonzeroExit, "ASDEPLOY179")]
    public async Task ReaderFailureOutcomesMapToSafeDiagnostics(BoundedGcloudExecutionReadOutcome outcome, string code)
    {
        // protects=reader failure classification; fails_when=raw process failure leaks or becomes success; no_claim=child cleanup succeeded.
        using var fixture = new ObserverFixture();
        var reader = new OutcomeReader(outcome);
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());

        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public async Task InvalidDurationsReturnUnknownWithoutProviderWork(int timeoutSeconds, int intervalSeconds)
    {
        // protects=finite ordered duration admission; fails_when=zero, negative, or oversized intervals dispatch; no_claim=clock precision rounds tiny budgets.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson());
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(
            fixture.Request(TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(intervalSeconds)));

        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY170", result.DiagnosticCode);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("\"run.googleapis.com/job\":\"schema-preparation\"", "\"run.googleapis.com/job\":\"other-job\"", "ASDEPLOY172")]
    [InlineData("\"cloud.googleapis.com/location\":\"us-central1\"", "\"cloud.googleapis.com/location\":\"europe-west1\"", "ASDEPLOY172")]
    [InlineData("\"conditions\":[{\"type\":\"Completed\",\"status\":\"True\",\"reason\":\"Completed\"}]", "\"conditions\":[{\"type\":\"Other\",\"status\":\"True\"}]", "ASDEPLOY174")]
    public async Task IdentityAndCompletionMutationsDoNotPass(string original, string mutation, string code)
    {
        // protects=parent/region/overall-condition evidence; fails_when=swapped or incomplete evidence is accepted; no_claim=provider aliases are resolved.
        using var fixture = new ObserverFixture();
        var json = fixture.ExecutionJson().Replace(original, mutation, StringComparison.Ordinal);
        var reader = new StubReader(json);
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());

        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
    }

    [Fact]
    public async Task ConcurrentObservationsDoNotShareUidOrReads()
    {
        // protects=request isolation; fails_when=one request's evidence changes another result; no_claim=provider execution ordering.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson(), fixture.ExecutionJson());
        var observer = new GcpCloudRunExecutionObserver(reader, TimeProvider.System);
        var results = await Task.WhenAll(observer.ObserveAsync(fixture.Request()), observer.ObserveAsync(fixture.Request()));

        Assert.All(results, result => Assert.Equal(CloudRunExecutionState.Succeeded, result.State));
        Assert.Equal(2, reader.Calls);
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("\"args\":[\"migrate\"]", "\"args\":[\"rollback\"]")]
    [InlineData("\"name\":\"ConnectionStrings__database\"", "\"name\":\"ConnectionStrings__other\"")]
    [InlineData("demo-project:us-central1:primary", "demo-project:us-central1:other")]
    [InlineData("projects/demo-project/global/networks/main", "projects/other-project/global/networks/main")]
    [InlineData("migration@demo-project.iam.gserviceaccount.com", "other@demo-project.iam.gserviceaccount.com")]
    [InlineData("\"taskCount\":1", "\"taskCount\":2")]
    [InlineData("\"maxRetries\":3", "\"maxRetries\":4")]
    [InlineData("\"timeoutSeconds\":600", "\"timeoutSeconds\":601")]
    public async Task ConfigurationInventoryMutationsCannotPass(string original, string replacement)
    {
        // protects=complete captured configuration parity; fails_when=any image/args/env/SQL/VPC/identity/count/retry/timeout mutation passes; no_claim=configuration proves workload acceptance.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson().Replace(original, replacement, StringComparison.Ordinal));
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY177", result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("\"succeededCount\":1", "\"succeededCount\":2")]
    [InlineData("\"failedCount\":0", "\"failedCount\":1")]
    [InlineData("\"cancelledCount\":0", "\"cancelledCount\":1")]
    public async Task ContradictoryTerminalCountersBecomeUnknown(string original, string replacement)
    {
        // protects=terminal counter consistency; fails_when=contradictory success evidence is classified terminal; no_claim=provider task logs are inspected.
        using var fixture = new ObserverFixture();
        var reader = new StubReader(fixture.ExecutionJson().Replace(original, replacement, StringComparison.Ordinal));
        var result = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY174", result.DiagnosticCode);
    }

    [Fact]
    public async Task ExplicitCloudSqlMountIsRejectedByV1ImplicitProjection()
    {
        // protects=v1 closed Cloud SQL inventory; fails_when=an explicit volume/mount is accepted beside the annotation; no_claim=socket connectivity is proven.
        using var fixture = new ObserverFixture();
        var json = fixture.ExecutionJson().Replace("\"env\":[", "\"volumeMounts\":[{\"name\":\"cloudsql\",\"mountPath\":\"/cloudsql\"}],\"env\":[", StringComparison.Ordinal);
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Fact]
    public async Task MatchingV2ExecutionUsesTypedCloudSqlAndVpcProjection()
    {
        // protects=v2 typed execution normalization; fails_when=the complete typed shape is rejected; no_claim=the response proves database connectivity.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.V2ExecutionJson()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.True(result.State == CloudRunExecutionState.Succeeded, $"diagnostic={result.DiagnosticCode}");
        Assert.True(result.ConfigurationMatched);
        Assert.Equal("ASDEPLOY183", result.DiagnosticCode);
    }

    [Theory]
    [InlineData("\"egress\":\"ALL_TRAFFIC\"", "\"egress_missing\":true")]
    [InlineData("\"reconciling\":false", "\"reconciling_missing\":true")]
    public async Task V2RequiredOmissionProofCannotBeInvented(string original, string replacement)
    {
        // protects=explicit v2 egress and reconciliation evidence; fails_when=documented defaults are silently fabricated; no_claim=provider omission semantics.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.V2ExecutionJson().Replace(original, replacement, StringComparison.Ordinal)), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Fact]
    public async Task ProviderReadCannotObserveMutationOfCallerArtifactList()
    {
        // protects=immutable request snapshot; fails_when=provider await observes caller artifact mutation; no_claim=bundle authenticity.
        using var fixture = new ObserverFixture();
        var rendered = fixture.Rendered();
        var artifacts = rendered.Artifacts.ToList();
        var request = new CloudRunExecutionObservationRequest(new DeploymentRenderResult(rendered.Target, artifacts), "schema-preparation", $"projects/{fixture.ProjectName}/locations/{fixture.RegionName}/jobs/{fixture.JobName}/executions/{fixture.ExecutionName}", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));
        var result = await new GcpCloudRunExecutionObserver(new MutatingReader(artifacts, fixture.ExecutionJson()), TimeProvider.System).ObserveAsync(request);
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Fact]
    public async Task ReadFailureAfterRunningDoesNotReturnStaleRunning()
    {
        // protects=provider failure precedence; fails_when=earlier Running evidence is promoted; no_claim=remote execution stopped.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.RunningJson(), "__FAIL__"), TimeProvider.System).ObserveAsync(fixture.Request(interval: TimeSpan.FromMilliseconds(1)));
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY179", result.DiagnosticCode);
    }

    [Fact]
    public async Task ReaderCancellationPropagatesCallerCancellation()
    {
        // protects=caller token precedence during provider read; fails_when=cancellation becomes provider Unknown; no_claim=remote cancellation.
        using var fixture = new ObserverFixture();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new GcpCloudRunExecutionObserver(new CancellingReader(cancellation), TimeProvider.System).ObserveAsync(fixture.Request(), cancellation.Token));
    }

    [Fact]
    public async Task ProviderReadAdvancingMonotonicClockReturnsUnknownDeadline()
    {
        // protects=single monotonic budget; fails_when=provider evidence beyond the deadline is classified terminal; no_claim=blocked process preemption.
        using var fixture = new ObserverFixture();
        var clock = new AdvancingTimeProvider();
        var result = await new GcpCloudRunExecutionObserver(new AdvancingReader(clock, fixture.ExecutionJson()), clock).ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(1)));
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY181", result.DiagnosticCode);
    }

    [Theory]
    [InlineData("\"maxRetries\":3,", "", "ASDEPLOY174")]
    [InlineData("\"key\":\"latest\"", "\"key\":\"1\"", "ASDEPLOY177")]
    [InlineData("\"containers\":[{", "\"containers\":[{\"image\":\"extra\"},{", "ASDEPLOY175")]
    public async Task RequiredV1EvidenceCannotBeOmittedOrExpanded(string original, string replacement, string code)
    {
        // protects=required v1 fields and closed inventory; fails_when=missing retries, wrong secret version, or extra container passes; no_claim=provider defaults are inferred.
        using var fixture = new ObserverFixture();
        var json = fixture.ExecutionJson().Replace(original, replacement, StringComparison.Ordinal);
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
    }

    [Theory]
    [InlineData("failed", "NonZeroExitCode", "False", "ASDEPLOY184")]
    [InlineData("cancelled", "Cancelled", "False", "ASDEPLOY185")]
    public async Task RecognizedTerminalFailureAndCancellationStatesAreReturned(string mode, string reason, string condition, string code)
    {
        // protects=terminal workload-state classification; fails_when=recognized failure/cancellation becomes Unknown or success; no_claim=caller recovery is automatic.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.StateJson(mode, reason, condition)), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(code, result.DiagnosticCode);
        Assert.Equal(mode == "failed" ? CloudRunExecutionState.Failed : CloudRunExecutionState.Cancelled, result.State);
    }

    [Theory]
    [InlineData("runningCount")]
    [InlineData("succeededCount")]
    [InlineData("failedCount")]
    [InlineData("cancelledCount")]
    [InlineData("retriedCount")]
    public async Task V1OmittedZeroCountersMatchExplicitZero(string counter)
    {
        // protects=approved v1 omitted-zero semantics; fails_when=omission changes classification; no_claim=provider defaults are universally valid.
        using var fixture = new ObserverFixture();
        var baseline = counter == "succeededCount" ? fixture.StateJson("failed", "NonZeroExitCode", "False") : fixture.ExecutionJson();
        var omitted = RemoveStatusProperty(baseline, counter);
        var explicitResult = await new GcpCloudRunExecutionObserver(new StubReader(baseline), TimeProvider.System).ObserveAsync(fixture.Request());
        var omittedResult = await new GcpCloudRunExecutionObserver(new StubReader(omitted), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(explicitResult.State, omittedResult.State);
        Assert.Equal(explicitResult.DiagnosticCode, omittedResult.DiagnosticCode);
    }

    [Theory]
    [InlineData("runningCount")]
    [InlineData("succeededCount")]
    [InlineData("failedCount")]
    [InlineData("cancelledCount")]
    [InlineData("retriedCount")]
    public async Task V2MissingCountersAreInsufficientEvidence(string counter)
    {
        // protects=v2 required counter evidence; fails_when=missing fields are fabricated as zero; no_claim=omitted counters are provider defaults.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(RemoveRootProperty(fixture.V2ExecutionJson(), counter)), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Theory]
    [InlineData("MysteryState", "ASDEPLOY175")]
    [InlineData("CANCELLING", "ASDEPLOY182")]
    public async Task V1ReasonBoundaryKeepsUnknownReasonsOutOfRunning(string reason, string code)
    {
        // protects=recognized nonterminal reason table; fails_when=arbitrary provider text becomes Running; no_claim=reason proves workload health.
        using var fixture = new ObserverFixture();
        var json = fixture.RunningJson().Replace("\"reason\":\"Running\"", $"\"reason\":\"{reason}\"", StringComparison.Ordinal);
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json), new DelayCompletingClock()).ObserveAsync(
            fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromSeconds(1)));
        Assert.Equal(reason == "CANCELLING" ? CloudRunExecutionState.Running : CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
    }

    [Theory]
    [InlineData("2026-10-24T12:00:00", "ASDEPLOY174")]
    [InlineData("2026-02-30T12:00:00Z", "ASDEPLOY174")]
    [InlineData("2026-10-24T12:00:00.1234567890Z", "ASDEPLOY174")]
    public async Task InvalidCompletionTimestampsAreRejected(string timestamp, string code)
    {
        // protects=strict RFC3339 terminal evidence; fails_when=ambiguous timestamps pass; no_claim=local clock receipt is provider completion time.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.ExecutionJson().Replace("2026-10-24T12:00:00Z", timestamp, StringComparison.Ordinal)), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(code, result.DiagnosticCode);
    }

    [Fact]
    public async Task V1FailedWithoutNonZeroReasonIsUnknown()
    {
        // protects=workload-failure proof; fails_when=failed counters alone become Failed; no_claim=logs establish root cause.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.StateJson("failed", "NonZeroExitCode", "False"))!;
        json["status"]!["conditions"]![0]!.AsObject().Remove("reason");
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Theory]
    [InlineData("CANCELLING")]
    [InlineData("DELAYED_START_PENDING")]
    public async Task V2RecognizedNonterminalReasonsRemainRunning(string reason)
    {
        // protects=v2 transient reason table; fails_when=recognized nonterminal evidence becomes Unknown; no_claim=completion or acceptance.
        using var fixture = new ObserverFixture();
        var json = fixture.V2ExecutionJson().Replace("\"state\":\"CONDITION_SUCCEEDED\"", "\"state\":\"CONDITION_PENDING\",\"executionReason\":\"" + reason + "\"", StringComparison.Ordinal).Replace("\"completionTime\":\"2026-10-24T12:00:00Z\",", "", StringComparison.Ordinal).Replace("\"succeededCount\":1", "\"succeededCount\":0", StringComparison.Ordinal).Replace("\"runningCount\":0", "\"runningCount\":1", StringComparison.Ordinal);
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json), new DelayCompletingClock()).ObserveAsync(fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromSeconds(1)));
        Assert.Equal(CloudRunExecutionState.Running, result.State);
    }

    [Theory]
    [InlineData("\"succeededCount\":1", "\"succeededCount\":null")]
    [InlineData("\"succeededCount\":1", "\"succeededCount\":-1")]
    [InlineData("\"succeededCount\":1", "\"succeededCount\":2147483648")]
    public async Task InvalidV1CounterValuesAreRejected(string original, string replacement)
    {
        // protects=counter type/range validation; fails_when=null, negative, or overflow counters pass; no_claim=provider task state is recovered.
        using var fixture = new ObserverFixture();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(fixture.ExecutionJson().Replace(original, replacement, StringComparison.Ordinal)), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY174", result.DiagnosticCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"invalid\"")]
    public async Task PresentMalformedV2LabelsCannotBeIgnored(string labels)
    {
        // protects=independent returned identity carriers; fails_when=malformed labels are treated as absent;
        // no_claim=well-formed labels authenticate provider identity.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        json["labels"] = JsonNode.Parse(labels);
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY174", result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Fact]
    public async Task V2Int64GenerationCanBeMatched()
    {
        // protects=wide v2 generation comparison; fails_when=valid Int64 generations overflow an Int32 parser; no_claim=generation attests execution provenance.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        json["generation"] = 2147483648L;
        json["observedGeneration"] = 2147483648L;
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Theory]
    [InlineData("executionReason", "NON_ZERO_EXIT_CODE", CloudRunExecutionState.Failed)]
    [InlineData("executionReason", "CANCELLED", CloudRunExecutionState.Cancelled)]
    [InlineData(null, null, CloudRunExecutionState.Unknown)]
    [InlineData("reason", "NON_ZERO_EXIT_CODE", CloudRunExecutionState.Unknown)]
    [InlineData("executionReason", "JOB_STATUS_SERVICE_POLLING_ERROR", CloudRunExecutionState.Unknown)]
    [InlineData("executionReason", "MysteryState", CloudRunExecutionState.Unknown)]
    public async Task V2FailureRequiresRecognizedExecutionReason(string? field, string? reason, CloudRunExecutionState expected)
    {
        // protects=workload versus provider failure classification; fails_when=counts alone or a different reason union promotes failure;
        // no_claim=the selected execution belongs to a particular submission.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        var condition = json["conditions"]![0]!.AsObject();
        condition["state"] = "CONDITION_FAILED";
        if (field is not null) condition[field] = reason;
        json["succeededCount"] = 0;
        json["failedCount"] = expected == CloudRunExecutionState.Cancelled ? 0 : 1;
        json["cancelledCount"] = expected == CloudRunExecutionState.Cancelled ? 1 : 0;
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(expected, result.State);
        Assert.Equal(expected != CloudRunExecutionState.Unknown, result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("executionReason", "EXECUTION_SUCCEEDED")]
    [InlineData("executionReason", "MysteryState")]
    [InlineData("reason", "WAITING_FOR_OPERATION")]
    [InlineData("revisionReason", "PENDING")]
    [InlineData("instanceReason", "INSTANCE_DELETED")]
    public async Task V2SucceededConditionCannotHaveReason(string field, string reason)
    {
        // protects=successful condition's absent-reason contract; fails_when=unsupported or contradictory reason is ignored;
        // no_claim=synthetic v2 success establishes installed-gcloud compatibility.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        json["conditions"]![0]![field] = reason;
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(null, null, CloudRunExecutionState.Running)]
    [InlineData("reason", "WAITING_FOR_OPERATION", CloudRunExecutionState.Running)]
    [InlineData("reason", "IMMEDIATE_RETRY", CloudRunExecutionState.Running)]
    [InlineData("reason", "POSTPONED_RETRY", CloudRunExecutionState.Running)]
    [InlineData("executionReason", "MysteryState", CloudRunExecutionState.Unknown)]
    [InlineData("executionReason", "DELETED", CloudRunExecutionState.Unknown)]
    [InlineData("executionReason", "JOB_STATUS_SERVICE_POLLING_ERROR", CloudRunExecutionState.Unknown)]
    [InlineData("executionReason", "WAITING_FOR_OPERATION", CloudRunExecutionState.Unknown)]
    [InlineData("reason", "SECRETS_ACCESS_CHECK_FAILED", CloudRunExecutionState.Unknown)]
    public async Task V2PendingReasonUnionRequiresSupportedTransientEvidence(string? field, string? reason, CloudRunExecutionState expected)
    {
        // protects=positive nonterminal evidence and reason-field identity; fails_when=provider faults or unknown enums become Running;
        // no_claim=the remote workload remains running at the local deadline.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        json["conditions"]![0]!["state"] = "CONDITION_PENDING";
        if (field is not null) json["conditions"]![0]![field] = reason;
        json.Remove("completionTime");
        json["succeededCount"] = 0;
        json["runningCount"] = 1;
        json["reconciling"] = true;
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), new DelayCompletingClock()).ObserveAsync(
            fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromSeconds(1)));
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == CloudRunExecutionState.Running, result.ConfigurationMatched);
    }

    [Fact]
    public async Task V2ReasonUnionCannotContainTwoFields()
    {
        // protects=unambiguous provider condition union; fails_when=one contradictory reason is silently preferred;
        // no_claim=recognized reason fields prove terminal success.
        using var fixture = new ObserverFixture();
        var json = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        json["conditions"]![0]!["reason"] = "WAITING_FOR_OPERATION";
        json["conditions"]![0]!["executionReason"] = "CANCELLING";
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY174", result.DiagnosticCode);
    }

    private static string RemoveStatusProperty(string json, string property) => RemoveProperty(json, "status", property);
    private static string RemoveRootProperty(string json, string property) => RemoveProperty(json, null, property);
    private static string RemoveProperty(string json, string? parent, string property)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var node = parent is null ? root : root[parent]!.AsObject();
        node.Remove(property);
        return root.ToJsonString();
    }

    private sealed class StubReader(params string[] responses) : IBoundedGcloudExecutionReader
    {
        private readonly Queue<string> _responses = new(responses);
        public int Calls { get; private set; }

        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            string response;
            lock (_responses)
            {
                Calls++;
                response = _responses.Count == 0 ? "{}" : _responses.Dequeue();
            }
            if (response == "__FAIL__") return Task.FromResult(new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.ReadFailure, null));
            return Task.FromResult(new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, Encoding.UTF8.GetBytes(response)));
        }
    }

    private sealed class MutatingReader(List<DeploymentArtifact> artifacts, string response) : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            artifacts.Clear();
            return Task.FromResult(new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, Encoding.UTF8.GetBytes(response)));
        }
    }

    private sealed class CancellingReader(CancellationTokenSource source) : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.FromCanceled<BoundedGcloudExecutionReadResult>(source.Token);
        }
    }

    private sealed class AdvancingReader(AdvancingTimeProvider clock, string response) : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, Encoding.UTF8.GetBytes(response)));
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class OutcomeReader(BoundedGcloudExecutionReadOutcome outcome) : IBoundedGcloudExecutionReader
    {
        public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(new BoundedGcloudExecutionReadResult(outcome, outcome == BoundedGcloudExecutionReadOutcome.Complete ? 0 : null));
    }

    private sealed class ObserverFixture : IDisposable
    {
        private const string Project = "demo-project";
        private const string Number = "123456789012";
        private const string Region = "us-central1";
        private const string Job = "schema-preparation";
        private const string Execution = "schema-preparation-run";
        private const string Image = "us-docker.pkg.dev/demo-project/jobs/migrations@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly string _root;
        private readonly string _profile;
        private readonly IReadOnlyDictionary<string, string>? _environment;
        public string ProjectName => Project;
        public string RegionName => Region;
        public string JobName => Job;
        public string ExecutionName => Execution;

        public ObserverFixture(IReadOnlyDictionary<string, string>? environment = null)
        {
            _environment = environment;
            _root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-observer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _profile = TestPathUtils.PathUnder(_root, "profile.json");
            File.WriteAllText(_profile, JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                environment = "Staging",
                project = Project,
                projectNumber = Number,
                region = Region,
                jobs = new Dictionary<string, string> { ["schema-preparation"] = Job },
                cloudSqlInstanceConnectionName = $"{Project}:{Region}:primary",
                network = new { network = $"projects/{Project}/global/networks/main", subnetwork = $"projects/{Project}/regions/{Region}/subnetworks/main", egress = "ALL_TRAFFIC" },
                serviceAccounts = new Dictionary<string, string> { ["migration-runtime"] = $"migration@{Project}.iam.gserviceaccount.com" },
                secrets = new Dictionary<string, object> { ["database"] = new { secretId = "database", versionMode = "latest" } }
            }));
        }

        public DeploymentRenderRequest RenderRequest() => new(Intent(), _profile, _root, "test");
        public DeploymentRenderResult Rendered() => new GcpCloudRunDeploymentTarget().RenderAsync(RenderRequest()).GetAwaiter().GetResult();
        public CloudRunExecutionObservationRequest Request(TimeSpan? timeout = null, TimeSpan? interval = null)
        {
            var rendered = Rendered();
            return new(rendered, "schema-preparation", $"projects/{Project}/locations/{Region}/jobs/{Job}/executions/{Execution}", timeout ?? TimeSpan.FromSeconds(5), interval ?? TimeSpan.FromMilliseconds(10));
        }

        public string ExecutionJson() => JsonSerializer.Serialize(new
        {
            apiVersion = "run.googleapis.com/v1",
            kind = "Execution",
            metadata = new { name = Execution, @namespace = Number, uid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", generation = "7", labels = new Dictionary<string, string> { ["cloud.googleapis.com/location"] = Region, ["run.googleapis.com/job"] = Job }, annotations = new Dictionary<string, string> { ["run.googleapis.com/cloudsql-instances"] = $"{Project}:{Region}:primary", ["run.googleapis.com/network-interfaces"] = JsonSerializer.Serialize(new[] { new { network = $"projects/{Project}/global/networks/main", subnetwork = $"projects/{Project}/regions/{Region}/subnetworks/main" } }), ["run.googleapis.com/vpc-access-egress"] = "all-traffic", ["run.googleapis.com/execution-environment"] = "gen2" } },
            spec = new { taskCount = 1, parallelism = 1, template = new { spec = new { containers = new[] { new { image = Image, command = new[] { "dotnet" }, args = new[] { "migrate" }, env = new[] { new { name = "ConnectionStrings__database", valueFrom = new { secretKeyRef = new { name = "database", key = "latest" } } } } } }, serviceAccountName = $"migration@{Project}.iam.gserviceaccount.com", maxRetries = 3, timeoutSeconds = 600 } } },
            status = new { observedGeneration = "7", conditions = new[] { new { type = "Completed", status = "True", reason = "Completed" } }, completionTime = "2026-10-24T12:00:00Z", succeededCount = 1, runningCount = 0, failedCount = 0, cancelledCount = 0, retriedCount = 0 }
        });

        public string RunningJson() => ExecutionJson()
            .Replace("\"status\":\"True\",\"reason\":\"Completed\"", "\"status\":\"Unknown\",\"reason\":\"Running\"", StringComparison.Ordinal)
            .Replace(",\"completionTime\":\"2026-10-24T12:00:00Z\",\"succeededCount\":1,\"runningCount\":0", ",\"runningCount\":1", StringComparison.Ordinal);

        public string StateJson(string mode, string reason, string condition)
        {
            var root = JsonNode.Parse(ExecutionJson())!.AsObject();
            var status = root["status"]!.AsObject();
            status["conditions"] = new JsonArray { new JsonObject { ["type"] = "Completed", ["status"] = condition, ["reason"] = reason } };
            status.Remove("succeededCount"); status.Remove("runningCount"); status.Remove("failedCount"); status.Remove("cancelledCount");
            if (mode == "failed") status["failedCount"] = 1;
            else status["cancelledCount"] = 1;
            return root.ToJsonString();
        }

        public string V2ExecutionJson()
        {
            var root = JsonNode.Parse(ExecutionJson())!.AsObject();
            var metadata = root["metadata"]!.AsObject();
            var status = root["status"]!.AsObject();
            root.Remove("apiVersion"); root.Remove("kind"); root.Remove("metadata");
            root.Remove("status");
            foreach (var property in status) root[property.Key] = property.Value!.DeepClone();
            root["name"] = $"projects/{Project}/locations/{Region}/jobs/{Job}/executions/{Execution}";
            root["uid"] = metadata["uid"]!.GetValue<string>(); root["generation"] = 7; root["observedGeneration"] = 7;
            root["job"] = $"projects/{Project}/locations/{Region}/jobs/{Job}";
            var spec = root["spec"]!.AsObject(); var oldTask = spec["template"]!["spec"]!.DeepClone()!.AsObject();
            root["taskCount"] = spec["taskCount"]!.DeepClone(); root["parallelism"] = spec["parallelism"]!.DeepClone();
            var container = oldTask["containers"]![0]!.AsObject();
            var env = container["env"]![0]!.AsObject(); var secret = env["valueFrom"]!["secretKeyRef"]!.AsObject();
            env.Remove("valueFrom"); env["valueSource"] = new JsonObject { ["secretKeyRef"] = new JsonObject { ["secret"] = $"projects/{Project}/secrets/database", ["version"] = "latest" } };
            oldTask.Remove("serviceAccountName"); oldTask["serviceAccount"] = $"migration@{Project}.iam.gserviceaccount.com";
            oldTask.Remove("timeoutSeconds"); oldTask["timeout"] = "600s"; oldTask["executionEnvironment"] = "EXECUTION_ENVIRONMENT_GEN2";
            oldTask["vpcAccess"] = new JsonObject { ["networkInterfaces"] = new JsonArray { new JsonObject { ["network"] = $"projects/{Project}/global/networks/main", ["subnetwork"] = $"projects/{Project}/regions/{Region}/subnetworks/main" } }, ["egress"] = "ALL_TRAFFIC" };
            oldTask["volumes"] = new JsonArray { new JsonObject { ["name"] = "cloudsql", ["cloudSqlInstance"] = new JsonObject { ["instances"] = new JsonArray($"{Project}:{Region}:primary") } } };
            container["volumeMounts"] = new JsonArray { new JsonObject { ["name"] = "cloudsql", ["mountPath"] = "/cloudsql" } };
            var task = oldTask; spec.Remove("template"); root["template"] = task;
            root.Remove("spec"); root["conditions"] = new JsonArray { new JsonObject { ["type"] = "Completed", ["state"] = "CONDITION_SUCCEEDED" } };
            root["reconciling"] = false;
            return root.ToJsonString();
        }

        private DeploymentIntent Intent() => new("Staging", new SourceRevision(new string('a', 40)), [new MigrationJobIntent(new DeploymentLogicalId("schema-preparation"), DeploymentPhase.CandidatePreparation, new ImmutableImageReference(Image), "dotnet", ["migrate"], new DeploymentExecutionPolicy(1, 1, 3, TimeSpan.FromMinutes(10)), new SecretBinding(new DeploymentLogicalId("database"), new DeploymentLogicalId("database-parameter"), "ConnectionStrings__database"), new DatabaseBinding(new DeploymentLogicalId("database"), "ConnectionStrings:database", new DeploymentLogicalId("database")), new DeploymentLogicalId("migration-runtime"), environment: _environment)]);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
