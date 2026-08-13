using System.Text.Json;
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
    public void ProgressSnapshot_ShouldSerializeAdditiveRateAndPhaseContract()
    {
        var json = JsonSerializer.Serialize(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                BuiltInDocumentsPerSecond = null,
                Harvesters =
                [
                    new AppSurfaceDocsHarvesterProgress(nameof(MarkdownHarvester), "Running", 2)
                    {
                        ProgressId = "server-only-id",
                        IsBuiltInProgressHarvester = true,
                        Phase = AppSurfaceDocsHarvestProgressPhase.Parsing,
                        SourceUnitsProcessed = 7
                    }
                ]
            });

        Assert.Contains("\"builtInDocumentsPerSecond\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"Parsing\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceUnitsProcessed\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server-only-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isBuiltInProgressHarvester", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressReporter_ShouldRetainPhaseAndSourceEvidenceWhenHealthTerminalizesRun()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);
        await session.ReportSourceUnitAsync(1);
        await reporter.CompleteRunAsync(runId, CreateHealth());

        var harvester = Assert.Single(reporter.CurrentSnapshot.Harvesters);
        Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Terminal, harvester.Phase);
        Assert.Equal(1, harvester.SourceUnitsProcessed);
        Assert.Equal(1, harvester.DocCount);
    }

    [Fact]
    public async Task ProgressReporter_ShouldCarryTrustedBuiltInMarkerFromRegistration()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        await reporter.BeginRunAsync(
        [
            new AppSurfaceDocsHarvesterRegistration("built-in", nameof(MarkdownHarvester), true),
            new AppSurfaceDocsHarvesterRegistration("custom", nameof(MarkdownHarvester))
        ]);

        var harvesters = reporter.CurrentSnapshot.Harvesters;
        Assert.True(harvesters.Single(harvester => harvester.ProgressId == "built-in").IsBuiltInProgressHarvester);
        Assert.False(harvesters.Single(harvester => harvester.ProgressId == "custom").IsBuiltInProgressHarvester);
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreInvalidOrOutOfOrderPhaseTransitions()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Waiting);
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Terminal);
        Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Waiting, Assert.Single(reporter.CurrentSnapshot.Harvesters).Phase);

        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Finalizing);
        Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Discovering, Assert.Single(reporter.CurrentSnapshot.Harvesters).Phase);

        await reporter.CompleteRunAsync(runId, CreateHealth());
        await session.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);

        Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Terminal, Assert.Single(reporter.CurrentSnapshot.Harvesters).Phase);
    }

    [Fact]
    public async Task ProgressReporter_ShouldRejectNegativeProgressDelta()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        Assert.Throws<ArgumentOutOfRangeException>(() => reporter.ReportSourceUnit(runId, nameof(MarkdownHarvester), -1));
        Assert.Equal(0, Assert.Single(reporter.CurrentSnapshot.Harvesters).SourceUnitsProcessed);
        await session.ReportSourceUnitAsync(0);
    }

    [Fact]
    public async Task ProgressReporter_ShouldCoalesceOrdinarySourceReports()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        for (var index = 0; index < 10_000; index++)
        {
            await session.ReportSourceUnitAsync(0);
        }

        Assert.Single(hub.Published);
        await hub.SecondPublicationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, hub.Published.Count);
        Assert.Equal(10_000, Assert.Single(reporter.CurrentSnapshot.Harvesters).SourceUnitsProcessed);
    }

    [Fact]
    public async Task ProgressReporter_ShouldComputeRollingBuiltInRateAfterMeasurementWindow()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance,
            timeProvider);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        await session.ReportSourceUnitAsync(2);
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Equal(8, reporter.CurrentSnapshot.BuiltInDocumentsPerSecond);
    }

    [Fact]
    public async Task ProgressReporter_ShouldDiscardStaleRateSamplesOutsideTheMeasurementWindow()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance,
            timeProvider);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var session = reporter.CreateSession(runId, nameof(MarkdownHarvester));

        await session.ReportSourceUnitAsync(2);
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Null(reporter.CurrentSnapshot.BuiltInDocumentsPerSecond);
    }

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
    public async Task ProgressReporter_ShouldKeepDuplicateHarvesterInstancesDistinct()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        AppSurfaceDocsHarvesterRegistration[] registrations =
        [
            new AppSurfaceDocsHarvesterRegistration("first", "CustomHarvester"),
            new AppSurfaceDocsHarvesterRegistration("second", "CustomHarvester")
        ];
        var runId = await reporter.BeginRunAsync(registrations);

        await reporter.HarvesterStartedAsync(runId, "first");
        await reporter.HarvesterCompletedAsync(runId, "first", DocHarvesterHealthStatus.Succeeded, 1);
        await reporter.HarvesterStartedAsync(runId, "second");
        await reporter.HarvesterCompletedAsync(runId, "second", DocHarvesterHealthStatus.Succeeded, 2);
        await reporter.CompleteRunAsync(runId, CreateHealthWithDuplicateHarvesters());

        Assert.Equal(2, reporter.CurrentSnapshot.Harvesters.Count);
        Assert.Equal([1, 2], reporter.CurrentSnapshot.Harvesters.Select(harvester => harvester.DocCount).ToArray());
        Assert.Equal(2, reporter.CurrentSnapshot.CompletedHarvesters);
    }

    [Fact]
    public void ProgressReporter_ShouldRejectDuplicateRegistrationIds()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        AppSurfaceDocsHarvesterRegistration[] registrations =
        [
            new AppSurfaceDocsHarvesterRegistration("same", "FirstHarvester"),
            new AppSurfaceDocsHarvesterRegistration("same", "SecondHarvester")
        ];

        Assert.Throws<ArgumentException>(() => reporter.BeginRunAsync(registrations));
    }

    [Fact]
    public void ProgressReporter_ShouldRejectBlankRegistrationIdentity()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var exception = Assert.Throws<ArgumentException>(
            () => reporter.BeginRunAsync([new AppSurfaceDocsHarvesterRegistration(" ", nameof(MarkdownHarvester))]));

        Assert.Contains("require non-blank progress IDs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreUnknownHarvesterCallbacksForTheCurrentRun()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var before = reporter.CurrentSnapshot;

        await reporter.HarvesterStartedAsync(runId, "missing");
        await reporter.HarvesterCompletedAsync(runId, "missing", DocHarvesterHealthStatus.Succeeded, 42);
        await reporter.HarvesterDocumentCountUpdatedAsync(runId, "missing", 42);

        Assert.Equal(before, reporter.CurrentSnapshot);
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreFailRunForStaleOrTerminalRuns()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.FailRunAsync("stale");

        Assert.Equal(AppSurfaceDocsHarvestRunState.Running, reporter.CurrentSnapshot.State);

        await reporter.FailRunAsync(runId);
        var failed = reporter.CurrentSnapshot;
        await reporter.FailRunAsync(runId);

        Assert.Equal(failed, reporter.CurrentSnapshot);
    }

    [Fact]
    public async Task ProgressReporter_ShouldCreateTerminalProgressForHealthHarvestersWithoutStartedCallback()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var health = new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            "/tmp/repo",
            TotalHarvesters: 2,
            SuccessfulHarvesters: 2,
            FailedHarvesters: 0,
            TotalDocs: 3,
            [
                new DocHarvesterHealth(nameof(MarkdownHarvester), DocHarvesterHealthStatus.Succeeded, 1, null),
                new DocHarvesterHealth(nameof(CSharpDocHarvester), DocHarvesterHealthStatus.Succeeded, 2, null)
            ],
            Diagnostics: []);

        await reporter.CompleteRunAsync(runId, health);

        var terminalHarvesters = reporter.CurrentSnapshot.Harvesters;
        Assert.Equal(2, terminalHarvesters.Count);
        Assert.All(terminalHarvesters, harvester => Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Terminal, harvester.Phase));
        Assert.Equal(2, terminalHarvesters.Single(harvester => harvester.HarvesterType == nameof(CSharpDocHarvester)).DocCount);
    }

    [Fact]
    public async Task ProgressReporter_ShouldKeepFailedRunTerminalWhenACompletionArrivesLate()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);

        await reporter.FailRunAsync(runId);
        var terminalSnapshot = reporter.CurrentSnapshot;
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Equal(AppSurfaceDocsHarvestRunState.Failed, reporter.CurrentSnapshot.State);
        Assert.Equal(terminalSnapshot, reporter.CurrentSnapshot);
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreLateHarvesterCallbacksAfterTerminalization()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());
        var terminalSnapshot = reporter.CurrentSnapshot;

        await reporter.HarvesterCompletedAsync(runId, nameof(MarkdownHarvester), DocHarvesterHealthStatus.Failed, 99);
        await reporter.HarvesterDocumentCountUpdatedAsync(runId, nameof(MarkdownHarvester), 99);
        await reporter.ActivityAsync(runId, "Late activity.");
        reporter.ReportOutputOnly(runId, nameof(MarkdownHarvester), 99);

        Assert.Equal(terminalSnapshot, reporter.CurrentSnapshot);
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
    public async Task ProgressReporter_ShouldNotBlockStateTransitionsOnAStalledHub()
    {
        var hub = new BlockingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await hub.FirstPublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await reporter.HarvesterStartedAsync(runId, nameof(MarkdownHarvester)).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Running", Assert.Single(reporter.CurrentSnapshot.Harvesters).Status);
        hub.Release.TrySetResult();
    }

    [Fact]
    public async Task ProgressReporter_ShouldDropNewPublicationsWhileAHubPublishIsStalled()
    {
        var hub = new BlockingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await hub.FirstPublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 100; index++)
        {
            await reporter.ActivityAsync(runId, $"Activity {index}");
        }

        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.False(hub.SecondPublicationStarted.Task.IsCompleted);

        hub.Release.TrySetResult();
        await hub.ThirdPublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, hub.PublicationCount);
    }

    [Fact]
    public async Task ProgressReporter_ShouldRetryTerminalPublicationAfterARecoverableFailure()
    {
        var hub = new FailingOnceTerminalRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());
        await hub.VisitPublicationSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldRetryCompletionVisitAfterARecoverableFailure()
    {
        var hub = new FailingOnceCompletionVisitRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(runId, CreateHealth());
        await hub.VisitPublicationSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressSession_ShouldCancelConfiguredPerDocumentPacing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = reporter.CreateSession(
            runId,
            nameof(MarkdownHarvester),
            testingDelayPerDocumentMilliseconds: 1,
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReportOutputOnlyAsync(1).AsTask());
        Assert.Equal(1, Assert.Single(reporter.CurrentSnapshot.Harvesters).DocCount);
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
    public async Task ProgressReporter_ShouldRecordQueuedStatusForNextRun_WhenQueuedBeforeBegin()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        Assert.Null(reporter.SuppressCompletionVisitForCurrentOrNextRun());
        await reporter.RebuildQueuedAsync(null);
        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var queuedSnapshot = reporter.CurrentSnapshot;
        await reporter.CompleteRunAsync(runId, CreateHealth());

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("A rebuild is queued and will start after this run finishes.", StringComparison.Ordinal));
        Assert.Equal("Harvesting (rebuild queued)", queuedSnapshot.Status);
        Assert.Contains(
            queuedSnapshot.Activity,
            item => item.Message.Contains("rebuild is queued", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldNotPublishDuplicateQueuedStatus_WhenDeferredRunWasMarkedOnBegin()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        reporter.SuppressCompletionVisitForCurrentOrNextRun();
        await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var publishCount = hub.Published.Count;

        await reporter.RebuildQueuedAsync(null);

        Assert.Equal(publishCount, hub.Published.Count);
        Assert.Equal("Harvesting (rebuild queued)", reporter.CurrentSnapshot.Status);
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreQueuedStatus_WhenNoDeferredRunWasMarked()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.RebuildQueuedAsync(null);

        Assert.Equal("Harvesting", reporter.CurrentSnapshot.Status);
        Assert.DoesNotContain(
            reporter.CurrentSnapshot.Activity,
            item => item.Message.Contains("rebuild is queued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProgressReporter_ShouldIgnoreDeferredQueuedStatus_WhenLaterRunReplacesSuppressedRun()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        reporter.SuppressCompletionVisitForCurrentOrNextRun();
        var firstRunId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        var currentRunId = await reporter.BeginRunAsync([nameof(CSharpDocHarvester)]);
        await reporter.RebuildQueuedAsync(null);

        Assert.NotEqual(firstRunId, currentRunId);
        Assert.Equal(currentRunId, reporter.CurrentSnapshot.RunId);
        Assert.Equal("Harvesting", reporter.CurrentSnapshot.Status);
        Assert.DoesNotContain(
            reporter.CurrentSnapshot.Activity,
            item => item.Message.Contains("rebuild is queued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProgressReporter_ShouldNotSuppressNextRunCompletion_WhenSuppressionIsRequestedAfterFailedRun()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var failedRunId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        await reporter.CompleteRunAsync(failedRunId, CreateHealth(DocHarvestHealthStatus.Failed));

        Assert.Equal(failedRunId, reporter.SuppressCompletionVisitForCurrentOrNextRun());
        var nextRunId = await reporter.BeginRunAsync([nameof(CSharpDocHarvester)]);
        await reporter.CompleteRunAsync(nextRunId, CreateHealth());

        Assert.Contains(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReporter_ShouldClearSuppressedRunId_WhenSuppressedRunFails()
    {
        var hub = new RecordingRazorWireStreamHub();
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamHub>(hub);
        using var provider = services.BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var runId = await reporter.BeginRunAsync([nameof(MarkdownHarvester)]);
        Assert.Equal(runId, reporter.SuppressCompletionVisitForCurrentOrNextRun());
        Assert.Equal(1, reporter.SuppressedCompletionVisitCount);

        await reporter.CompleteRunAsync(runId, CreateHealth(DocHarvestHealthStatus.Failed));

        Assert.Equal(0, reporter.SuppressedCompletionVisitCount);
        Assert.DoesNotContain(
            hub.Published,
            item => item.Message.Contains("action=\"rw-visit\"", StringComparison.Ordinal));
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

    private static DocHarvestHealthSnapshot CreateHealthWithDuplicateHarvesters()
    {
        return new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            "/tmp/repo",
            TotalHarvesters: 2,
            SuccessfulHarvesters: 2,
            FailedHarvesters: 0,
            TotalDocs: 3,
            [
                new DocHarvesterHealth("CustomHarvester", DocHarvesterHealthStatus.Succeeded, 1, null),
                new DocHarvesterHealth("CustomHarvester", DocHarvesterHealthStatus.Succeeded, 2, null)
            ],
            Diagnostics: []);
    }

    private sealed class RecordingRazorWireStreamHub : IRazorWireStreamHub
    {
        public List<PublishedMessage> Published { get; } = [];

        public TaskCompletionSource SecondPublicationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(string channel, string message)
        {
            Record(channel, message, options: null);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            Record(channel, message, options);
            return ValueTask.CompletedTask;
        }

        private void Record(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            Published.Add(new PublishedMessage(channel, message, options));
            if (Published.Count >= 2)
            {
                SecondPublicationObserved.TrySetResult();
            }
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

    private sealed class BlockingRazorWireStreamHub : IRazorWireStreamHub
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstPublicationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondPublicationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdPublicationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _publicationCount;

        public int PublicationCount => Volatile.Read(ref _publicationCount);

        public ValueTask PublishAsync(string channel, string message)
        {
            return PublishAsync(channel, message, options: null);
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            var publicationCount = Interlocked.Increment(ref _publicationCount);
            if (publicationCount == 1)
            {
                FirstPublicationStarted.TrySetResult();
            }
            else if (publicationCount == 2)
            {
                SecondPublicationStarted.TrySetResult();
            }
            else if (publicationCount == 3)
            {
                ThirdPublicationStarted.TrySetResult();
            }

            return new ValueTask(Release.Task);
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
        }
    }

    private sealed class FailingOnceTerminalRazorWireStreamHub : IRazorWireStreamHub
    {
        private int _terminalAttempts;

        public List<PublishedMessage> Published { get; } = [];

        public TaskCompletionSource VisitPublicationSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(string channel, string message)
        {
            return PublishAsync(channel, message, options: null);
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            if (message.Contains("data-appsurface-docs-harvest-complete=\"true\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _terminalAttempts) == 1)
            {
                throw new InvalidOperationException("The terminal snapshot failed once.");
            }

            Published.Add(new PublishedMessage(channel, message, options));
            if (message.Contains("action=\"rw-visit\"", StringComparison.Ordinal))
            {
                VisitPublicationSucceeded.TrySetResult();
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

    private sealed class FailingOnceCompletionVisitRazorWireStreamHub : IRazorWireStreamHub
    {
        private int _visitAttempts;

        public List<PublishedMessage> Published { get; } = [];

        public TaskCompletionSource VisitPublicationSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(string channel, string message)
        {
            return PublishAsync(channel, message, options: null);
        }

        public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            if (message.Contains("action=\"rw-visit\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _visitAttempts) == 1)
            {
                throw new InvalidOperationException("The completion visit failed once.");
            }

            Published.Add(new PublishedMessage(channel, message, options));
            if (message.Contains("action=\"rw-visit\"", StringComparison.Ordinal))
            {
                VisitPublicationSucceeded.TrySetResult();
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

    private sealed record PublishedMessage(
        string Channel,
        string Message,
        RazorWireStreamPublishOptions? Options);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
            _timestamp = checked(_timestamp + elapsed.Ticks);
        }
    }
}
