// docs:snippet durable-external-activation-export-imports:start
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ForgeTrust.AppSurface.Core;
using OpenTelemetry;
using OpenTelemetry.Trace;
// docs:snippet durable-external-activation-export-imports:end

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

// docs:snippet durable-external-activation-in-process-export:start
/// <summary>Proves export from the source-owned test host running in the same process as the OpenTelemetry SDK.</summary>
internal static class InProcessActivationExportExample
{
    /// <summary>Starts the in-process test host and sends an authorized empty wake through its real service.</summary>
    /// <param name="callerCancellation">Cancellation for the test-owned HTTP request.</param>
    /// <returns>The activation received by the attached exporter after a successful SDK flush.</returns>
    /// <remarks>The fixture uses controlled health/admission dependencies; this recipe alone proves no persistence.</remarks>
    internal static async Task<Activity> RequireExportAsync(CancellationToken callerCancellation)
    {
        var exported = new ConcurrentQueue<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new RecordingActivityExporter(exported)))
            .Build();
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            UseProductionActivationService = true,
        }).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        using var response = await host.Client.SendAsync(request, callerCancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync(callerCancellation).ConfigureAwait(false);

        if (!tracerProvider.ForceFlush(30_000))
        {
            throw new InvalidOperationException("The activation exporter did not flush.");
        }

        return exported.Single(activity =>
            activity.Source.Name == AppSurfaceActivitySources.ActivitySourceName
            && activity.OperationName == "appsurface.durable.runtime.activation");
    }

    /// <summary>Collects activities delivered through the SDK's export processor for this test only.</summary>
    /// <param name="exported">Test-owned receipt collection.</param>
    private sealed class RecordingActivityExporter(ConcurrentQueue<Activity> exported) : BaseExporter<Activity>
    {
        /// <inheritdoc />
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
// docs:snippet durable-external-activation-in-process-export:end
