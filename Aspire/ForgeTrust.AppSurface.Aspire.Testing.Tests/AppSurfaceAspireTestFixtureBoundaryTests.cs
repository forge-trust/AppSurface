using System.Collections;
using System.Net;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

public sealed partial class AppSurfaceAspireTestFixtureTests
{
    // Value: protects=fatal client, builder and diagnostic-storage identity through nested cleanup aggregates;
    // fails_when=an outer aggregate is downgraded after an ordinary failure; why_new=direct fatal cases miss wrapped failures;
    // seam=existing FixtureOperations used by StartAsync; no_claim=synthetic exceptions do not simulate resource exhaustion.
    [Theory]
    [InlineData("client-success", false)]
    [InlineData("client-primary", false)]
    [InlineData("client-before-probe", false)]
    [InlineData("builder-primary", false)]
    [InlineData("metadata", false)]
    [InlineData("client-success", true)]
    [InlineData("client-primary", true)]
    [InlineData("client-before-probe", true)]
    [InlineData("builder-primary", true)]
    [InlineData("metadata", true)]
    public async Task FatalCleanupFailures_PropagateWithoutWrappingOrSuppression(string phase, bool aggregate)
    {
        foreach (var fatal in new Exception[] { new OutOfMemoryException(), new StackOverflowException(), new AccessViolationException() })
        {
            var cleanupFailure = aggregate
                ? new AggregateException(new InvalidOperationException("ordinary nested cleanup"), new AggregateException(fatal))
                : fatal;
            await using var application = CreateApplication();
            await using var builder = CreateBuilder();
            using var caller = new CancellationTokenSource();
            var primary = phase == "metadata" ? new FatalFixtureDataException(cleanupFailure) : new Exception("ordinary startup failure");
            var clientDisposals = 0;
            var builderCleanups = 0;
            using var client = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK), () =>
            {
                clientDisposals++;
                if (phase.StartsWith("client", StringComparison.Ordinal)) throw cleanupFailure;
                if (phase == "metadata") throw new InvalidOperationException("ordinary cleanup failure");
            }));
            var operations = CreateOperations(application, [], []) with
            {
                WaitForHttpAsync = (_, _, _, _, _) => phase == "client-success" ? Task.CompletedTask : Task.FromException(primary),
                CleanupAsync = _ =>
                {
                    builderCleanups++;
                    if (phase == "builder-primary") throw cleanupFailure;
                    return ValueTask.CompletedTask;
                },
            };

            var error = await Record.ExceptionAsync(() => AppSurfaceAspireTestFixture.StartAsync(
                _ => Task.FromResult(builder), Target,
                new()
                {
                    CreateProbeClient = _ =>
                    {
                        if (phase == "client-before-probe") caller.Cancel();
                        return client;
                    },
                }, caller.Token, TimeProvider.System, operations));

            Assert.Same(fatal, error);
            Assert.Equal(1, clientDisposals);
            Assert.Equal(phase == "client-before-probe" ? 0 : 1, builderCleanups);
        }
    }

    private sealed class FatalFixtureDataException(Exception fatal) : Exception
    {
        public override IDictionary Data => throw fatal;
    }

    [Theory]
    [InlineData("null-factory")]
    [InlineData("null-target")]
    [InlineData("blank-resource")]
    [InlineData("blank-endpoint")]
    [InlineData("null-clock")]
    [InlineData("null-operations")]
    public async Task InvalidInputs_NeverInvokeFactory(string invalid)
    {
        var calls = 0;
        Func<CancellationToken, Task<AppSurfaceAspireProfileTestingBuilder>> factory = _ =>
        {
            calls++;
            throw new InvalidOperationException("must not run");
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppSurfaceAspireTestFixture.StartAsync(
            invalid == "null-factory" ? null! : factory,
            invalid == "null-target" ? null! : Target with
            {
                ResourceName = invalid == "blank-resource" ? " " : Target.ResourceName,
                EndpointName = invalid == "blank-endpoint" ? " " : Target.EndpointName,
            }, null, default,
            invalid == "null-clock" ? null! : TimeProvider.System,
            invalid == "null-operations" ? null! : new()));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disposed")]
    public async Task InvalidFactoryResult_IsRejectedAndTransferredBuilderIsCleaned(string result)
    {
        var builder = CreateBuilder();
        if (result == "disposed") await builder.DisposeAsync();
        var cleanup = 0;
        var error = await Record.ExceptionAsync(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(result == "null" ? null! : builder), Target, null, default, TimeProvider.System,
            new() { CleanupAsync = _ => { cleanup++; return ValueTask.CompletedTask; } }));
        if (result == "disposed") Assert.IsType<ObjectDisposedException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(result == "null" ? 0 : 1, cleanup);
    }

    // Value: protects=invalid build result cleanup; fails_when=null application continues startup;
    // why_new=factory-null coverage does not exercise the transferred builder's build result;
    // seam=existing FixtureOperations; no_claim=native builder cannot normally return null.
    [Fact]
    public async Task NullBuildResult_CleansTransferredBuilderWithoutStarting()
    {
        var cleaned = 0;
        var started = 0;
        var builder = CreateBuilder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target, null, default, TimeProvider.System,
            new()
            {
                BuildAsync = (_, _) => Task.FromResult<global::Aspire.Hosting.DistributedApplication>(null!),
                StartAsync = (_, _) => { started++; return Task.CompletedTask; },
                CleanupAsync = _ => { cleaned++; return ValueTask.CompletedTask; },
            }));
        Assert.Contains("did not return an application", error.Message);
        Assert.Equal(0, started);
        Assert.Equal(1, cleaned);
        await builder.DisposeAsync();
    }

    // Value: protects=primary error survives unavailable diagnostic storage;
    // fails_when=writing cleanup metadata replaces the original exception;
    // why_new=ordinary Exception.Data accepts writes; seam=existing FixtureOperations;
    // no_claim=caller-owned exception text is not redacted.
    [Fact]
    public async Task UnavailableExceptionData_DoesNotReplacePrimaryWhenClientAndBuilderCleanupFail()
    {
        var primary = new UnavailableDataException();
        var application = CreateApplication();
        var builder = CreateBuilder();
        var disposed = 0;
        var cleaned = 0;
        var operations = CreateOperations(application, [], []) with
        {
            WaitForHttpAsync = (_, _, _, _, _) => Task.FromException(primary),
            CleanupAsync = _ => { cleaned++; throw new InvalidOperationException("secondary builder detail"); },
        };
        var error = await Record.ExceptionAsync(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target,
            new()
            {
                CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK), () =>
            {
                disposed++;
                throw new InvalidOperationException("secondary client detail");
            }))
            }, default, TimeProvider.System, operations));
        Assert.Same(primary, error);
        Assert.Equal(1, disposed);
        Assert.Equal(1, cleaned);
        Assert.DoesNotContain("secondary", error!.ToString());
        await builder.DisposeAsync();
        await application.DisposeAsync();
    }

    private sealed class UnavailableDataException : Exception
    {
        public override IDictionary Data => throw new NotSupportedException("Caller diagnostic storage is unavailable.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFactoryTransfer_IsOwnedBeforeOriginalBudgetCheck(bool callerCancels)
    {
        var time = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        var builder = CreateBuilder();
        var cleanup = 0;
        var result = await Record.ExceptionAsync(() => AppSurfaceAspireTestFixture.StartAsync(
            _ =>
            {
                time.Advance(TimeSpan.FromSeconds(5));
                if (callerCancels) caller.Cancel();
                return Task.FromResult(builder);
            }, Target, new() { StartupTimeout = TimeSpan.FromSeconds(5) }, caller.Token, time,
            new() { CleanupAsync = _ => { cleanup++; return ValueTask.CompletedTask; } }));
        if (callerCancels) Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(result).CancellationToken);
        else Assert.Equal(ReadinessPhase.Creation, Assert.IsType<ReadinessException>(result).Phase);
        Assert.Equal(1, cleanup);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("start")]
    [InlineData("health")]
    [InlineData("endpoint")]
    [InlineData("http")]
    public async Task OriginalDeadline_IsNotRestartedByAnyStartupPhase(string expireAt)
    {
        var time = new FakeTimeProvider();
        var application = CreateApplication();
        var builder = CreateBuilder();
        var cleaned = 0;
        void Expire(string phase) { if (phase == expireAt) time.Advance(TimeSpan.FromSeconds(5)); }
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            BuildAsync = (_, _) => { Expire("build"); return Task.FromResult(application); },
            StartAsync = (_, _) => { Expire("start"); return Task.CompletedTask; },
            WaitForResourceHealthyAsync = (_, _, _) => { Expire("health"); return Task.CompletedTask; },
            GetEndpoint = (_, _, _) => { Expire("endpoint"); return new Uri("http://localhost/"); },
            WaitForHttpAsync = (_, _, _, _, _) => { Expire("http"); return Task.CompletedTask; },
            CleanupAsync = _ => { cleaned++; return ValueTask.CompletedTask; },
        };
        var error = await Assert.ThrowsAsync<ReadinessException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target,
            new() { StartupTimeout = TimeSpan.FromSeconds(5), CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))) },
            default, time, operations));
        Assert.Equal(ReadinessFailureCategory.Deadline, error.Category);
        Assert.Equal(expireAt switch { "build" => ReadinessPhase.Build, "start" => ReadinessPhase.Start, "health" => ReadinessPhase.ResourceHealth, "endpoint" => ReadinessPhase.EndpointResolution, _ => ReadinessPhase.Http }, error.Phase);
        Assert.Equal(1, cleaned);
        await application.DisposeAsync();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("relative")]
    [InlineData("ftp")]
    public async Task EndpointFailures_AreSafeAndCleanTransferredOwner(string shape)
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var cleaned = 0;
        var operations = CreateOperations(application, [], []) with
        {
            GetEndpoint = (_, _, _) => shape switch { "null" => null!, "relative" => new Uri("health", UriKind.Relative), _ => new Uri("ftp://sensitive.invalid/") },
            CleanupAsync = _ => { cleaned++; return ValueTask.CompletedTask; },
        };
        var error = await Assert.ThrowsAsync<ReadinessException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target, null, default, TimeProvider.System, operations));
        Assert.Equal(ReadinessFailureCategory.EndpointUnavailable, error.Category);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Equal(1, cleaned);
        await application.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuilderRemainsSingleOwner_ApplicationIsDisposedBeforeActivation(bool failStartup)
    {
        var order = new List<string>();
        var inner = Aspire.Hosting.DistributedApplication.CreateBuilder(new Aspire.Hosting.DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AppSurfaceAspireTestFixtureTests).Assembly.GetName().Name,
            ProjectDirectory = AppContext.BaseDirectory,
            DisableDashboard = true,
        });
        inner.Services.AddSingleton(_ => new OrderedAsyncApplicationDisposalProbe(order));
        var builder = new AppSurfaceAspireProfileTestingBuilder(inner, new OrderedActivationDisposalProbe(order));
        var primary = new InvalidOperationException("health failed");
        var operations = new AppSurfaceAspireTestFixture.FixtureOperations
        {
            StartAsync = (application, token) =>
            {
                _ = application.Services.GetRequiredService<OrderedAsyncApplicationDisposalProbe>();
                return Task.CompletedTask;
            },
            WaitForResourceHealthyAsync = (_, _, _) => failStartup ? Task.FromException(primary) : Task.CompletedTask,
            GetEndpoint = (_, _, _) => new Uri("http://localhost/"),
            WaitForHttpAsync = (_, _, _, _, _) => Task.CompletedTask,
        };
        var startup = AppSurfaceAspireTestFixture.StartAsync(_ => Task.FromResult(builder), Target,
            new() { CreateProbeClient = _ => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))) },
            default, TimeProvider.System, operations);
        if (failStartup)
        {
            Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => startup));
        }
        else
        {
            var fixture = await startup;
            await fixture.DisposeAsync();
            await fixture.DisposeAsync();
        }

        Assert.Equal(["application", "activation"], order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientFactoryCancellation_CleansLateTransferredClientAndKeepsOriginalToken(bool disposeThrows)
    {
        using var caller = new CancellationTokenSource();
        var application = CreateApplication();
        var builder = CreateBuilder();
        var disposed = 0;
        var handler = new StubHandler(_ => new(HttpStatusCode.OK), () =>
        {
            disposed++;
            if (disposeThrows) throw new InvalidOperationException("secondary secret");
        });
        var operations = CreateOperations(application, [], []);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target, new() { CreateProbeClient = _ => { caller.Cancel(); return new HttpClient(handler); } },
            caller.Token, TimeProvider.System, operations));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(1, disposed);
        if (disposeThrows) Assert.Equal(ReadinessFailureCategory.CleanupFailure, error.Data["AppSurface.Readiness.CleanupFailure"]);
        await application.DisposeAsync();
    }

    [Fact]
    public async Task DefaultClientFactoryAndUnrelatedEndpointCancellationPreserveTheirContracts()
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var disposed = 0;
        var operations = CreateOperations(application, [], []) with
        {
            CreateDefaultProbeClient = (_, _, _) => new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK), () => disposed++)),
        };
        await using (var fixture = await AppSurfaceAspireTestFixture.StartAsync(_ => Task.FromResult(builder), Target, null,
            default, TimeProvider.System, operations))
        {
            Assert.Equal(1, disposed);
        }

        var cancellation = new OperationCanceledException("original unrelated callback");
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(CreateBuilder()), Target, null, default, TimeProvider.System,
            operations with { GetEndpoint = (_, _, _) => throw cancellation }));
        Assert.Same(cancellation, canceled);
        await application.DisposeAsync();
    }

    [Theory]
    [InlineData("null-client")]
    [InlineData("cleanup-failure")]
    [InlineData("cancel-during-cleanup")]
    [InlineData("deadline-during-cleanup")]
    [InlineData("primary-and-cleanup")]
    public async Task ClientOwnership_IsReleasedBeforeSuccessAndPreservesPrimary(string failure)
    {
        var application = CreateApplication();
        var builder = CreateBuilder();
        var time = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        var primary = new InvalidOperationException("original predicate exception");
        var disposed = 0;
        var cleaned = 0;
        var handler = new StubHandler(_ => new(HttpStatusCode.OK), () =>
        {
            disposed++;
            if (failure == "cancel-during-cleanup") caller.Cancel();
            if (failure == "deadline-during-cleanup") time.Advance(TimeSpan.FromSeconds(5));
            if (failure is "cleanup-failure" or "primary-and-cleanup") throw new InvalidOperationException("raw-cleanup-secret");
        });
        var operations = CreateOperations(application, [], []) with
        {
            WaitForHttpAsync = (_, _, _, _, _) => failure == "primary-and-cleanup" ? Task.FromException(primary) : Task.CompletedTask,
            CleanupAsync = _ => { cleaned++; return ValueTask.CompletedTask; },
        };
        var error = await Record.ExceptionAsync(() => AppSurfaceAspireTestFixture.StartAsync(
            _ => Task.FromResult(builder), Target,
            new() { StartupTimeout = TimeSpan.FromSeconds(5), CreateProbeClient = _ => failure == "null-client" ? null! : new HttpClient(handler) },
            caller.Token, time, operations));
        Assert.NotNull(error);
        if (failure == "primary-and-cleanup")
        {
            Assert.Same(primary, error);
            Assert.Equal(ReadinessFailureCategory.CleanupFailure, error.Data["AppSurface.Readiness.CleanupFailure"]);
        }
        if (failure == "cleanup-failure")
        {
            var cleanupError = Assert.IsType<ReadinessException>(error);
            Assert.Equal(ReadinessPhase.Cleanup, cleanupError.Phase);
            Assert.Equal("Owned cleanup exception type: InvalidOperationException.", cleanupError.Cause);
        }
        if (failure == "cancel-during-cleanup") Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        if (failure == "deadline-during-cleanup") Assert.Equal(ReadinessFailureCategory.Deadline, Assert.IsType<ReadinessException>(error).Category);
        Assert.DoesNotContain("raw-cleanup-secret", error.ToString());
        Assert.Equal(failure == "null-client" ? 0 : 1, disposed);
        Assert.Equal(1, cleaned);
        await application.DisposeAsync();
    }
}
