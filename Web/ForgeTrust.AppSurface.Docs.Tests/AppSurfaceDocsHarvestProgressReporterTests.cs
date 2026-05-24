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

    [Fact]
    public async Task ProgressReporter_ShouldPublishRetainedCompletionBeforeLiveVisit()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.True(hub.Published.Count >= 3);
        var completion = hub.Published[^2];
        var visit = hub.Published[^1];
        Assert.Equal(AppSurfaceDocsHarvestProgressReporter.ChannelName, completion.Channel);
        Assert.True(completion.Options?.Replay);
        Assert.Contains("data-appsurface-docs-harvest-complete=\"true\"", completion.Message, StringComparison.Ordinal);
        Assert.Equal(AppSurfaceDocsHarvestProgressReporter.ChannelName, visit.Channel);
        Assert.False(visit.Options?.Replay ?? false);
        Assert.Equal(
            "<turbo-stream action=\"rw-visit\" url=\"#\" visit-action=\"replace\"></turbo-stream>",
            visit.Message);
    }

    [Fact]
    public async Task ProgressReporter_ShouldNotReplayCompletionVisit()
    {
        var hub = new InMemoryRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());

        var replay = hub.Subscribe(
            AppSurfaceDocsHarvestProgressReporter.ChannelName,
            new RazorWireStreamSubscribeOptions { Replay = true });
        var messages = new List<string>();
        while (replay.TryRead(out var message))
        {
            messages.Add(message);
        }

        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.DoesNotContain("action=\"rw-visit\"", message, StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
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

    private sealed class RecordingRazorWireStreamHub : IRazorWireStreamHub
    {
        public List<PublishedMessage> Published { get; } = [];

        public ValueTask PublishAsync(string channel, string message)
        {
            Published.Add(new PublishedMessage(channel, message, null));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            Published.Add(new PublishedMessage(channel, message, options));
            return ValueTask.CompletedTask;
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
        }
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

    private sealed record PublishedMessage(
        string Channel,
        string Message,
        RazorWireStreamPublishOptions? Options);
}
