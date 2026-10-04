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
                .AddSource(AppSurfaceTelemetrySources.ActivitySourceName)
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(new SimpleActivityExportProcessor(activityExporter)));

            using (var startup = new CancellationTokenSource(FixtureBudgets.HostStartup))
            {
                app = await WorkerApplication.BuildAsync(builder, startup.Token);
                await WorkerApplication.StartAsync(app, startup.Token);
            }

            var address = GetStartedAddress(app);
            using var client = new HttpClient { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

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
            phase = "authorization-before-wake-body";
            using (var unauthorized = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("body-must-not-be-read")),
            })
            {
                unauthorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    "invalid-token");
                using var response = await client.SendAsync(unauthorized, lifecycle.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            var workControl = app.Services.GetRequiredService<IDurableWorkControlClient>();
            var scopeId = new DurableScopeId(SampleWorkDefinition.Scope);
            var afterUnauthorized = await workControl.GetAsync(
                new DurableWorkGetRequest(scopeId, accepted.WorkId),
                lifecycle.Token);
            Assert.True(afterUnauthorized.IsSuccess, "The published Work control API could not inspect the accepted item.");
            var stillAccepted = Assert.IsType<DurableWorkSnapshot>(afterUnauthorized.Value);
            Assert.Equal(0, stillAccepted.AttemptNumber);
            Assert.False(IsTerminal(stillAccepted.State));

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
            Assert.Equal(fixture.RoleCatalogBeforeHost, await fixture.ReadRoleCatalogAsync(lifecycle.Token));
            Assert.Equal(fixture.DurableCatalogBeforeHost, await fixture.ReadDurableCatalogAsync(lifecycle.Token));
            var schema = await new PostgreSqlDurableRuntimeSchemaManager(fixture.Runtime).GetStatusAsync(lifecycle.Token);
            Assert.True(schema.IsCompatible);
            Assert.Equal(fixture.StoreId, schema.StoreId);
            Assert.Equal(fixture.Epoch, schema.ActiveRuntimeEpoch);

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
            var activityText = string.Join("\n", activationActivity.Tags.Values.Select(static value => value?.ToString()));
            Assert.DoesNotContain(token, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(dispatcherConnection, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(runtimeConnection, activityText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, activityText, StringComparison.Ordinal);
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
