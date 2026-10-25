using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class CloudRunExecutionObserverTests
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
        public string ProjectName => Project;
        public string RegionName => Region;
        public string JobName => Job;
        public string ExecutionName => Execution;

        public ObserverFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "appsurface-observer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _profile = Path.Combine(_root, "profile.json");
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
            root.Remove("spec"); root["conditions"] = new JsonArray { new JsonObject { ["type"] = "Completed", ["state"] = "CONDITION_SUCCEEDED", ["executionReason"] = "EXECUTION_SUCCEEDED" } };
            root["reconciling"] = false;
            return root.ToJsonString();
        }

        private static DeploymentIntent Intent() => new("Staging", new SourceRevision(new string('a', 40)), [new MigrationJobIntent(new DeploymentLogicalId("schema-preparation"), DeploymentPhase.CandidatePreparation, new ImmutableImageReference(Image), "dotnet", ["migrate"], new DeploymentExecutionPolicy(1, 1, 3, TimeSpan.FromMinutes(10)), new SecretBinding(new DeploymentLogicalId("database"), new DeploymentLogicalId("database-parameter"), "ConnectionStrings__database"), new DatabaseBinding(new DeploymentLogicalId("database"), "ConnectionStrings:database", new DeploymentLogicalId("database")), new DeploymentLogicalId("migration-runtime"))]);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
