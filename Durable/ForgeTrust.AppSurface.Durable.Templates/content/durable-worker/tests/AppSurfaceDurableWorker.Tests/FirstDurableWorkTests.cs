using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppSurfaceDurableWorker;
using AppSurfaceDurableWorker.Work;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Observability;
using ForgeTrust.AppSurface.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit.Abstractions;

namespace DurableWorkerTemplate.Tests;

[Collection(GeneratedPostgreSqlActivationCollection.Name)]
public sealed class FirstDurableWorkTests
{
    private const string FirstProofLink = "https://github.com/forge-trust/AppSurface/blob/main/start-here/durable-worker.md#three-command-first-proof";
    private const string TimingReadyFileVariable = "APPSURFACE_TEMPLATE_TIMING_READY_FILE";
    private readonly ITestOutputHelper _output;

    public FirstDurableWorkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "PostgreSql")]
    public async Task FirstDurableWork_persists_typed_terminal_result_and_exports_the_real_activation()
    {
        var startedTicks = Stopwatch.GetTimestamp();
        _output.WriteLine($"[timing-fixture] started-utc={DateTimeOffset.UtcNow:O}");
        _output.WriteLine($"[timing-fixture] started-ticks={startedTicks.ToString(CultureInfo.InvariantCulture)}");
        var timingReadyFile = Environment.GetEnvironmentVariable(TimingReadyFileVariable);
        if (!string.IsNullOrWhiteSpace(timingReadyFile))
        {
            File.WriteAllText(timingReadyFile, startedTicks.ToString(CultureInfo.InvariantCulture));
        }

        PostgreSqlFixture? fixture = null;
        WebApplication? app = null;
        var phase = "postgresql-fixture-setup";
        var primaryFailure = (string?)null;
        var primaryFailureLocation = (string?)null;
        var cleanupFailures = new List<string>();
        var checkpoints = new List<string>(capacity: 4);
        var cleanupStartedAt = 0L;

        try
        {
            fixture = await PostgreSqlFixture.StartDockerAsync();
            _output.WriteLine($"[timing-fixture] database-sha256={fixture.DatabaseIdentitySha256}");

            phase = "host-read-only-startup";
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var value = $"first-work-{Guid.NewGuid():N}";
            var dispatcherConnection = fixture.DispatcherConnectionString;
            var runtimeConnection = fixture.RuntimeConnectionString;
            var activityExporter = new ActivityExporter();
            var builder = WorkerApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
            builder.Environment.EnvironmentName = Environments.Development;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Durable:DispatcherConnectionString"] = dispatcherConnection,
                ["Durable:RuntimeConnectionString"] = runtimeConnection,
                ["Durable:StoreId"] = fixture.StoreId.ToString("D"),
                ["Durable:RuntimeEpoch"] = fixture.Epoch.ToString("D"),
                [WorkerApplication.DevelopmentTokenKey] = token,
            });
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
                .AddProcessor(new SimpleActivityExportProcessor(activityExporter)));

            using (var startup = new CancellationTokenSource(FixtureBudgets.HostStartup))
            {
                app = await WorkerApplication.BuildAsync(builder, startup.Token);
                await WorkerApplication.StartAsync(app, startup.Token);
            }

            var address = GetStartedAddress(app);
            using var client = new HttpClient { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

            phase = "read-only-startup-catalog";
            using (var startupObservation = new CancellationTokenSource(FixtureBudgets.NativeObservation))
            {
                await AssertStoreIdentityUnchangedAsync(fixture, startupObservation.Token);
                using var live = await client.GetAsync("/live", startupObservation.Token);
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                using var json = await ReadJsonAsync(live, startupObservation.Token);
                Assert.Equal("Live", json.RootElement.GetProperty("status").GetString());
            }

            phase = "initial-readiness";
            using (var healthProbe = new CancellationTokenSource(FixtureBudgets.NativeObservation))
            {
                using (var compatibility = await client.GetAsync("/compatibility", healthProbe.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
                    using var json = await ReadJsonAsync(compatibility, healthProbe.Token);
                    AssertAssessment(json.RootElement, "NotStarted", canEnableActivation: true, isReady: false);
                }

                using (var readiness = await client.GetAsync("/ready", healthProbe.Token))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
                    using var json = await ReadJsonAsync(readiness, healthProbe.Token);
                    AssertAssessment(json.RootElement, "NotStarted", canEnableActivation: true, isReady: false);
                }
            }

            using var lifecycle = new CancellationTokenSource(FixtureBudgets.Lifecycle);
            phase = "typed-work-acceptance";
            var commandId = new DurableCommandId($"template-{Guid.NewGuid():N}");
            var producer = new SampleWorkProducer(app.Services.GetRequiredService<IDurableWorkClient>());
            var acceptance = await producer.AcceptAsync(
                new SampleWork(value),
                commandId,
                idempotencyKey: Guid.NewGuid().ToString("N"),
                lifecycle.Token);
            Assert.True(acceptance.IsSuccess, "The published Durable work client rejected the typed sample request.");
            var accepted = Assert.IsType<DurableWorkAcceptance>(acceptance.Value);
            Assert.Equal(commandId, accepted.CommandId);
            Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Kind);
            var workControl = app.Services.GetRequiredService<IDurableWorkControlClient>();
            var scopeId = new DurableScopeId(SampleWorkDefinition.Scope);
            var getRequest = new DurableWorkGetRequest(scopeId, accepted.WorkId);
            var beforeWake = await GetReadyUntouchedWorkAsync(workControl, getRequest, lifecycle.Token);
            Assert.Equal(accepted.Revision, beforeWake.Revision);
            phase = "authorization-before-wake-body";
            foreach (var bearer in new string?[] { null, "invalid-token" })
            {
                using var unauthorized = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("body-must-not-be-read")),
                };
                if (bearer is not null)
                {
                    unauthorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                }
                using var response = await client.SendAsync(unauthorized, lifecycle.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                var afterUnauthorized = await GetReadyUntouchedWorkAsync(workControl, getRequest, lifecycle.Token);
                Assert.Equal(beforeWake, afterUnauthorized);
            }

            phase = "authenticated-work-activation";
            using (var activation = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
            {
                Content = new ByteArrayContent([]),
            })
            {
                activation.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(activation, lifecycle.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = await ReadJsonAsync(response, lifecycle.Token);
                Assert.Equal("Completed", json.RootElement.GetProperty("outcome").GetString());
                var pump = json.RootElement.GetProperty("pumpResult");
                Assert.Equal(1, pump.GetProperty("discovered").GetInt32());
                Assert.Equal(1, pump.GetProperty("claimed").GetInt32());
                Assert.Equal(1, pump.GetProperty("processed").GetInt32());
                Assert.Equal(0, pump.GetProperty("failed").GetInt32());
                Assert.Equal(0, pump.GetProperty("deferred").GetInt32());
            }
            checkpoints.Add("[first-work] authorized activation accepted");

            phase = "persisted-terminal-work";
            var terminal = await WaitForTerminalWorkAsync(
                workControl,
                new DurableWorkGetRequest(scopeId, accepted.WorkId),
                lifecycle.Token);
            var definition = SampleWorkDefinition.Definition;
            Assert.Equal(accepted.WorkId, terminal.WorkId);
            Assert.Equal(definition.WorkName, terminal.WorkName);
            Assert.Equal(definition.WorkVersion, terminal.WorkVersion);
            Assert.Equal(definition.ProviderSafety, terminal.ProviderSafety);
            Assert.Equal(DurableWorkState.Succeeded, terminal.State);
            Assert.Equal(1, terminal.AttemptNumber);
            Assert.NotNull(terminal.TerminalAtUtc);
            Assert.Equal(TimeSpan.Zero, terminal.TerminalAtUtc!.Value.Offset);
            Assert.Equal("completed", terminal.TerminalCode);
            Assert.NotNull(terminal.Result);
            var result = definition.ResultCodec.Decode(terminal.Result!);
            Assert.Equal($"processed:{value}", result.Value);
            checkpoints.Add("[first-work] Work reached terminal completion");

            phase = "healthy-readiness-transition";
            using (var readiness = await client.GetAsync("/ready", lifecycle.Token))
            {
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
                using var json = await ReadJsonAsync(readiness, lifecycle.Token);
                AssertAssessment(json.RootElement, "Healthy", canEnableActivation: true, isReady: true);
            }
            checkpoints.Add("[first-work] readiness: NotStarted -> Healthy");

            phase = "read-only-startup-catalog";
            await AssertStoreIdentityUnchangedAsync(fixture, lifecycle.Token);

            phase = "sdk-export-flush";
            var provider = app.Services.GetRequiredService<TracerProvider>();
            var flushStartedAt = Stopwatch.GetTimestamp();
            var flushed = provider.ForceFlush((int)FixtureBudgets.ActivityFlush.TotalMilliseconds);
            Assert.True(flushed, "The generated OpenTelemetry SDK did not flush its activity exporter.");
            Assert.InRange(Stopwatch.GetElapsedTime(flushStartedAt), TimeSpan.Zero, FixtureBudgets.ActivityFlush);
            var activationActivity = Assert.Single(activityExporter.Activities, activity =>
                activity.SourceName == AppSurfaceTelemetrySources.ActivitySourceName
                && activity.OperationName == "appsurface.durable.runtime.activation");
            Assert.True(activityExporter.ExportCount > 0);
            Assert.True(activityExporter.FlushCount > 0);
            Assert.Equal(ActivityKind.Internal, activationActivity.Kind);
            Assert.Equal(ActivityStatusCode.Unset, activationActivity.Status);
            Assert.Null(activationActivity.StatusDescription);
            Assert.Equal(0, activationActivity.EventCount);
            const string tagPrefix = "appsurface.durable.activation.";
            Assert.Equal(new[] { tagPrefix + "contract_version", tagPrefix + "health_state", tagPrefix + "outcome", tagPrefix + "phase" },
                activationActivity.Tags.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(1, Assert.IsType<int>(activationActivity.Tags[tagPrefix + "contract_version"]));
            Assert.Equal("NotStarted", activationActivity.Tags[tagPrefix + "health_state"]);
            Assert.Equal("Completed", activationActivity.Tags[tagPrefix + "outcome"]);
            Assert.Equal("completed", activationActivity.Tags[tagPrefix + "phase"]);
            var activityText = string.Join("\n", activationActivity.Tags.Values.Select(static value => value?.ToString()));
            Assert.DoesNotContain(token, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(dispatcherConnection, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(runtimeConnection, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(accepted.WorkId.Value, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(scopeId.Value, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(commandId.Value, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(terminal.ActivityId, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(terminal.ProviderKey, activityText, StringComparison.Ordinal);
            checkpoints.Add("[first-work] exported appsurface.durable.runtime.activation");
        }
        catch (Exception exception)
        {
            primaryFailure = exception.GetType().Name;
            primaryFailureLocation = GetSafeFailureLocation(exception);
        }
        finally
        {
            cleanupStartedAt = Stopwatch.GetTimestamp();
            if (app is not null)
            {
                try
                {
                    await app.DisposeAsync().AsTask().WaitAsync(FixtureBudgets.Cleanup);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add($"host-{exception.GetType().Name}");
                }
            }

            if (fixture is not null)
            {
                var remaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStartedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    cleanupFailures.Add("fixture-budget-exhausted");
                }
                else
                {
                    try
                    {
                        await fixture.DisposeWithinBudgetAsync(remaining);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add($"fixture-{exception.GetType().Name}");
                    }
                }
            }
        }

        var cleanupElapsedMilliseconds = Math.Max(
            0L,
            (long)Math.Ceiling(Stopwatch.GetElapsedTime(cleanupStartedAt).TotalMilliseconds));
        _output.WriteLine($"[first-work-cleanup] elapsed-ms={cleanupElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");

        if (primaryFailure is not null || cleanupFailures.Count != 0 || checkpoints.Count != 4)
        {
            var failureCode = primaryFailure ?? (cleanupFailures.Count == 0 ? "MissingCheckpoint" : "CleanupFailed");
            var cleanup = cleanupFailures.Count == 0 ? "none" : string.Join(",", cleanupFailures);
            var retry = phase == "postgresql-fixture-setup"
                ? "Confirm Docker, the packaged PostgreSQL role recipe, and the disposable fixture, then rerun: dotnet test --filter FullyQualifiedName~FirstDurableWork"
                : "Confirm the generated package graph and rerun the FirstDurableWork test";
            _output.WriteLine(
                $"[FirstDurableWork] failure phase={phase} code={failureCode} at={primaryFailureLocation ?? "unknown"} cause=The bounded phase did not satisfy its assertion or owned cleanup contract; cleanup={cleanup}; retry={retry}; docs={FirstProofLink}");
            throw new InvalidOperationException($"FirstDurableWork failed in phase {phase} ({failureCode}); see {FirstProofLink}.");
        }

        foreach (var checkpoint in checkpoints)
        {
            _output.WriteLine(checkpoint);
        }

        _output.WriteLine("[FirstDurableWork] passed after owned cleanup");
    }

    private static Uri GetStartedAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault(static item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return address is not null
            ? new Uri(address, UriKind.Absolute)
            : throw new InvalidOperationException("The ordinary generated Kestrel server did not expose its loopback address.");
    }

    private static async Task AssertStoreIdentityUnchangedAsync(PostgreSqlFixture fixture, CancellationToken cancellationToken)
    {
        Assert.Equal(fixture.RoleCatalogBeforeHost, await fixture.ReadRoleCatalogAsync(cancellationToken));
        Assert.Equal(fixture.DurableCatalogBeforeHost, await fixture.ReadDurableCatalogAsync(cancellationToken));
        var schema = await new PostgreSqlDurableRuntimeSchemaManager(fixture.Runtime).GetStatusAsync(cancellationToken);
        Assert.True(schema.IsCompatible);
        Assert.Equal(fixture.StoreId, schema.StoreId);
        Assert.Equal(fixture.Epoch, schema.ActiveRuntimeEpoch);
    }

    private static async Task<DurableWorkSnapshot> GetReadyUntouchedWorkAsync(
        IDurableWorkControlClient control, DurableWorkGetRequest request, CancellationToken cancellationToken)
    {
        var observation = await control.GetAsync(request, cancellationToken);
        Assert.True(observation.IsSuccess, "The published Work control API could not inspect the accepted item.");
        var snapshot = Assert.IsType<DurableWorkSnapshot>(observation.Value);
        Assert.Equal(DurableWorkState.Ready, snapshot.State);
        Assert.Equal(0, snapshot.AttemptNumber);
        Assert.Null(snapshot.TerminalAtUtc);
        Assert.Null(snapshot.TerminalCode);
        Assert.Null(snapshot.Result);
        return snapshot;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 4096);
        return JsonDocument.Parse(body);
    }

    private static void AssertAssessment(JsonElement response, string state, bool canEnableActivation, bool isReady)
    {
        Assert.Equal("Assessment", response.GetProperty("outcome").GetString());
        Assert.Equal(state, response.GetProperty("observedHealthState").GetString());
        Assert.Equal(canEnableActivation, response.GetProperty("canEnableActivation").GetBoolean());
        Assert.Equal(isReady, response.GetProperty("isReady").GetBoolean());
    }

    private static string? GetSafeFailureLocation(Exception exception)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        if (frames is null)
        {
            return exception.TargetSite is { } target
                ? $"{target.DeclaringType?.Name ?? "unknown"}.{target.Name}"
                : null;
        }

        var methodNames = frames
            .Select(static frame => frame.GetMethod())
            .Where(static method => method is not null)
            .Select(static method => $"{method!.DeclaringType?.Name ?? "unknown"}.{method.Name}")
            .Take(4)
            .ToArray();
        return methodNames.Length == 0 ? null : string.Join("->", methodNames);
    }

    private static async Task<DurableWorkSnapshot> WaitForTerminalWorkAsync(
        IDurableWorkControlClient control,
        DurableWorkGetRequest request,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await control.GetAsync(request, cancellationToken);
            Assert.True(result.IsSuccess, "The published Work control API did not return an observation.");
            var snapshot = Assert.IsType<DurableWorkSnapshot>(result.Value);
            if (snapshot.State == DurableWorkState.Succeeded)
            {
                return snapshot;
            }

            if (IsTerminal(snapshot.State))
            {
                throw new InvalidOperationException("The sample Work reached a non-success terminal state.");
            }

            await Task.Delay(FixtureBudgets.WorkPollInterval, cancellationToken);
        }

        throw new TimeoutException("The sample Work did not reach a terminal result before its shared lifecycle budget expired.");
    }

    private static bool IsTerminal(DurableWorkState state) => state is
        DurableWorkState.Succeeded
        or DurableWorkState.SucceededAfterCancelRequested
        or DurableWorkState.FailedTerminal
        or DurableWorkState.CanceledBeforeEffect;
}

/// <summary>Exercises real generated Kestrel hosts and PostgreSQL authority with a controlled application executor.</summary>
/// <remarks>Separate from the documented FirstDurableWork filter so its four-checkpoint timing workload stays stable.</remarks>
[Collection(GeneratedPostgreSqlActivationCollection.Name)]
public sealed class GeneratedActivationConcurrencyTests
{
    private static readonly DurableWorkDefinition<SampleWork, SampleWorkResult> ControlledDefinition = DurableWork.Define(
        "template.controlled-work", "v1", SampleWorkDefinition.Definition.WorkCodec,
        SampleWorkDefinition.Definition.ResultCodec, DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

    [Fact]
    [Trait("Category", "PostgreSql")]
    public Task Twelve_http_wakes_share_one_real_host_admission_slot() => RunWithHostsAsync(1, async scenario =>
    {
        var accepted = await AcceptAsync(scenario);
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Kind);
        var running = scenario.WakeAsync(0);
        await scenario.Execution.Entered.Task.WaitAsync(FixtureBudgets.NativeObservation, scenario.Token);

        // The admitted executor remains held until every other HTTP wake has observed the provider's real slot.
        var overlaps = await Task.WhenAll(Enumerable.Range(0, 11).Select(_ => scenario.WakeAsync(0)));
        Assert.All(overlaps, static observation =>
        {
            Assert.Equal(HttpStatusCode.Conflict, observation.Status);
            Assert.Equal("Busy", observation.Outcome);
            Assert.Equal("Healthy", observation.Health);
            Assert.False(observation.HasPumpResult);
        });
        Assert.Equal(1, scenario.Execution.InvocationCount);
        var during = await GetWorkAsync(scenario, 0, accepted.WorkId);
        Assert.Equal(DurableWorkState.Claimed, during.State);
        Assert.Equal(1, during.AttemptNumber);
        Assert.Null(during.Result);

        scenario.Execution.Release.TrySetResult();
        AssertSuccessfulPass(await running, processed: 1);
        await AssertPersistedSuccessAsync(scenario, 0, accepted.WorkId, DurableWorkState.Succeeded);
        Assert.Equal(1, scenario.Execution.EffectCount);
        Assert.Equal(12, scenario.Requests.Count);
    });

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel-after-permit")]
    [InlineData("scope-fence")]
    [Trait("Category", "PostgreSql")]
    public Task Two_real_hosts_preserve_one_claim_and_completion_authority(string intervention) => RunWithHostsAsync(2, async scenario =>
    {
        var firstHealth = await scenario.Apps[0].Services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(scenario.Token);
        var secondHealth = await scenario.Apps[1].Services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(scenario.Token);
        // Heartbeats are keyed by WorkerId alone. Instance GUIDs cannot make a shared WorkerId concurrently ownable.
        Assert.NotEqual(firstHealth.WorkerId, secondHealth.WorkerId);

        var accepted = await AcceptAsync(scenario);
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Kind);
        var running = scenario.WakeAsync(0);
        await scenario.Execution.Entered.Task.WaitAsync(FixtureBudgets.NativeObservation, scenario.Token);
        var claimed = await GetWorkAsync(scenario, 1, accepted.WorkId);
        Assert.Equal(DurableWorkState.Claimed, claimed.State);
        Assert.Equal(1, claimed.AttemptNumber);
        Assert.Null(claimed.Result);

        // The other independently admitted host must not claim the already-permitted effect.
        AssertSuccessfulPass(await scenario.WakeAsync(1), processed: 0);
        Assert.Equal(1, scenario.Execution.InvocationCount);
        var activeFirst = await scenario.Apps[0].Services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(scenario.Token);
        var activeSecond = await scenario.Apps[1].Services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(scenario.Token);
        Assert.Equal(DurableRuntimeHealthState.Healthy, activeFirst.State);
        Assert.Equal(DurableRuntimeHealthState.Healthy, activeSecond.State);
        Assert.True(activeFirst.IsPassActive);
        Assert.False(activeSecond.IsPassActive);
        Assert.NotNull(activeFirst.WorkerInstanceId);
        Assert.NotNull(activeSecond.WorkerInstanceId);
        Assert.NotEqual(activeFirst.WorkerInstanceId, activeSecond.WorkerInstanceId);

        DurableWorkSnapshot? fencedBeforeCompletion = null;
        if (intervention == "cancel-after-permit")
        {
            var canceled = await scenario.Apps[1].Services.GetRequiredService<IDurableWorkControlClient>().CancelAsync(
                new DurableWorkCancelRequest(scenario.Scope, accepted.WorkId, "template-test", "cancel-after-permit", claimed.Revision),
                scenario.Token);
            Assert.True(canceled.IsSuccess, "The public cancellation command was rejected.");
            Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);
            Assert.Equal(DurableWorkState.CancelPending, canceled.Value.State);
        }
        else if (intervention == "scope-fence")
        {
            var disabled = await scenario.Apps[1].Services.GetRequiredService<IDurableScopeControlClient>().DisableAsync(
                new DurableScopeDisableRequest(scenario.Scope, "template-test", "fence-owned-completion", expectedGeneration: 1),
                scenario.Token);
            Assert.True(disabled.IsSuccess, "The public scope-generation fence was rejected.");
            Assert.Equal(DurableScopeDisableOutcome.Applied, disabled.Value!.Outcome);
            Assert.Equal(2, disabled.Value.Generation);
            fencedBeforeCompletion = await GetWorkAsync(scenario, 1, accepted.WorkId);
            Assert.Equal(DurableWorkState.Suspended, fencedBeforeCompletion.State);
            Assert.Equal(DurableProblemCodes.ScopeDisabled, fencedBeforeCompletion.TerminalCode);
            Assert.Equal(1, fencedBeforeCompletion.AttemptNumber);
            Assert.Null(fencedBeforeCompletion.TerminalAtUtc);
            Assert.Null(fencedBeforeCompletion.Result);
        }

        scenario.Execution.Release.TrySetResult();
        var completion = await running;
        if (intervention == "scope-fence")
        {
            Assert.Equal(HttpStatusCode.OK, completion.Status);
            Assert.Equal("Completed", completion.Outcome);
            Assert.Equal(1, completion.Claimed);
            Assert.Equal(0, completion.Processed);
            Assert.Equal(1, completion.Deferred);
            Assert.Equal(0, completion.Failed);
            var fenced = await GetWorkAsync(scenario, 1, accepted.WorkId);
            Assert.Equal(fencedBeforeCompletion, fenced);
            // A generation-two disabled scope cannot make the old completion or another wake authoritative.
            AssertSuccessfulPass(await scenario.WakeAsync(1), processed: 0);
            var afterRetry = await GetWorkAsync(scenario, 0, accepted.WorkId);
            Assert.Equal(fenced, afterRetry);
        }
        else
        {
            AssertSuccessfulPass(completion, processed: 1);
            var expectedState = intervention == "cancel-after-permit"
                ? DurableWorkState.SucceededAfterCancelRequested : DurableWorkState.Succeeded;
            var terminal = await AssertPersistedSuccessAsync(scenario, 0, accepted.WorkId, expectedState);
            await AssertPersistedSuccessAsync(scenario, 1, accepted.WorkId, expectedState);
            // Retry the same producer intent and wake the other host: neither may repeat the completed effect.
            var duplicate = await AcceptAsync(scenario);
            Assert.Equal(DurableWorkAcceptanceKind.Duplicate, duplicate.Kind);
            Assert.Equal(accepted.WorkId, duplicate.WorkId);
            AssertSuccessfulPass(await scenario.WakeAsync(1), processed: 0);
            var afterRetry = await GetWorkAsync(scenario, 1, accepted.WorkId);
            Assert.Equal(terminal.Revision, afterRetry.Revision);
            Assert.Equal(terminal.TerminalAtUtc, afterRetry.TerminalAtUtc);
            Assert.Equal(1, afterRetry.AttemptNumber);
        }
        Assert.Equal(1, scenario.Execution.InvocationCount);
        Assert.Equal(1, scenario.Execution.EffectCount);
    });

    private static async Task<DurableWorkAcceptance> AcceptAsync(Scenario scenario)
    {
        var result = await scenario.Apps[0].Services.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            ControlledDefinition.CreateRequest(scenario.Scope, scenario.Command, scenario.IdempotencyKey, new SampleWork(scenario.Value)),
            scenario.Token);
        Assert.True(result.IsSuccess, "The typed controlled Work was rejected.");
        return Assert.IsType<DurableWorkAcceptance>(result.Value);
    }

    private static async Task<DurableWorkSnapshot> GetWorkAsync(Scenario scenario, int host, DurableWorkId workId)
    {
        var result = await scenario.Apps[host].Services.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scenario.Scope, workId), scenario.Token);
        Assert.True(result.IsSuccess, "The public Work control observation was rejected.");
        return Assert.IsType<DurableWorkSnapshot>(result.Value);
    }

    private static async Task<DurableWorkSnapshot> AssertPersistedSuccessAsync(
        Scenario scenario, int host, DurableWorkId workId, DurableWorkState state)
    {
        var terminal = await GetWorkAsync(scenario, host, workId);
        Assert.Equal(state, terminal.State);
        Assert.Equal(1, terminal.AttemptNumber);
        Assert.Equal(ControlledDefinition.WorkName, terminal.WorkName);
        Assert.Equal(ControlledDefinition.WorkVersion, terminal.WorkVersion);
        Assert.Equal(ControlledDefinition.ProviderSafety, terminal.ProviderSafety);
        Assert.Equal("completed", terminal.TerminalCode);
        Assert.NotNull(terminal.TerminalAtUtc);
        Assert.NotNull(terminal.Result);
        Assert.Equal($"processed:{scenario.Value}", ControlledDefinition.ResultCodec.Decode(terminal.Result!).Value);
        return terminal;
    }

    private static void AssertSuccessfulPass(ActivationObservation result, int processed)
    {
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("Completed", result.Outcome);
        Assert.True(result.HasPumpResult);
        Assert.Equal(processed, result.Claimed);
        Assert.Equal(processed, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
    }

    private static async Task RunWithHostsAsync(int hostCount, Func<Scenario, Task> assertions)
    {
        PostgreSqlFixture? fixture = null;
        var apps = new List<WebApplication>();
        var clients = new List<HttpClient>();
        var execution = new ControlledExecution();
        var requests = new List<Task<ActivationObservation>>();
        using var lifecycle = new CancellationTokenSource();
        var failures = new List<string>();
        var phase = "fixture";
        try
        {
            fixture = await PostgreSqlFixture.StartDockerAsync();
            var bearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            for (var host = 0; host < hostCount; host++)
            {
                phase = "host-startup";
                var builder = WorkerApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
                builder.Environment.EnvironmentName = Environments.Development;
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Durable:DispatcherConnectionString"] = fixture.DispatcherConnectionString,
                    ["Durable:RuntimeConnectionString"] = fixture.RuntimeConnectionString,
                    ["Durable:StoreId"] = fixture.StoreId.ToString("D"),
                    ["Durable:RuntimeEpoch"] = fixture.Epoch.ToString("D"),
                    ["DurableActivation:RequestBudgetSeconds"] = "20",
                    [WorkerApplication.DevelopmentTokenKey] = bearer,
                });
                builder.Services.AddSingleton(execution);
                builder.Services.AddDurableWork(ControlledDefinition.ExecutedBy<ControlledExecutor>());
                using var startup = new CancellationTokenSource(FixtureBudgets.HostStartup);
                var app = await WorkerApplication.BuildAsync(builder, startup.Token);
                apps.Add(app);
                await WorkerApplication.StartAsync(app, startup.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                clients.Add(new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)), Timeout = Timeout.InfiniteTimeSpan });
            }
            lifecycle.CancelAfter(FixtureBudgets.Lifecycle);
            phase = "controlled-http-claims";
            await assertions(new Scenario(apps, clients, execution, requests, bearer, lifecycle.Token));
        }
        catch (Exception exception)
        {
            // Assertions can include payloads or credentials; retain only the safe phase and exception type.
            failures.Add($"{phase}-{exception.GetType().Name}");
        }
        finally
        {
            var cleanupStarted = Stopwatch.GetTimestamp();
            var cleanupOperations = new SetupOperationLifetime();
            execution.Release.TrySetResult();

            // These are deadlines on the one shared cleanup clock, not fresh budgets for each operation.
            await AttemptCleanupAsync("cancellation", _ => lifecycle.CancelAsync(), TimeSpan.FromSeconds(1));
            await AttemptCleanupAsync("requests", _ => Task.WhenAll(requests), FixtureBudgets.Cleanup / 4,
                allowSettledRequestCancellation: true);
            foreach (var app in apps.AsEnumerable().Reverse())
            {
                await AttemptCleanupAsync("host", _ => app.DisposeAsync().AsTask(), FixtureBudgets.Cleanup / 2);
            }
            foreach (var client in clients)
            {
                await AttemptCleanupAsync("client", _ =>
                {
                    client.Dispose();
                    return Task.CompletedTask;
                }, FixtureBudgets.Cleanup / 2);
            }
            if (fixture is not null)
            {
                await AttemptCleanupAsync("fixture", fixture.DisposeWithinBudgetAsync, FixtureBudgets.Cleanup);
            }
            if (cleanupOperations.PendingCount != 0 || requests.Any(static request => !request.IsCompleted))
            {
                failures.Add("owned-cleanup-unfinished");
            }
            if (Stopwatch.GetElapsedTime(cleanupStarted) > FixtureBudgets.Cleanup)
            {
                failures.Add("owned-cleanup-total-budget-exceeded");
            }

            async Task AttemptCleanupAsync(
                string resource, Func<TimeSpan, Task> dispose, TimeSpan deadline,
                bool allowSettledRequestCancellation = false)
            {
                var remaining = deadline - Stopwatch.GetElapsedTime(cleanupStarted);
                using var observation = new CancellationTokenSource();
                if (remaining <= TimeSpan.Zero)
                {
                    failures.Add($"{resource}-cleanup-budget-exhausted");
                    // Still invoke disposal off-thread, but do not grant a fresh observation window after expiry.
                    observation.Cancel();
                }
                else
                {
                    observation.CancelAfter(remaining);
                }
                try
                {
                    // SetupOperationLifetime bounds invocation itself and observes late task faults.
                    await cleanupOperations.AwaitAsync(resource, () =>
                    {
                        var sharedRemaining = FixtureBudgets.Cleanup - Stopwatch.GetElapsedTime(cleanupStarted);
                        return dispose(sharedRemaining > TimeSpan.Zero ? sharedRemaining : TimeSpan.FromMilliseconds(1));
                    }, observation.Token);
                }
                catch (OperationCanceledException) when (allowSettledRequestCancellation
                    && requests.All(static request => request.IsCompleted && !request.IsFaulted))
                {
                    // Cancellation of fully settled HTTP requests is expected after a failed assertion phase.
                }
                catch (Exception exception)
                {
                    failures.Add($"{resource}-{exception.GetType().Name}");
                }
            }
        }
        Assert.True(failures.Count == 0, $"Generated activation scenario failed: {string.Join(",", failures)}.");
    }

    /// <summary>Test-owned executor barrier; HTTP request and lifecycle tokens bound all waits.</summary>
    private sealed class ControlledExecution
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int InvocationCount;
        internal int EffectCount;
    }

    /// <summary>Application-selected typed executor registered through the published binding API.</summary>
    private sealed class ControlledExecutor : IDurableWorkerExecutor<SampleWork, SampleWorkResult>
    {
        private readonly ControlledExecution _execution;

        /// <summary>Uses the scenario-owned barrier through ordinary constructor injection.</summary>
        /// <param name="execution">Bounded barrier and effect observations shared by the scenario's hosts.</param>
        public ControlledExecutor(ControlledExecution execution)
        {
            _execution = execution;
        }

        /// <inheritdoc />
        public async ValueTask<SampleWorkResult> ExecuteAsync(DurableWorkerEnvelope<SampleWork> work, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _execution.InvocationCount);
            _execution.Entered.TrySetResult();
            await _execution.Release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _execution.EffectCount);
            return await new SampleWorkExecutor().ExecuteAsync(work, cancellationToken);
        }
    }

    private sealed class Scenario(
        IReadOnlyList<WebApplication> apps, IReadOnlyList<HttpClient> clients, ControlledExecution execution,
        List<Task<ActivationObservation>> requests, string bearer, CancellationToken token)
    {
        internal IReadOnlyList<WebApplication> Apps { get; } = apps;
        internal ControlledExecution Execution { get; } = execution;
        internal List<Task<ActivationObservation>> Requests { get; } = requests;
        internal CancellationToken Token { get; } = token;
        internal DurableScopeId Scope { get; } = new($"controlled-{Guid.NewGuid():N}");
        internal DurableCommandId Command { get; } = new($"controlled-{Guid.NewGuid():N}");
        internal string IdempotencyKey { get; } = Guid.NewGuid().ToString("N");
        internal string Value { get; } = $"controlled-{Guid.NewGuid():N}";

        internal Task<ActivationObservation> WakeAsync(int host)
        {
            var request = SendWakeAsync(clients[host], bearer, Token);
            Requests.Add(request);
            return request;
        }
    }

    private static async Task<ActivationObservation> SendWakeAsync(HttpClient client, string bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent([]),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 4096);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var hasPump = root.TryGetProperty("pumpResult", out var pump) && pump.ValueKind == JsonValueKind.Object;
        return new ActivationObservation(response.StatusCode, root.GetProperty("outcome").GetString(),
            root.GetProperty("observedHealthState").GetString(), hasPump,
            hasPump ? pump.GetProperty("claimed").GetInt32() : 0,
            hasPump ? pump.GetProperty("processed").GetInt32() : 0,
            hasPump ? pump.GetProperty("deferred").GetInt32() : 0,
            hasPump ? pump.GetProperty("failed").GetInt32() : 0);
    }

    private sealed record ActivationObservation(
        HttpStatusCode Status, string? Outcome, string? Health, bool HasPumpResult,
        int Claimed, int Processed, int Deferred, int Failed);
}

/// <summary>Serializes SDK observations of the process-shared source against other generated test collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GeneratedPostgreSqlActivationCollection
{
    /// <summary>Collection identity shared by the first proof and real activation concurrency scenarios.</summary>
    public const string Name = "generated-postgresql-activation";
}
