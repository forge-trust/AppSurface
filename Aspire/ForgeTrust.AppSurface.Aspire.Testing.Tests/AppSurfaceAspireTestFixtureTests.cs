using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

[Collection(AspireEnvironmentCollection.Name)]
public sealed partial class AppSurfaceAspireTestFixtureTests : IDisposable
{
    private readonly string? _originalDcpPath = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
    private readonly string? _originalDashboardPath = Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_PATH");

    public AppSurfaceAspireTestFixtureTests()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
    }

    [Fact]
    public async Task StartAsync_CompletesOrderedOwnedLifecycleAndDisposesProbeBeforeReturning()
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var events = new List<string>();
        var handler = new StubHandler(_ => new(HttpStatusCode.OK), () => events.Add("client-dispose"));
        using var client = new HttpClient(handler);
        var calls = new List<CancellationToken>();
        var operations = CreateOperations(application, events, calls);
        var factoryCalls = 0;
        var options = new AppSurfaceAspireStartupOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(20),
            CreateProbeClient = _ => client,
        };

        await using var fixture = await AppSurfaceAspireTestFixture.StartAsync(
            _ =>
            {
                factoryCalls++;
                events.Add("factory");
                return Task.FromResult(builder);
            },
            Target,
            options,
            CancellationToken.None,
            timeProvider: TimeProvider.System,
            operations: operations);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(
            ["factory", "build", "start", "health:api", "endpoint:api:http", "probe", "client-dispose"],
            events);
        Assert.All(calls, token => Assert.True(token.CanBeCanceled));
        Assert.Equal(new Uri("http://127.0.0.1:4567/"), fixture.Endpoint);
        Assert.Same(application, fixture.Application);
        Assert.True(handler.IsDisposed);

        await fixture.DisposeAsync();
        Assert.Equal(1, events.Count(item => item == "cleanup"));
    }

    [Theory]
    [InlineData("invalid-duration")]
    [InlineData("invalid-path")]
    [InlineData("null-http-options")]
    public async Task StartAsync_ValidatesBeforeFactoryAndOwnership(string invalidCase)
    {
        var factoryCalls = 0;
        var options = new AppSurfaceAspireStartupOptions();
        var target = Target;
        switch (invalidCase)
        {
            case "invalid-duration":
                options = new() { StartupTimeout = TimeSpan.Zero };
                break;
            case "invalid-path":
                target = Target with { ProbePath = "//other.example/health" };
                break;
            case "null-http-options":
                options = new() { Http = null! };
                break;
        }

        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ =>
            {
                factoryCalls++;
                return Task.FromResult(CreateBuilder());
            },
            target,
            options,
            CancellationToken.None,
            TimeProvider.System,
            operations: new AppSurfaceAspireTestFixture.FixtureOperations()));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task StartAsync_PreCanceledCallerDoesNotInvokeFactoryOrTransferBuilder()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factoryCalls = 0;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ =>
            {
                factoryCalls++;
                return Task.FromResult(CreateBuilder());
            },
            Target,
            null,
            cancellationToken: cancellation.Token,
            timeProvider: TimeProvider.System,
            operations: new AppSurfaceAspireTestFixture.FixtureOperations()));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, factoryCalls);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("build")]
    [InlineData("start")]
    [InlineData("health")]
    [InlineData("endpoint")]
    [InlineData("probe")]
    public async Task StartAsync_PreservesPrimaryFailureAndCleansTransferredBuilder(string failurePhase)
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var events = new List<string>();
        var primary = new InvalidOperationException($"primary-{failurePhase}");
        var cleanupFailure = new InvalidOperationException("sensitive-cleanup-detail");
        var operations = CreateOperations(application, events, []) with
        {
            CleanupAsync = _ =>
            {
                events.Add("cleanup");
                throw cleanupFailure;
            },
        };
        if (failurePhase == "build")
        {
            operations = operations with { BuildAsync = (_, _) => throw primary };
        }
        else if (failurePhase == "start")
        {
            operations = operations with { StartAsync = (_, _) => throw primary };
        }
        else if (failurePhase == "health")
        {
            operations = operations with { WaitForResourceHealthyAsync = (_, _, _) => throw primary };
        }
        else if (failurePhase == "endpoint")
        {
            operations = operations with { GetEndpoint = (_, _, _) => throw new InvalidOperationException("native endpoint error") };
        }
        else if (failurePhase == "probe")
        {
            operations = operations with { WaitForHttpAsync = (_, _, _, _, _) => throw primary };
        }

        Exception thrown = failurePhase == "endpoint"
            ? await Assert.ThrowsAsync<ReadinessException>(() => AppSurfaceAspireTestFixture.StartAsync(
                _ =>
                {
                    events.Add("factory");
                    return Task.FromResult(builder);
                },
                Target,
                new AppSurfaceAspireStartupOptions { CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))) },
                CancellationToken.None,
                TimeProvider.System,
                operations))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ =>
            {
                events.Add("factory");
                if (failurePhase == "factory")
                {
                    throw primary;
                }

                return Task.FromResult(builder);
            },
            Target,
            new AppSurfaceAspireStartupOptions { CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))) },
            CancellationToken.None,
            TimeProvider.System,
            operations));

        if (failurePhase == "endpoint")
        {
            var readinessException = Assert.IsType<ReadinessException>(thrown);
            Assert.Equal(ReadinessPhase.EndpointResolution, readinessException.Phase);
            Assert.Equal(ReadinessFailureCategory.EndpointUnavailable, readinessException.Category);
        }
        else
        {
            Assert.Same(primary, thrown);
        }
        if (failurePhase != "factory")
        {
            Assert.Equal(ReadinessFailureCategory.CleanupFailure, thrown.Data["AppSurface.Readiness.CleanupFailure"]);
            Assert.DoesNotContain("sensitive-cleanup-detail", string.Join(" ", thrown.Data.Values));
            Assert.Equal(1, events.Count(item => item == "cleanup"));
        }
        else
        {
            Assert.DoesNotContain("cleanup", events);
        }
    }

    [Fact]
    public async Task StartAsync_RejectsAlreadyBuiltBuilderAndCleansItsTransferredOwnership()
    {
        var builder = CreateBuilder();
        _ = await builder.BuildAsync();
        var cleanupCalls = 0;
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            CleanupAsync = _ =>
            {
                cleanupCalls++;
                return ValueTask.CompletedTask;
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder),
            Target,
            null,
            CancellationToken.None,
            timeProvider: TimeProvider.System,
            operations: operations));

        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public async Task StartAsync_CallerCancellationDuringHealthUsesOriginalTokenAndCleansBuilder()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = CreateBuilder();
        var cleanupCalls = 0;
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            StartAsync = (_, _) => Task.CompletedTask,
            WaitForResourceHealthyAsync = async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            CleanupAsync = _ =>
            {
                cleanupCalls++;
                return ValueTask.CompletedTask;
            },
        };

        var start = AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder),
            Target,
            null,
            cancellationToken: cancellation.Token,
            timeProvider: TimeProvider.System,
            operations: operations);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public async Task StartAsync_DeadlineDuringStartRetainsPhaseAndCleansBuilder()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = CreateBuilder();
        var cleanupCalls = 0;
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            StartAsync = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            CleanupAsync = _ =>
            {
                cleanupCalls++;
                return ValueTask.CompletedTask;
            },
        };

        var start = AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder),
            Target,
            new AppSurfaceAspireStartupOptions { StartupTimeout = TimeSpan.FromSeconds(5) },
            CancellationToken.None,
            time,
            operations: operations);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<ReadinessException>(() => start);
        Assert.Equal(ReadinessPhase.Start, exception.Phase);
        Assert.Equal(ReadinessFailureCategory.Deadline, exception.Category);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentAndReentrantCallersJoinOneCleanupAndShareFailure()
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var enteredCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("cleanup failure");
        var cleanupCalls = 0;
        AppSurfaceAspireTestFixture? fixture = null;
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            StartAsync = (_, _) => Task.CompletedTask,
            WaitForResourceHealthyAsync = (_, _, _) => Task.CompletedTask,
            WaitForHttpAsync = (_, _, _, _, _) => Task.CompletedTask,
            GetEndpoint = (_, _, _) => new Uri("http://127.0.0.1:4567/"),
            CleanupAsync = async _ =>
            {
                cleanupCalls++;
                enteredCleanup.SetResult();
                var reentrant = fixture!.DisposeAsync().AsTask();
                Assert.True(reentrant.IsCompletedSuccessfully);
                await releaseCleanup.Task;
                throw cleanupFailure;
            },
        };
        fixture = await AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder),
            Target,
            new AppSurfaceAspireStartupOptions { CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))) },
            CancellationToken.None,
            TimeProvider.System,
            operations: operations);

        var first = fixture.DisposeAsync().AsTask();
        await enteredCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        releaseCleanup.SetResult();

        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());
        Assert.Equal(1, cleanupCalls);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", _originalDcpPath);
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", _originalDashboardPath);
    }

    private static AspireHttpReadinessTarget Target => new("api", "http", "/health");

    private static AppSurfaceAspireProfileTestingBuilder CreateBuilder() => new(
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AppSurfaceAspireTestFixtureTests).Assembly.GetName().Name,
            ProjectDirectory = AppContext.BaseDirectory,
            DisableDashboard = true,
        }),
        A.Fake<IAsyncDisposable>());

    private static DistributedApplication CreateApplication() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AppSurfaceAspireTestFixtureTests).Assembly.GetName().Name,
            ProjectDirectory = AppContext.BaseDirectory,
            DisableDashboard = true,
        }).Build();

    private static AppSurfaceAspireTestFixture.FixtureOperations CreateOperations(
        DistributedApplication application,
        List<string> events,
        List<CancellationToken> tokens) => new()
        {
            BuildAsync = (_, token) =>
            {
                events.Add("build");
                tokens.Add(token);
                return Task.FromResult(application);
            },
            StartAsync = (_, token) =>
            {
                events.Add("start");
                tokens.Add(token);
                return Task.CompletedTask;
            },
            WaitForResourceHealthyAsync = (_, resourceName, token) =>
            {
                events.Add($"health:{resourceName}");
                tokens.Add(token);
                return Task.CompletedTask;
            },
            GetEndpoint = (_, resourceName, endpointName) =>
            {
                events.Add($"endpoint:{resourceName}:{endpointName}");
                return new Uri("http://127.0.0.1:4567/");
            },
            WaitForHttpAsync = async (client, endpoint, path, settings, deadline) =>
            {
                events.Add("probe");
                tokens.Add(deadline.Token);
                await HttpReadinessProbe.WaitCoreAsync(client, endpoint, path, settings, deadline);
            },
            CleanupAsync = _ =>
            {
                events.Add("cleanup");
                return ValueTask.CompletedTask;
            },
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        Action? onDispose = null) : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            onDispose?.Invoke();
            base.Dispose(disposing);
        }
    }
}
