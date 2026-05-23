using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestFailurePreflightServiceTests : IDisposable
{
    private readonly IWebHostEnvironment _environment = A.Fake<IWebHostEnvironment>();
    private readonly IAppSurfaceDocsHtmlSanitizer _sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
    private readonly ILogger<DocAggregator> _aggregatorLogger = A.Fake<ILogger<DocAggregator>>();
    private readonly RecordingLogger<AppSurfaceDocsHarvestFailurePreflightService> _preflightLogger = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Memo _memo;

    public AppSurfaceDocsHarvestFailurePreflightServiceTests()
    {
        _memo = new Memo(_cache);
        A.CallTo(() => _environment.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => _sanitizer.Sanitize(A<string>._)).ReturnsLazily((string value) => value);
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var aggregator = CreateAggregator([]);
        var options = new AppSurfaceDocsOptions();

        Assert.Throws<ArgumentNullException>(
            "options",
            () => new AppSurfaceDocsHarvestFailurePreflightService(null!, aggregator, _preflightLogger));
        Assert.Throws<ArgumentNullException>(
            "aggregator",
            () => new AppSurfaceDocsHarvestFailurePreflightService(options, null!, _preflightLogger));
        Assert.Throws<ArgumentNullException>(
            "logger",
            () => new AppSurfaceDocsHarvestFailurePreflightService(options, aggregator, null!));
    }

    [Fact]
    public async Task StartAsync_ShouldNotHarvest_WhenStrictModeIsDisabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: false);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StartAsync_ShouldNotHarvest_WhenHarvestOptionsAreNull()
    {
        var harvester = A.Fake<IDocHarvester>();
        var aggregator = CreateAggregator([harvester]);
        var service = new AppSurfaceDocsHarvestFailurePreflightService(
            new AppSurfaceDocsOptions { Harvest = null! },
            aggregator,
            _preflightLogger);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenStrictModeIsEnabledAndHarvestIsHealthy()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: true);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ShouldStartBackgroundWarmup_WhenStrictModeIsDisabledAndStartupModeIsBackground()
    {
        var harvester = new SignalingHarvester();
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: false, AppSurfaceDocsHarvestStartupMode.Background);

        await service.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await harvester.Started.Task.WaitAsync(cts.Token);
        harvester.Complete();
    }

    [Fact]
    public async Task StartAsync_ShouldBlockWarmup_WhenStartupModeIsBlocking()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: false, AppSurfaceDocsHarvestStartupMode.Blocking);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenStrictModeIsEnabledAndHarvestIsEmpty()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(Array.Empty<DocNode>());
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: true);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenStrictModeIsEnabledAndHarvestIsDegraded()
    {
        var failingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => failingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("raw failure"));
        var workingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => workingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        var aggregator = CreateAggregator([failingHarvester, workingHarvester]);
        var service = CreateService(aggregator, failOnFailure: true);

        await service.StartAsync(CancellationToken.None);

        A.CallTo(() => failingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => workingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ShouldThrowRedactedException_WhenStrictModeIsEnabledAndHarvestFailed()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("raw failure"));
        var aggregator = CreateAggregator([harvester], repositoryRoot: "/tmp/private-repo");
        var service = CreateService(aggregator, failOnFailure: true);

        var exception = await Assert.ThrowsAsync<AppSurfaceDocsHarvestFailedException>(
            async () => await service.StartAsync(CancellationToken.None));

        Assert.Equal(DocHarvestHealthStatus.Failed, exception.Summary.Status);
        Assert.NotEqual(default, exception.Summary.GeneratedUtc);
        Assert.Equal(1, exception.Summary.TotalHarvesters);
        Assert.Equal(1, exception.Summary.FailedHarvesters);
        Assert.DoesNotContain("/tmp/private-repo", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw failure", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DocHarvestDiagnosticCodes.HarvesterFailed, exception.Message, StringComparison.Ordinal);
        Assert.Contains(DocHarvestDiagnosticCodes.AllFailed, exception.Message, StringComparison.Ordinal);
        Assert.All(exception.Summary.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.Fix)));
        AssertCriticalLogIncludesException(_preflightLogger, exception);
    }

    [Fact]
    public async Task StartAsync_ShouldReuseCachedSnapshot_WhenStrictModeWarmsHealth()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: true);

        await service.StartAsync(CancellationToken.None);
        var docs = await aggregator.GetDocsAsync();

        Assert.Single(docs);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ShouldPropagateCancellation_WithoutPoisoningSharedSnapshot()
    {
        var releaseHarvester = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => releaseHarvester.Task);
        var aggregator = CreateAggregator([harvester]);
        var service = CreateService(aggregator, failOnFailure: true);
        using var cts = new CancellationTokenSource();

        var preflight = service.StartAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await preflight);
        releaseHarvester.SetResult([new DocNode("Recovered", "docs/recovered.md", "<p>Recovered</p>")]);
        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Healthy, health.Status);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AppSurfaceDocsHarvestFailedException_ShouldOmitRepositoryRootRawExceptionAndCause()
    {
        var health = new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Failed,
            new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            "/tmp/private-repo",
            TotalHarvesters: 1,
            SuccessfulHarvesters: 0,
            FailedHarvesters: 1,
            TotalDocs: 0,
            [
                new DocHarvesterHealth(
                    "SecretHarvester",
                    DocHarvesterHealthStatus.Failed,
                    DocCount: 0,
                    new DocHarvestDiagnostic(
                        DocHarvestDiagnosticCodes.HarvesterFailed,
                        DocHarvestDiagnosticSeverity.Error,
                        "SecretHarvester",
                        "An AppSurface Docs harvester failed.",
                        "raw cause with /tmp/private-repo and stack trace",
                        "Inspect the host logs."))
            ],
            [
                new DocHarvestDiagnostic(
                    DocHarvestDiagnosticCodes.HarvesterFailed,
                    DocHarvestDiagnosticSeverity.Error,
                    "SecretHarvester",
                    "An AppSurface Docs harvester failed.",
                    "raw cause with /tmp/private-repo and stack trace",
                    "Inspect the host logs.")
            ]);

        var exception = new AppSurfaceDocsHarvestFailedException(health);

        Assert.DoesNotContain("/tmp/private-repo", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw cause", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack trace", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SecretHarvester", Assert.Single(exception.Summary.Diagnostics).HarvesterType);
        Assert.Contains(DocHarvestDiagnosticCodes.HarvesterFailed, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsHarvestFailedException_ShouldTolerateNullSummaryDiagnostics()
    {
        var summary = new DocHarvestFailureSummary(
            DocHarvestHealthStatus.Failed,
            new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            TotalHarvesters: 1,
            SuccessfulHarvesters: 0,
            FailedHarvesters: 1,
            TotalDocs: 0,
            Diagnostics: null!);

        var exception = new AppSurfaceDocsHarvestFailedException(summary);

        Assert.DoesNotContain("Diagnostics:", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _memo.Dispose();
        _cache.Dispose();
    }

    private DocAggregator CreateAggregator(
        IEnumerable<IDocHarvester> harvesters,
        string? repositoryRoot = null)
    {
        return new DocAggregator(
            harvesters,
            new AppSurfaceDocsOptions
            {
                Source = new AppSurfaceDocsSourceOptions
                {
                    RepositoryRoot = repositoryRoot ?? Path.GetTempPath()
                }
            },
            _environment,
            _memo,
            _sanitizer,
            _aggregatorLogger);
    }

    private AppSurfaceDocsHarvestFailurePreflightService CreateService(
        DocAggregator aggregator,
        bool failOnFailure,
        AppSurfaceDocsHarvestStartupMode startupMode = AppSurfaceDocsHarvestStartupMode.Disabled)
    {
        return new AppSurfaceDocsHarvestFailurePreflightService(
            new AppSurfaceDocsOptions
            {
                Harvest = new AppSurfaceDocsHarvestOptions
                {
                    FailOnFailure = failOnFailure,
                    StartupMode = startupMode
                }
            },
            aggregator,
            _preflightLogger);
    }

    private sealed class SignalingHarvester : IDocHarvester
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<IReadOnlyList<DocNode>> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _completed.Task;
        }

        public void Complete()
        {
            _completed.TrySetResult([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        }
    }

    private static void AssertCriticalLogIncludesException(
        RecordingLogger<AppSurfaceDocsHarvestFailurePreflightService> logger,
        AppSurfaceDocsHarvestFailedException exception)
    {
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Critical
                && ReferenceEquals(entry.Exception, exception)
                && (entry.Message?.Contains(
                    "AppSurface Docs strict harvest failed during startup.",
                    StringComparison.Ordinal) ?? false));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
