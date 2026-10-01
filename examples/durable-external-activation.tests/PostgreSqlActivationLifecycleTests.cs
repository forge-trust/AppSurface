using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

/// <summary>Runs the actual host, restricted PostgreSQL roles, typed Work, and SDK exporter together.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlActivationLifecycleTests
{
    [Fact]
    public async Task Authorized_first_wake_completes_persisted_Work_becomes_ready_and_exports_activation()
    {
        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        var schemaBefore = await new PostgreSqlDurableRuntimeSchemaManager(fixture.Administrator).GetStatusAsync();
        Assert.DoesNotContain(fixture.Services.GetServices<IHostedService>(), service => service.GetType().Name.Contains("DurableRuntimeWorker", StringComparison.Ordinal));
        Assert.Same(fixture.Services.GetRequiredService<IDurableRuntimePump>(), fixture.Services.GetRequiredService<IDurableRuntimePumpAdmission>());
        Assert.Same(DemoWorkContract.Definition,
            fixture.Services.GetRequiredService<DurableWorkDefinition<DemoWork, DemoWorkResult>>());
        var receipt = await fixture.AcceptAsync("first-start");
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, receipt.Kind);
        Assert.Equal(DurableWorkState.Ready, (await fixture.InspectAsync(receipt)).State);

        using (var live = await fixture.Client.GetAsync("/live"))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        foreach (var path in new[] { "/compatibility", "/ready" })
        {
            using var response = await fixture.Client.GetAsync(path);
            Assert.Equal(path == "/ready" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Assessment", body.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("NotStarted", body.RootElement.GetProperty("observedHealthState").GetString());
            Assert.Equal(DurableProblemCodes.ActivatorStale, body.RootElement.GetProperty("problemCode").GetString());
            Assert.True(body.RootElement.GetProperty("canEnableActivation").GetBoolean());
            Assert.False(body.RootElement.GetProperty("isReady").GetBoolean());
        }

        var exported = new ConcurrentQueue<Activity>();
        using var telemetry = Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new LifecycleExporter(exported)))
            .Build();
        using var activation = await fixture.Client.PostAsync("/private/durable/activate", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using var activationBody = JsonDocument.Parse(await activation.Content.ReadAsStringAsync());
        var result = activationBody.RootElement;
        Assert.Equal("Completed", result.GetProperty("outcome").GetString());
        Assert.Equal("NotStarted", result.GetProperty("observedHealthState").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("problemCode").ValueKind);
        var aggregate = result.GetProperty("pumpResult");
        Assert.Equal(1, aggregate.GetProperty("claimed").GetInt32());
        Assert.Equal(1, aggregate.GetProperty("processed").GetInt32());
        Assert.Equal(0, aggregate.GetProperty("failed").GetInt32());
        Assert.True(telemetry.ForceFlush(30_000));
        var activity = Assert.Single(exported, value => value.OperationName == "appsurface.durable.runtime.activation");
        Assert.Equal(AppSurfaceActivitySources.ActivitySourceName, activity.Source.Name);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Empty(activity.Events);
        Assert.Equal(new[]
        {
            "appsurface.durable.activation.contract_version",
            "appsurface.durable.activation.health_state",
            "appsurface.durable.activation.outcome",
            "appsurface.durable.activation.phase",
        }, activity.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1, activity.GetTagItem("appsurface.durable.activation.contract_version"));
        Assert.Equal("NotStarted", activity.GetTagItem("appsurface.durable.activation.health_state"));
        Assert.Equal("Completed", activity.GetTagItem("appsurface.durable.activation.outcome"));
        Assert.Equal("completed", activity.GetTagItem("appsurface.durable.activation.phase"));

        var persisted = await fixture.InspectAsync(receipt);
        Assert.Equal(DurableWorkState.Succeeded, persisted.State);
        Assert.Equal(1, persisted.AttemptNumber);
        Assert.NotNull(persisted.TerminalAtUtc);
        Assert.Equal("processed:first-start", DemoWorkContract.CreateResultCodec().Decode(persisted.Result!).Value);
        using var ready = await fixture.Client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var readyBody = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", readyBody.RootElement.GetProperty("observedHealthState").GetString());
        Assert.True(readyBody.RootElement.GetProperty("isReady").GetBoolean());
        var schemaAfter = await new PostgreSqlDurableRuntimeSchemaManager(fixture.Administrator).GetStatusAsync();
        Assert.Equal(schemaBefore.StoreId, schemaAfter.StoreId);
        Assert.Equal(schemaBefore.ActiveRuntimeEpoch, schemaAfter.ActiveRuntimeEpoch);
        Assert.Equal(schemaBefore.InstalledVersion, schemaAfter.InstalledVersion);
        Assert.Equal(fixture.CatalogBeforeHost, await fixture.ReadCatalogAsync());
    }

    [Fact]
    public async Task Real_store_outage_preserves_liveness_and_reports_unavailable_assessments()
    {
        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        await fixture.SetStoreAvailableAsync(false);
        try
        {
            using var live = await fixture.Client.GetAsync("/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            foreach (var path in new[] { "/compatibility", "/ready" })
            {
                using var response = await fixture.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Assessment", body.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("Unavailable", body.RootElement.GetProperty("observedHealthState").GetString());
                Assert.Equal(DurableProblemCodes.StoreUnavailable, body.RootElement.GetProperty("problemCode").GetString());
                Assert.False(body.RootElement.GetProperty("canEnableActivation").GetBoolean());
                Assert.False(body.RootElement.GetProperty("isReady").GetBoolean());
            }
        }
        finally
        {
            await fixture.SetStoreAvailableAsync(true);
        }
    }

    [Fact]
    public async Task Drain_refuses_a_wake_without_claiming_accepted_Work()
    {
        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        var receipt = await fixture.AcceptAsync("draining");
        await fixture.Services.GetRequiredService<IDurableRuntimeDrainControl>().BeginDrainAsync();
        using var response = await fixture.Client.PostAsync("/private/durable/activate", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Draining", body.RootElement.GetProperty("outcome").GetString());
        var snapshot = await fixture.InspectAsync(receipt);
        Assert.Equal(DurableWorkState.Ready, snapshot.State);
        Assert.Equal(0, snapshot.AttemptNumber);
        using var ready = await fixture.Client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Overlapping_payload_free_wakes_leave_receipt_bound_Work_independently_inspectable()
    {
        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        var firstReceipt = await fixture.AcceptAsync("first-receipt");
        var secondReceipt = await fixture.AcceptAsync("second-receipt");
        Assert.NotEqual(firstReceipt.WorkId, secondReceipt.WorkId);

        var responses = await Task.WhenAll(
            fixture.Client.PostAsync("/private/durable/activate", new ByteArrayContent([])),
            fixture.Client.PostAsync("/private/durable/activate", new ByteArrayContent([])));
        try
        {
            var processedCounts = new List<int>();
            foreach (var response in responses)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var result = body.RootElement;
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    Assert.Equal("Busy", result.GetProperty("outcome").GetString());
                    Assert.Equal(JsonValueKind.Null, result.GetProperty("pumpResult").ValueKind);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("Completed", result.GetProperty("outcome").GetString());
                    processedCounts.Add(result.GetProperty("pumpResult").GetProperty("processed").GetInt32());
                }
            }

            // Generic wakes carry no acceptance identity. One pass may process both receipts while the other
            // wake is refused or completes an empty pass; response order cannot establish Work causality.
            Assert.Contains(2, processedCounts);
            Assert.Equal(2, processedCounts.Sum());
            foreach (var (receipt, expectedValue) in new[]
            {
                (firstReceipt, "processed:first-receipt"),
                (secondReceipt, "processed:second-receipt"),
            })
            {
                var persisted = await fixture.InspectAsync(receipt);
                Assert.Equal(receipt.WorkId, persisted.WorkId);
                Assert.Equal(DurableWorkState.Succeeded, persisted.State);
                Assert.Equal(1, persisted.AttemptNumber);
                Assert.NotNull(persisted.TerminalAtUtc);
                Assert.Equal(expectedValue, DemoWorkContract.CreateResultCodec().Decode(persisted.Result!).Value);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Lost_HTTP_response_is_recovered_from_the_retained_acceptance_receipt()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PostgreSqlActivationFixture.StartAsync(
            services => services.AddSingleton<IStartupFilter>(new DropActivationResponseFilter(completed)), useKestrel: true);
        var receipt = await fixture.AcceptAsync("lost-response");
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await fixture.Client.PostAsync("/private/durable/activate", new ByteArrayContent([]));
            _ = await response.Content.ReadAsStringAsync();
        });
        Assert.True(failure is HttpRequestException or OperationCanceledException, $"Expected a disconnected response, received {failure?.GetType().Name ?? "success"}.");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var snapshot = await fixture.InspectAsync(receipt);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.State);
        Assert.Equal(1, snapshot.AttemptNumber);
        Assert.Equal("processed:lost-response", DemoWorkContract.CreateResultCodec().Decode(snapshot.Result!).Value);
        using var ready = await fixture.Client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private sealed class LifecycleExporter(ConcurrentQueue<Activity> exported) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }
            return ExportResult.Success;
        }
    }

    private sealed class DropActivationResponseFilter(TaskCompletionSource completed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path != "/private/durable/activate")
                {
                    await continuation(context);
                    return;
                }
                context.Response.Body = Stream.Null;
                await continuation(context);
                completed.TrySetResult();
                context.Abort();
            });
            next(app);
        };
    }
}
