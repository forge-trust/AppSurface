using System.Threading.Channels;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestCoordinatorTests
{
    [Fact]
    public async Task WaitForCompletionAsync_WhenHarvestOutlivesBudget_ReturnsFalseAndKeepsSharedHarvestRunning()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new BlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        var completedWithinBudget = await coordinator.WaitForCompletionAsync(
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.False(completedWithinBudget);
        await harvester.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, harvester.CallCount);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Running, coordinator.CurrentProgress.State);

        harvester.Complete(new DocNode("Ready", "README.md", "<p>Ready</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(1, harvester.CallCount);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
        Assert.Equal(1, coordinator.CurrentProgress.TotalDocs);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenCalledConcurrently_StartsOnlyOneInitialHarvest()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new BlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        var firstWait = coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        var secondWait = coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        await harvester.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        harvester.Complete(new DocNode("Ready", "README.md", "<p>Ready</p>"));

        Assert.True(await firstWait);
        Assert.True(await secondWait);
        Assert.Equal(1, harvester.CallCount);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenInitialHarvestFaults_RetriesOnNextAttempt()
    {
        using var cache = new MemoryCache(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(1) });
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new StaticHarvester([new DocNode("Ready", "README.md", "<p>Ready</p>")]);
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        var sanitizeCalls = 0;
        A.CallTo(() => sanitizer.Sanitize(A<string>._))
            .ReturnsLazily(
                (string value) =>
                {
                    if (Interlocked.Increment(ref sanitizeCalls) == 1)
                    {
                        throw new InvalidOperationException("transient sanitize failure");
                    }

                    return value;
                });
        var coordinator = CreateCoordinator(
            harvester,
            cache,
            services,
            sanitizer,
            TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        await Task.Delay(30);

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(2, harvester.CallCount);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
        Assert.Equal(1, coordinator.CurrentProgress.TotalDocs);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTestingDelayIsConfigured_DelaysHarvesterAfterPublishingProgress()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var streamHub = new BlockingHarvesterStartedStreamHub();
        await using var services = new ServiceCollection()
            .AddSingleton<IRazorWireStreamHub>(streamHub)
            .BuildServiceProvider();
        var harvester = new BlockingHarvester();
        var delay = TimeSpan.FromMilliseconds(250);
        var tolerance = TimeSpan.FromMilliseconds(75);
        var coordinator = CreateCoordinator(
            harvester,
            cache,
            services,
            configureOptions: options => options.Harvest.TestingDelayPerHarvesterMilliseconds = (int)delay.TotalMilliseconds);

        try
        {
            Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
            await streamHub.HarvesterStartedPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(harvester.Started.Task.IsCompleted);
            Assert.Equal(0, harvester.CallCount);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            streamHub.ReleaseHarvesterStartedPublish();
            await harvester.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            Assert.True(
                sw.Elapsed >= delay - tolerance,
                $"Harvester started after {sw.Elapsed.TotalMilliseconds} ms.");
            harvester.Complete(new DocNode("Ready", "README.md", "<p>Ready</p>"));

            Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            Assert.Equal(1, harvester.CallCount);
        }
        finally
        {
            streamHub.ReleaseHarvesterStartedPublish();
            harvester.Complete(new DocNode("Ready", "README.md", "<p>Ready</p>"));
        }
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenHarvesterHasSynchronousStartupWork_ReturnsBeforeHarvestCompletes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new SynchronousSlowHarvester(TimeSpan.FromMilliseconds(500));
        var coordinator = CreateCoordinator(harvester, cache, services);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var completed = await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None);

        sw.Stop();
        Assert.False(completed);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Wait returned after {sw.Elapsed.TotalMilliseconds} ms.");
        await WaitUntilAsync(() => harvester.CallCount == 1);
        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenPreHarvestTestingDelayIsConfigured_PublishesRunBeforeAnyHarvesterRuns()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new BlockingHarvester();
        var coordinator = CreateCoordinator(
            harvester,
            cache,
            services,
            configureOptions: options => options.Harvest.TestingPreHarvestDelayMilliseconds = 500);

        Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
        await WaitUntilAsync(() => coordinator.CurrentProgress.State == AppSurfaceDocsHarvestRunState.Running);

        Assert.Equal(0, harvester.CallCount);
        await WaitUntilAsync(
            () => coordinator.CurrentProgress.Activity.Any(
                activity => activity.Message.Contains("before harvesters start", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            coordinator.CurrentProgress.Activity,
            activity => activity.Message.Contains("before harvesters start", StringComparison.OrdinalIgnoreCase));

        await harvester.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        harvester.Complete(new DocNode("Ready", "README.md", "<p>Ready</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(1, harvester.CallCount);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenPerDocumentTestingDelayIsConfigured_PublishesDocumentCountsBeforeCompletion()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new StaticHarvester(
            [
                new DocNode("One", "one.md", "<p>One</p>"),
                new DocNode("Two", "two.md", "<p>Two</p>"),
                new DocNode("Three", "three.md", "<p>Three</p>")
            ]);
        var coordinator = CreateCoordinator(
            harvester,
            cache,
            services,
            configureOptions: options => options.Harvest.TestingDelayPerDocumentMilliseconds = 500);

        Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
        var inProgressSnapshot = await WaitForProgressSnapshotAsync(
            () => coordinator.CurrentProgress,
            snapshot => snapshot.TotalDocs > 0 && snapshot.State == AppSurfaceDocsHarvestRunState.Running);

        Assert.Equal(AppSurfaceDocsHarvestRunState.Running, inProgressSnapshot.State);
        Assert.InRange(inProgressSnapshot.TotalDocs, 1, 2);
        Assert.Contains(
            inProgressSnapshot.Activity,
            activity => activity.Message.Contains("processed", StringComparison.OrdinalIgnoreCase));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
        Assert.Equal(3, coordinator.CurrentProgress.TotalDocs);
    }

    [Fact]
    public async Task RequestRebuildAsync_WhenNoHarvestIsActive_StartsImmediately()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new SequencedBlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        var result = await coordinator.RequestRebuildAsync(CancellationToken.None);

        Assert.Equal(AppSurfaceDocsHarvestRebuildRequestResult.Started, result);
        await WaitUntilAsync(() => harvester.CallCount == 1);
        Assert.True(coordinator.HasActiveOrQueuedHarvest);

        harvester.CompleteCall(1, new DocNode("Ready", "README.md", "<p>Ready</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(1, harvester.CallCount);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
    }

    [Fact]
    public async Task RequestRebuildAsync_WhenHarvestIsActive_QueuesOneSupersedingRun()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new SequencedBlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
        await WaitUntilAsync(() => harvester.CallCount == 1);

        var queued = await coordinator.RequestRebuildAsync(CancellationToken.None);
        var alreadyQueued = await coordinator.RequestRebuildAsync(CancellationToken.None);

        Assert.Equal(AppSurfaceDocsHarvestRebuildRequestResult.Queued, queued);
        Assert.Equal(AppSurfaceDocsHarvestRebuildRequestResult.AlreadyQueued, alreadyQueued);
        Assert.True(coordinator.HasActiveOrQueuedHarvest);

        harvester.CompleteCall(1, new DocNode("First", "first.md", "<p>First</p>"));
        await WaitUntilAsync(() => harvester.CallCount == 2);

        harvester.CompleteCall(2, new DocNode("Second", "second.md", "<p>Second</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(2, harvester.CallCount);
        Assert.Equal(1, coordinator.CurrentProgress.TotalDocs);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
    }

    [Fact]
    public async Task RequestRebuildAsync_WhenActiveRunFailsStillStartsQueuedRebuild()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new SequencedBlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
        await WaitUntilAsync(() => harvester.CallCount == 1);
        Assert.Equal(AppSurfaceDocsHarvestRebuildRequestResult.Queued, await coordinator.RequestRebuildAsync(CancellationToken.None));

        harvester.FailCall(1, new InvalidOperationException("first run failed"));
        await WaitUntilAsync(() => harvester.CallCount == 2);
        harvester.CompleteCall(2, new DocNode("Recovered", "README.md", "<p>Recovered</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(2, harvester.CallCount);
        Assert.Equal(AppSurfaceDocsHarvestRunState.Completed, coordinator.CurrentProgress.State);
    }

    [Fact]
    public async Task RequestRebuildAsync_WhenManyPostsRace_QueuesOnlyOneRebuild()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var services = new ServiceCollection().BuildServiceProvider();
        var harvester = new SequencedBlockingHarvester();
        var coordinator = CreateCoordinator(harvester, cache, services);

        Assert.False(await coordinator.WaitForCompletionAsync(TimeSpan.Zero, CancellationToken.None));
        await WaitUntilAsync(() => harvester.CallCount == 1);

        var requests = Enumerable.Range(0, 20)
            .Select(_ => coordinator.RequestRebuildAsync(CancellationToken.None).AsTask())
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, results.Count(result => result == AppSurfaceDocsHarvestRebuildRequestResult.Queued));
        Assert.Equal(19, results.Count(result => result == AppSurfaceDocsHarvestRebuildRequestResult.AlreadyQueued));

        harvester.CompleteCall(1, new DocNode("First", "first.md", "<p>First</p>"));
        await WaitUntilAsync(() => harvester.CallCount == 2);
        harvester.CompleteCall(2, new DocNode("Second", "second.md", "<p>Second</p>"));

        Assert.True(await coordinator.WaitForCompletionAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(2, harvester.CallCount);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(StackOverflowException), true)]
    [InlineData(typeof(AccessViolationException), true)]
    public void IsFatalHarvestException_ShouldClassifyProcessFatalFailures(Type exceptionType, bool expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expected, AppSurfaceDocsHarvestCoordinator.IsFatalHarvestException(exception));
    }

    private static AppSurfaceDocsHarvestCoordinator CreateCoordinator(
        IDocHarvester harvester,
        IMemoryCache cache,
        IServiceProvider services,
        IAppSurfaceDocsHtmlSanitizer? sanitizer = null,
        TimeSpan? failureCacheDuration = null,
        Action<AppSurfaceDocsOptions>? configureOptions = null)
    {
        var environment = new TestWebHostEnvironment();
        if (sanitizer is null)
        {
            sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
            A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string value) => value);
        }

        var progress = new AppSurfaceDocsHarvestProgressReporter(
            services,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);

        var options = new AppSurfaceDocsOptions();
        configureOptions?.Invoke(options);
        var aggregator = new DocAggregator(
            new[] { harvester },
            options,
            environment,
            failureCacheDuration.HasValue
                ? new Memo(cache, failureCacheDuration.Value)
                : new Memo(cache),
            sanitizer,
            new DocsUrlBuilder(options),
            NullLogger<DocAggregator>.Instance,
            progress);

        return new AppSurfaceDocsHarvestCoordinator(
            aggregator,
            progress);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static async Task<AppSurfaceDocsHarvestProgressSnapshot> WaitForProgressSnapshotAsync(
        Func<AppSurfaceDocsHarvestProgressSnapshot> getSnapshot,
        Func<AppSurfaceDocsHarvestProgressSnapshot, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var snapshot = getSnapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class BlockingHarvester : IDocHarvester
    {
        private readonly TaskCompletionSource<IReadOnlyList<DocNode>> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            return _completed.Task;
        }

        public void Complete(params DocNode[] docs)
        {
            _completed.TrySetResult(docs);
        }
    }

    private sealed class SequencedBlockingHarvester : IDocHarvester
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<IReadOnlyList<DocNode>>> _calls = [];

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count;
                }
            }
        }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _calls.Add(completion);
            }

            return completion.Task;
        }

        public void CompleteCall(int callNumber, params DocNode[] docs)
        {
            GetCall(callNumber).TrySetResult(docs);
        }

        public void FailCall(int callNumber, Exception exception)
        {
            GetCall(callNumber).TrySetException(exception);
        }

        private TaskCompletionSource<IReadOnlyList<DocNode>> GetCall(int callNumber)
        {
            lock (_gate)
            {
                return _calls[callNumber - 1];
            }
        }
    }

    private sealed class BlockingHarvesterStartedStreamHub : IRazorWireStreamHub
    {
        private readonly TaskCompletionSource _releaseHarvesterStartedPublish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HarvesterStartedPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(string channel, string message)
        {
            return PublishAsync(channel, message, options: null);
        }

        public async ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
        {
            if (message.Contains("BlockingHarvester started.", StringComparison.Ordinal))
            {
                HarvesterStartedPublished.TrySetResult();
                await _releaseHarvesterStartedPublish.Task;
            }
        }

        public void ReleaseHarvesterStartedPublish()
        {
            _releaseHarvesterStartedPublish.TrySetResult();
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
        }
    }

    private sealed class StaticHarvester(IReadOnlyList<DocNode> docs) : IDocHarvester
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(docs);
        }
    }

    private sealed class SynchronousSlowHarvester(TimeSpan delay) : IDocHarvester
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Thread.Sleep(delay);
            return Task.FromResult<IReadOnlyList<DocNode>>(
                [new DocNode("Ready", "README.md", "<p>Ready</p>")]);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public string EnvironmentName { get; set; } = Environments.Development;

        public string WebRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
