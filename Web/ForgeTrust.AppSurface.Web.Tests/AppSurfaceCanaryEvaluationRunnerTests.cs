using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryEvaluationRunnerTests
{
    [Fact]
    public async Task EvaluateAsync_ReturnsNotFoundWithoutResolvingEvaluator()
    {
        var activations = 0;
        var services = new ServiceCollection();
        services.AddTransient<RecordingEvaluator>(_ =>
        {
            activations++;
            throw new InvalidOperationException("must not activate");
        });
        services.AddAppSurfaceCanary<RecordingEvaluator>("proof");
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();

        var outcome = await runner.EvaluateAsync("Proof", null, null, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Null(outcome.Result);
        Assert.Same(AppSurfaceCanaryEvaluationOutcome.NotFound, outcome);
        Assert.Equal(0, activations);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass)]
    [InlineData(AppSurfaceCanaryStatus.Pending)]
    [InlineData(AppSurfaceCanaryStatus.Fail)]
    [InlineData(AppSurfaceCanaryStatus.Stale)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured)]
    public async Task EvaluateAsync_ReturnsEveryDefinedStatus(AppSurfaceCanaryStatus status)
    {
        var evaluator = new RecordingEvaluator { Result = new AppSurfaceCanaryResult(status) };
        await using var fixture = CreateFixture(evaluator);

        var outcome = await fixture.Runner.EvaluateAsync("proof", null, null, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.NotNull(outcome.Result);
        Assert.Equal(status, outcome.Result.Status);
        Assert.Equal(1, evaluator.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesContextAndCancellationTokenExactly()
    {
        var evaluator = new RecordingEvaluator();
        await using var fixture = CreateFixture(evaluator);
        var freshSince = new DateTimeOffset(2026, 7, 12, 9, 15, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var outcome = await fixture.Runner.EvaluateAsync(
            "proof",
            " deploy-42 ",
            freshSince,
            cancellation.Token);

        Assert.True(outcome.Found);
        Assert.Equal("proof", evaluator.Context!.Name);
        Assert.Equal(" deploy-42 ", evaluator.Context.Marker);
        Assert.Equal(freshSince, evaluator.Context.FreshSince);
        Assert.Equal(cancellation.Token, evaluator.CancellationToken);
        Assert.Equal(1, evaluator.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_ResolvesTransientEvaluatorExactlyOncePerInvocation()
    {
        var activations = 0;
        var services = new ServiceCollection();
        services.AddTransient<RecordingEvaluator>(_ =>
        {
            activations++;
            return new RecordingEvaluator();
        });
        services.AddAppSurfaceCanary<RecordingEvaluator>("proof");
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();

        var outcome = await runner.EvaluateAsync("proof", null, null, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task EvaluateAsync_UsesCurrentRequestScopeForScopedEvaluator()
    {
        var services = new ServiceCollection();
        services.AddScoped<IdentityEvaluator>();
        services.AddAppSurfaceCanary<IdentityEvaluator>("proof");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Guid firstId;
        await using (var firstScope = provider.CreateAsyncScope())
        {
            var runner = firstScope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();
            var expected = firstScope.ServiceProvider.GetRequiredService<IdentityEvaluator>();
            var outcome = await runner.EvaluateAsync("proof", null, null, CancellationToken.None);
            firstId = expected.Id;

            Assert.True(outcome.Found);
            Assert.Equal(expected.Id, expected.LastEvaluatedId);
        }

        await using (var secondScope = provider.CreateAsyncScope())
        {
            var runner = secondScope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();
            var expected = secondScope.ServiceProvider.GetRequiredService<IdentityEvaluator>();
            var outcome = await runner.EvaluateAsync("proof", null, null, CancellationToken.None);

            Assert.True(outcome.Found);
            Assert.NotEqual(firstId, expected.Id);
            Assert.Equal(expected.Id, expected.LastEvaluatedId);
        }
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesEvaluatorException()
    {
        var evaluator = new RecordingEvaluator { Exception = new TestEvaluationException() };
        await using var fixture = CreateFixture(evaluator);

        var exception = await Assert.ThrowsAsync<TestEvaluationException>(async () =>
            await fixture.Runner.EvaluateAsync("proof", null, null, CancellationToken.None));

        Assert.Same(evaluator.Exception, exception);
        Assert.Equal(1, evaluator.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var evaluator = new RecordingEvaluator
        {
            Exception = new OperationCanceledException(cancellation.Token)
        };
        await using var fixture = CreateFixture(evaluator);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Runner.EvaluateAsync("proof", null, null, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsNullEvaluatorResult()
    {
        await using var fixture = CreateFixture(new NullResultEvaluator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runner.EvaluateAsync("proof", null, null, CancellationToken.None));

        Assert.Contains("null result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_AcceptsOnlyDetailsDeclaredForSelectedCanary()
    {
        var evaluator = new RecordingEvaluator
        {
            Result = new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options => options.AddDetail("proof.kind", "migration"))
        };
        await using var fixture = CreateFixture(
            evaluator,
            options => options.AllowedDetailKeys.Add("proof.kind"));

        var outcome = await fixture.Runner.EvaluateAsync("proof", null, null, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("migration", outcome.Result!.Details["proof.kind"]);
        Assert.Equal(1, evaluator.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsUndeclaredDetailAfterOneInvocation()
    {
        var evaluator = new RecordingEvaluator
        {
            Result = new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options => options.AddDetail("provider.region", "us-east"))
        };
        await using var fixture = CreateFixture(evaluator);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runner.EvaluateAsync("proof", null, null, CancellationToken.None));

        Assert.StartsWith("ASCAN301", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider.region", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, evaluator.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotShareAllowedKeysAcrossCanaries()
    {
        var first = new RecordingEvaluator();
        var second = new SecondRecordingEvaluator
        {
            Result = new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pending,
                options => options.AddDetail("provider.region", "west"))
        };
        var services = new ServiceCollection();
        services.AddSingleton(first);
        services.AddSingleton(second);
        services.AddAppSurfaceCanary<RecordingEvaluator>(
            "proof.one",
            options => options.AllowedDetailKeys.Add("provider.region"));
        services.AddAppSurfaceCanary<SecondRecordingEvaluator>("proof.two");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.EvaluateAsync("proof.two", null, null, CancellationToken.None));

        Assert.StartsWith("ASCAN301", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider.region", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesActivationFailure()
    {
        var services = new ServiceCollection();
        services.AddTransient<ActivationFailureEvaluator>(
            _ => throw new TestActivationException());
        services.AddAppSurfaceCanary<ActivationFailureEvaluator>("proof");
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();

        await Assert.ThrowsAsync<TestActivationException>(async () =>
            await runner.EvaluateAsync("proof", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task DescriptorOverload_RejectsNullDescriptor()
    {
        await using var fixture = CreateFixture(new RecordingEvaluator());

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await fixture.Runner.EvaluateAsync(
                (AppSurfaceCanaryDescriptor)null!,
                null,
                null,
                CancellationToken.None));

        Assert.Equal("descriptor", exception.ParamName);
    }

    [Fact]
    public void TryGetDescriptor_UsesExactOrdinalLookup()
    {
        using var fixture = CreateFixture(new RecordingEvaluator());

        Assert.True(fixture.Runner.TryGetDescriptor("proof", out var descriptor));
        Assert.Equal("proof", descriptor.Name);
        Assert.False(fixture.Runner.TryGetDescriptor("Proof", out _));
    }

    private static RunnerFixture CreateFixture<TEvaluator>(
        TEvaluator evaluator,
        Action<AppSurfaceCanaryRegistrationOptions>? configure = null)
        where TEvaluator : class, IAppSurfaceCanaryEvaluator
    {
        var services = new ServiceCollection();
        services.AddSingleton(evaluator);
        services.AddAppSurfaceCanary<TEvaluator>("proof", configure);
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();
        return new RunnerFixture(provider, scope, runner);
    }

    private sealed class RunnerFixture(
        ServiceProvider provider,
        AsyncServiceScope scope,
        AppSurfaceCanaryEvaluationRunner runner) : IAsyncDisposable, IDisposable
    {
        public AppSurfaceCanaryEvaluationRunner Runner { get; } = runner;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private sealed class RecordingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public AppSurfaceCanaryResult Result { get; init; } = new(AppSurfaceCanaryStatus.Pass);

        public Exception? Exception { get; init; }

        public int InvocationCount { get; private set; }

        public AppSurfaceCanaryEvaluationContext? Context { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            Context = context;
            CancellationToken = cancellationToken;

            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(Result);
        }
    }

    private sealed class IdentityEvaluator : IAppSurfaceCanaryEvaluator
    {
        public Guid Id { get; } = Guid.NewGuid();

        public Guid? LastEvaluatedId { get; private set; }

        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken)
        {
            LastEvaluatedId = Id;
            return ValueTask.FromResult(new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass));
        }
    }

    private sealed class SecondRecordingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public AppSurfaceCanaryResult Result { get; init; } = new(AppSurfaceCanaryStatus.Pass);

        public int InvocationCount { get; private set; }

        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class NullResultEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AppSurfaceCanaryResult>(null!);
    }

    private sealed class ActivationFailureEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestEvaluationException : Exception;

    private sealed class TestActivationException : Exception;
}
