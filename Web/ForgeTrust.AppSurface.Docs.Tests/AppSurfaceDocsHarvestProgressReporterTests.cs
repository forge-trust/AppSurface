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

    [Fact]
    public async Task ProgressReporter_ShouldPublishCompletionVisitOnlyOnce()
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
        await reporter.CompleteRunAsync(runId, CreateHealth());

        var visitMessages = hub.Published
            .Where(item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(visitMessages);
    }

    [Fact]
    public async Task ProgressReporter_ShouldSuppressCompletionVisitForCurrentRun()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        reporter.SuppressCompletionVisitForCurrentOrNextRun();
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldSuppressCompletionVisitForNextRun_WhenSuppressionIsRequestedBeforeBegin()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        reporter.SuppressCompletionVisitForCurrentOrNextRun();
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldSuppressCompletionVisit_WhenQueuedDuringTerminalPublish()
    {
        var hub = new BlockingCompletionRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var complete = reporter.CompleteRunAsync(runId, CreateHealth()).AsTask();
        await hub.CompletionPublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        reporter.SuppressCompletionVisitForCurrentOrNextRun();
        hub.ReleaseCompletionPublish.TrySetResult();
        await complete.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreQueuedStatus_WhenSupersededRunIsStale()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var supersededRunId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var currentRunId = await reporter.BeginRunAsync([nameof(CSharpDocHarvester)]);
        await reporter.RebuildQueuedAsync(supersededRunId);

        Assert.Equal(currentRunId, reporter.CurrentSnapshot.RunId);
        Assert.Equal("Harvesting", reporter.CurrentSnapshot.Status);
        Assert.DoesNotContain(
            reporter.CurrentSnapshot.Activity,
            item => item.Message.Contains("rebuild is queued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProgressReporter_ShouldRecordQueuedStatus_WhenSupersededRunIsCurrent()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.RebuildQueuedAsync(runId);

        Assert.Equal("Harvesting (rebuild queued)", reporter.CurrentSnapshot.Status);
        Assert.Contains(
            reporter.CurrentSnapshot.Activity,
            item => item.Message.Contains("rebuild is queued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProgressReporter_ShouldReplayFailedTerminalStateWithoutVisit()
    {
        var hub = new InMemoryRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth(DocHarvestHealthStatus.Failed));

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
        Assert.Contains(messages, message => message.Contains("Needs attention", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Harvest finished with diagnostics", StringComparison.Ordinal));
    }

    private static DocHarvestHealthSnapshot CreateHealth(DocHarvestHealthStatus status = DocHarvestHealthStatus.Healthy)
    {
        var failed = status == DocHarvestHealthStatus.Failed;
        return new DocHarvestHealthSnapshot(
            status,
            DateTimeOffset.UtcNow,
            "/tmp/repo",
            TotalHarvesters: 1,
            SuccessfulHarvesters: failed ? 0 : 1,
            FailedHarvesters: failed ? 1 : 0,
            TotalDocs: failed ? 0 : 1,
            [
                new DocHarvesterHealth(
                    nameof(MarkdownHarvester),
                    failed ? DocHarvesterHealthStatus.Failed : DocHarvesterHealthStatus.Succeeded,
                    DocCount: failed ? 0 : 1,
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

    private sealed class BlockingCompletionRazorWireStreamHub : IRazorWireStreamHub
    {
        public List<PublishedMessage> Published { get; } = [];

        public TaskCompletionSource CompletionPublishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCompletionPublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(string channel, string message)
        {
            return PublishAsync(channel, message, options: null);
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            Published.Add(new PublishedMessage(channel, message, options));
            if (message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal))
            {
                CompletionPublishStarted.TrySetResult();
                return new ValueTask(ReleaseCompletionPublish.Task);
            }

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
