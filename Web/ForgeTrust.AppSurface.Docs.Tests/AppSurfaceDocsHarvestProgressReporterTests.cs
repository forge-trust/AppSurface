using System.Threading.Channels;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestProgressReporterTests
{
    [Fact]
    public async Task ProgressReporter_ShouldIgnoreUpdatesFromStaleRunIds()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var before = reporter.CurrentSnapshot;

        await reporter.ActivityAsync("stale-run", "Ignored activity.");
        await reporter.HarvesterStartedAsync("stale-run", nameof(MarkdownHarvester));
        await reporter.CompleteRunAsync("stale-run", CreateHealth());

        Assert.Equal(runId, before.RunId);
        Assert.Equal(before, reporter.CurrentSnapshot);
    }

    [Fact]
    public async Task ProgressReporter_ShouldSwallowNonFatalPublishFailures()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub, ThrowingRazorWireStreamHub>();
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);

        Assert.Equal(runId, reporter.CurrentSnapshot.RunId);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Running, reporter.CurrentSnapshot.State);
    }

    private static DocHarvestHealthSnapshot CreateHealth()
    {
        return new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            "/tmp/repo",
            TotalHarvesters: 1,
            SuccessfulHarvesters: 1,
            FailedHarvesters: 0,
            TotalDocs: 1,
            [
                new DocHarvesterHealth(
                    nameof(MarkdownHarvester),
                    DocHarvesterHealthStatus.Succeeded,
                    DocCount: 1,
                    Diagnostic: null)
            ],
            Diagnostics: []);
    }

    private sealed class ThrowingRazorWireStreamHub : IRazorWireStreamHub
    {
        public ValueTask PublishAsync(string channel, string message)
        {
            throw new InvalidOperationException("Publish failed.");
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            throw new InvalidOperationException("Publish failed.");
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
        }
    }
}
