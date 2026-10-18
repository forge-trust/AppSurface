using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using ForgeTrust.AppSurface.Core;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivationExporterCollection
{
    public const string Name = "Durable external activation shared activity source";
}

[Collection(ActivationExporterCollection.Name)]
public sealed class ActivationExporterTests
{
    private const string ActivationPath = "/private/durable/activate";
    private const string ContractVersionTag = "appsurface.durable.activation.contract_version";
    private const string PhaseTag = "appsurface.durable.activation.phase";
    private const string OutcomeTag = "appsurface.durable.activation.outcome";
    private const string HealthStateTag = "appsurface.durable.activation.health_state";

    private static readonly string[] ExpectedTagNames =
    [
        ContractVersionTag,
        PhaseTag,
        OutcomeTag,
        HealthStateTag,
    ];

    [Fact]
    public async Task Authorized_activation_reaches_the_sdk_exporter_and_successful_force_flush()
    {
        var exporter = new RecordingActivityExporter(forceFlushSucceeds: true);
        using var provider = CreateProvider(exporter);
        await using var host = await StartProductionServiceHostAsync();

        await SendAuthorizedActivationAsync(host);

        Assert.True(provider.ForceFlush(30_000));
        Assert.Equal(1, exporter.ForceFlushCallCount);
        Assert.Equal(1, exporter.ExportCallCount);
        AssertActivationActivity(Assert.Single(exporter.ExportedActivities));
    }

    [Fact]
    public async Task Listener_and_flush_without_an_exporter_do_not_produce_an_export_receipt()
    {
        var observed = new ConcurrentQueue<Activity>();
        using var listener = Listen(observed);
        using var provider = CreateProviderWithoutExporter();
        using var unattachedExporter = new RecordingActivityExporter(forceFlushSucceeds: true);
        await using var host = await StartProductionServiceHostAsync();

        await SendAuthorizedActivationAsync(host);

        Assert.True(provider.ForceFlush(30_000));
        AssertActivationActivity(Assert.Single(observed));
        Assert.Empty(unattachedExporter.ExportedActivities);
        Assert.Equal(0, unattachedExporter.ExportCallCount);
        Assert.Equal(0, unattachedExporter.ForceFlushCallCount);
    }

    [Fact]
    public async Task Force_flush_failure_is_reported_even_when_the_exporter_received_the_activity()
    {
        var exporter = new RecordingActivityExporter(forceFlushSucceeds: false);
        using var provider = CreateProvider(exporter);
        await using var host = await StartProductionServiceHostAsync();

        await SendAuthorizedActivationAsync(host);

        Assert.False(provider.ForceFlush(30_000));
        Assert.Equal(1, exporter.ForceFlushCallCount);
        Assert.Equal(1, exporter.ExportCallCount);
        AssertActivationActivity(Assert.Single(exporter.ExportedActivities));
    }

    private static async Task<ActivationTestHost> StartProductionServiceHostAsync() =>
        await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            UseProductionActivationService = true,
        }).ConfigureAwait(false);

    private static async Task SendAuthorizedActivationAsync(ActivationTestHost host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ActivationPath)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        request.Headers.Add("traceparent", "00-11111111111111111111111111111111-2222222222222222-00");
        using var response = await host.Client.SendAsync(request).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Equal(1L, host.Dependencies.Health.CallCount);
        Assert.Equal(1L, host.Dependencies.Admission.CallCount);
        Assert.Equal(0L, host.Dependencies.Activation.CallCount);
    }

    private static TracerProvider CreateProvider(RecordingActivityExporter exporter) =>
        Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();

    private static TracerProvider CreateProviderWithoutExporter() =>
        Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .Build();

    private static ActivityListener Listen(ConcurrentQueue<Activity> observed)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = observed.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertActivationActivity(Activity activity)
    {
        Assert.Equal("ForgeTrust.AppSurface", activity.Source.Name);
        Assert.Equal("appsurface.durable.runtime.activation", activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.True(activity.Recorded);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Empty(activity.Events);

        var tagObjects = activity.TagObjects.ToArray();
        Assert.Equal(
            ExpectedTagNames.OrderBy(static name => name, StringComparer.Ordinal),
            tagObjects.Select(tag => tag.Key).OrderBy(static name => name, StringComparer.Ordinal));
        var tags = tagObjects.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
        Assert.Equal(1, Assert.IsType<int>(tags[ContractVersionTag]));
        Assert.Equal("completed", Assert.IsType<string>(tags[PhaseTag]));
        Assert.Equal("Completed", Assert.IsType<string>(tags[OutcomeTag]));
        Assert.Equal("NotStarted", Assert.IsType<string>(tags[HealthStateTag]));
    }

    private sealed class RecordingActivityExporter(bool forceFlushSucceeds) : BaseExporter<Activity>
    {
        private readonly ConcurrentQueue<Activity> exportedActivities = new();
        private int exportCallCount;
        private int forceFlushCallCount;

        internal IReadOnlyList<Activity> ExportedActivities => exportedActivities.ToArray();

        internal int ExportCallCount => Volatile.Read(ref exportCallCount);

        internal int ForceFlushCallCount => Volatile.Read(ref forceFlushCallCount);

        public override ExportResult Export(in Batch<Activity> batch)
        {
            Interlocked.Increment(ref exportCallCount);
            foreach (var activity in batch)
            {
                exportedActivities.Enqueue(activity);
            }

            return ExportResult.Success;
        }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            Interlocked.Increment(ref forceFlushCallCount);
            return forceFlushSucceeds;
        }
    }
}
