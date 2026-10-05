using System.Diagnostics;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;

namespace DurableWorkerTemplate.Tests;

public sealed partial class TypedWorkScenarioTests
{
    [Fact]
    public async Task Published_testing_contract_builds_typed_work_and_keeps_admission_authoritative()
    {
        var definition = CreateDefinition();
        var observation = DurableWorkDefinitionObservation.Capture(definition);
        var request = new DurableWorkRequestBuilder<ScenarioWork, ScenarioResult>(definition)
            .WithScope(new DurableScopeId("scenario-scope"))
            .WithCommand(new DurableCommandId("scenario-command"))
            .WithIdempotencyKey("scenario-key")
            .WithWork(new ScenarioWork("safe-value"))
            .Build();

        Assert.Equal("template.tests.scenario", observation.WorkName);
        Assert.Equal("v1", observation.WorkVersion);
        Assert.Equal(typeof(ScenarioWork), observation.WorkPayloadType);
        Assert.Equal(typeof(ScenarioResult), observation.ResultPayloadType);
        Assert.Equal(DurableProviderSafety.Idempotent, observation.ProviderSafety);
        Assert.Equal(new ScenarioWork("safe-value"), definition.WorkCodec.Decode(request.Payload));

        var health = new FakeDurableRuntimeHealth(
            new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.NotStarted).Build());
        var admission = new RecordingDurableRuntimePump();
        var pumpRequest = new DurableRuntimePumpRequestBuilder()
            .WithMaximumItems(1)
            .WithTimeBudget(TimeSpan.FromSeconds(2))
            .WithSurfaces(DurableRuntimeSurface.Work)
            .Build();
        var scenario = new DurableHostScenario(health, admission, pumpRequest);

        var assessment = await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.False(assessment.Snapshot.IsReady);
        Assert.True(assessment.Snapshot.CanAttemptPump);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempt.Kind);
        var call = Assert.Single(admission.History);
        Assert.Same(pumpRequest, call.Request);
        Assert.Equal(DurableRuntimeSurface.Work, call.Surfaces);
        Assert.Same(assessment, Assert.Single(scenario.PumpInvocations).Assessment);
    }

    [Fact]
    public async Task Published_testing_contract_preserves_a_real_admission_refusal()
    {
        var health = new FakeDurableRuntimeHealth(
            new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Healthy).Build());
        var admission = new RecordingDurableRuntimePump
        {
            Admission = static (_, _) => ValueTask.FromResult(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, result: null, problemCode: null)),
        };
        var scenario = new DurableHostScenario(
            health,
            admission,
            new DurableRuntimePumpRequestBuilder().WithSurfaces(DurableRuntimeSurface.Work).Build());

        await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, attempt.Kind);
        Assert.Null(attempt.Result);
        Assert.Equal(DurableRuntimePumpCallKind.Admission, Assert.Single(admission.History).Kind);
    }

    [Fact]
    public async Task Fixture_resource_walk_preserves_primary_and_source_faults_and_attempts_the_owned_container()
    {
        var calls = new List<string>();
        var failures = new List<string> { "original-setup-failure" };
        await PostgreSqlFixture.CleanupOwnedResourcesAsync(new SetupOperationLifetime(),
            [() => { calls.Add("newest-source"); throw new IOException("private diagnostic"); },
             () => { calls.Add("older-source"); return Task.CompletedTask; }],
            dropNativeDatabase: null, stopOwnedContainer: null,
            () => { calls.Add("owned-container"); return Task.CompletedTask; },
            Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(2), failures);

        Assert.Equal(["newest-source", "older-source", "owned-container"], calls);
        Assert.Contains("original-setup-failure", failures);
        Assert.Contains("data-source-IOException", failures);
        Assert.DoesNotContain(failures, failure => failure.Contains("private diagnostic", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fixture_resource_walk_attempts_later_resources_after_expiry_or_synchronous_source_block(bool expired)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var container = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();
        var startedAt = Stopwatch.GetTimestamp() - (expired ? Stopwatch.Frequency : 0);
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await new SetupOperationLifetime().AwaitAsync("fixture-test-observation",
                () => PostgreSqlFixture.CleanupOwnedResourcesAsync(new SetupOperationLifetime(),
                [() =>
                 {
                     entered.SetResult();
                     try { release.Wait(); throw new IOException("late private diagnostic"); }
                     finally { sourceFinished.SetResult(); }
                 },
                 () => { older.SetResult(); return Task.CompletedTask; }],
                dropNativeDatabase: null, stopOwnedContainer: null,
                () => { container.SetResult(); return Task.CompletedTask; },
                startedAt, TimeSpan.FromMilliseconds(50), failures), observation.Token);
            await Task.WhenAll(entered.Task, older.Task, container.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(failures);
            Assert.Contains("fixture-cleanup-unsettled-or-budget-exhausted", failures);
        }
        finally
        {
            release.Set();
            await sourceFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fixture_native_DDL_is_attempted_after_expiry_only_when_provisioning_is_settled(bool pending)
    {
        var setup = new SetupOperationLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.AwaitAsync("native-provisioning",
            () => { entered.SetResult(); return release.Task; }, canceled.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (!pending)
        {
            release.SetResult();
            Assert.True(await setup.StopAndObservePendingAsync(null, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), new List<string>()));
        }
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ddl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();
        try
        {
            await PostgreSqlFixture.CleanupOwnedResourcesAsync(setup,
                [() => { source.SetResult(); return Task.CompletedTask; }],
                () => { ddl.SetResult(); return Task.CompletedTask; },
                stopOwnedContainer: null, disposeOwnedContainer: null,
                Stopwatch.GetTimestamp() - Stopwatch.Frequency, TimeSpan.FromMilliseconds(50), failures);
            await source.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (pending)
            {
                Assert.False(ddl.Task.IsCompleted);
                Assert.Contains("native-database-retained-while-setup-operation-remains-active", failures);
            }
            else
            {
                await ddl.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.DoesNotContain("native-database-retained-while-setup-operation-remains-active", failures);
            }
            Assert.NotEmpty(failures);
        }
        finally
        {
            release.TrySetResult();
            await setup.StopAndObservePendingAsync(null, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), new List<string>());
        }
    }

    [Theory]
    [InlineData("fault", true)]
    [InlineData("fault", false)]
    [InlineData("synchronous-block", true)]
    [InlineData("expired", true)]
    public async Task Fixture_container_fallback_preserves_owned_identity_and_the_original_deadline(string sdkMode, bool removeSucceeds)
    {
        const string ownedId = "owned-fixture-container";
        using var release = new ManualResetEventSlim();
        var sdkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdkFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = new TaskCompletionSource<(string Id, TimeSpan Budget)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdkCalls = 0;
        var identityReads = 0;
        var failures = new List<string>();
        var totalBudget = sdkMode == "fault" ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(50);
        var startedAt = Stopwatch.GetTimestamp() - (sdkMode == "expired" ? Stopwatch.Frequency : 0);
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await new SetupOperationLifetime().AwaitAsync("container-test-observation",
                () => PostgreSqlFixture.DisposeOwnedContainerAsync(() =>
            {
                Interlocked.Increment(ref sdkCalls);
                sdkEntered.SetResult();
                try
                {
                    if (sdkMode != "fault") { release.Wait(); }
                    throw new IOException("private SDK failure");
                }
                finally { sdkFinished.SetResult(); }
            }, () => { Interlocked.Increment(ref identityReads); return ownedId; },
                (id, remaining) =>
                {
                    fallback.SetResult((id, remaining));
                    release.Set();
                    return Task.FromResult(removeSucceeds);
                }, startedAt, totalBudget, failures), observation.Token);
            await sdkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var removal = await fallback.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ownedId, removal.Id);
            Assert.InRange(removal.Budget, TimeSpan.FromMilliseconds(1), totalBudget);
            Assert.Equal(1, Volatile.Read(ref sdkCalls));
            Assert.Equal(1, Volatile.Read(ref identityReads));
            if (sdkMode == "expired") { Assert.Equal(TimeSpan.FromMilliseconds(1), removal.Budget); }
            if (sdkMode == "fault" && removeSucceeds) { Assert.Empty(failures); }
            else { Assert.NotEmpty(failures); }
        }
        finally
        {
            release.Set();
            await sdkFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_container_removal_tolerates_SDK_fault_before_or_during_settlement_observation(bool alreadySettled)
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();
        if (alreadySettled) disposal.SetException(new IOException("private already-removed SDK detail"));

        var observation = PostgreSqlFixture.ObserveRemovedContainerDisposalAsync(disposal.Task,
            Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(1), failures);
        if (!alreadySettled)
        {
            Assert.False(observation.IsCompleted);
            disposal.SetException(new IOException("private already-removed SDK detail"));
        }
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(disposal.Task.IsCompleted);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Confirmed_container_removal_still_fails_when_SDK_settlement_outlives_the_original_deadline()
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();
        try
        {
            await PostgreSqlFixture.ObserveRemovedContainerDisposalAsync(disposal.Task,
                Stopwatch.GetTimestamp() - Stopwatch.Frequency, TimeSpan.FromMilliseconds(50), failures);

            Assert.False(disposal.Task.IsCompleted);
            Assert.Contains("container-dispose-unsettled-or-budget-exhausted", failures);
        }
        finally
        {
            disposal.SetException(new IOException("private late SDK detail"));
        }
    }

    private static DurableWorkDefinition<ScenarioWork, ScenarioResult> CreateDefinition() => DurableWork.Define(
        "template.tests.scenario",
        "v1",
        new SystemTextJsonDurablePayloadCodec<ScenarioWork>(
            "template.tests.scenario-input",
            "v1",
            DurableDataClassification.ApprovedApplication,
            ScenarioJsonContext.Default.ScenarioWork,
            static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 80,
            maximumBytes: 512),
        new SystemTextJsonDurablePayloadCodec<ScenarioResult>(
            "template.tests.scenario-result",
            "v1",
            DurableDataClassification.ApprovedApplication,
            ScenarioJsonContext.Default.ScenarioResult,
            static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 80,
            maximumBytes: 512),
        DurableProviderSafety.Idempotent,
        DurableWorkRetryPolicy.Default);

    private sealed record ScenarioWork(string Value);

    private sealed record ScenarioResult(string Value);

    [JsonSerializable(typeof(ScenarioWork))]
    [JsonSerializable(typeof(ScenarioResult))]
    private sealed partial class ScenarioJsonContext : JsonSerializerContext;
}
