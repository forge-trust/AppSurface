using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx.Binding;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[Collection(AspireEnvironmentCollection.Name)]
public sealed class AppSurfaceAspireTestingBuilderTests : IDisposable
{
    private const string AspireProfileActivatorCategory = "ForgeTrust.AppSurface.Aspire.AspireProfileActivator";
    private readonly string? _originalDcpPath = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
    private readonly string? _originalDashboardPath = Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_PATH");

    public AppSurfaceAspireTestingBuilderTests()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
        TestModule.LastProbe = null;
        TestModule.LastActivationArgs = null;
        CancellationTriggerComponent.Source = null;
        SelectiveFailureLoggerFactory.Reset();
    }

    [Fact]
    public async Task CreateAsync_UsesPinnedIdentityPassThroughArgsAndSharedComposition()
    {
        await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, TestProfile>();

        Assert.Same(typeof(TestAppHost).Assembly, builder.AppHostAssembly);
        Assert.Equal(Path.GetFullPath(TestAppHost.ProjectPath), builder.AppHostDirectory);
        Assert.Equal("profile-value", builder.Configuration["profile-setting"]);
        Assert.Contains(builder.Resources, resource => resource.Name == "typed-profile-value");
        Assert.NotNull(TestModule.LastProbe);
        Assert.False(TestModule.LastProbe!.IsDisposed);
        Assert.Empty(Assert.IsType<string[]>(TestModule.LastActivationArgs));
        Assert.NotNull(builder.Environment);
        Assert.NotNull(builder.ExecutionContext);
        Assert.NotNull(builder.Eventing);
        Assert.NotNull(builder.Pipeline);
        Assert.NotNull(builder.FileSystemService);
        Assert.NotNull(builder.UserSecretsManager);
    }

    [Fact]
    public async Task BuildAsync_AllowsOneBuildAndRejectsPostBuildAccess()
    {
        await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, EmptyProfile>();
        builder.Configuration["test-customization"] = "visible";
        builder.Services.AddSingleton<BuilderCustomizationProbe>();
        await using var application = await builder.BuildAsync();

        Assert.Equal(
            "visible",
            application.Services.GetRequiredService<IConfiguration>()["test-customization"]);
        Assert.NotNull(application.Services.GetRequiredService<BuilderCustomizationProbe>());
        Assert.Throws<InvalidOperationException>(() => _ = builder.AppHostAssembly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());
        Assert.Throws<InvalidOperationException>(() => builder.AddParameter("late"));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndDisposesActivationServicesOnce()
    {
        var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, EmptyProfile>();
        var probe = Assert.IsType<DisposalProbe>(TestModule.LastProbe);

        await builder.DisposeAsync();
        await builder.DisposeAsync();

        Assert.True(probe.IsDisposed);
        Assert.Equal(1, probe.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
        Assert.Throws<ObjectDisposedException>(() => builder.AddParameter("disposed"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => builder.BuildAsync());
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallsJoinActivationCleanup()
    {
        var activation = new GatedAsyncDisposalProbe();
        var builder = CreateTestingBuilder(A.Fake<IDistributedApplicationBuilder>(), activation);

        var firstDispose = builder.DisposeAsync().AsTask();
        await activation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondDispose = builder.DisposeAsync().AsTask();

        Assert.False(secondDispose.IsCompleted);
        activation.Release.SetResult();
        await Task.WhenAll(firstDispose, secondDispose);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsCommandBoundProfileMembers()
    {
        var optionException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, BoundProfile>());
        var parameterException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, ParameterBoundProfile>());

        Assert.Contains(nameof(BoundProfile.Value), optionException.Message, StringComparison.Ordinal);
        Assert.Contains("CliFx", optionException.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ParameterBoundProfile.Value), parameterException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingCommandMetadata()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, MissingCommandProfile>());

        Assert.Contains("[Command]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingProjectDirectory()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<MissingDirectoryAppHost, TestModule, EmptyProfile>());

        Assert.Contains("existing directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_HonorsCancellationBeforeActivation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, EmptyProfile>(cancellation.Token));

        Assert.Null(TestModule.LastProbe);
    }

    [Fact]
    public async Task CreateAsync_RejectsCrossAssemblyTypes()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<string, TestModule, EmptyProfile>());

        Assert.Contains("same AppHost assembly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsNonPublicAndAbstractTypes()
    {
        var moduleException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, NonPublicModule, EmptyProfile>());
        var profileException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, AbstractProfile>());

        Assert.Contains("module", moduleException.Message, StringComparison.Ordinal);
        Assert.Contains("profile", profileException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidProjectPathMetadata()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<MissingProjectPathAppHost, TestModule, EmptyProfile>());
        var wrongType = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<WrongProjectPathTypeAppHost, TestModule, EmptyProfile>());
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<EmptyProjectPathAppHost, TestModule, EmptyProfile>());
        var getter = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<ThrowingProjectPathAppHost, TestModule, EmptyProfile>());
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<InvalidProjectPathAppHost, TestModule, EmptyProfile>());

        Assert.Contains("ProjectPath", missing.Message, StringComparison.Ordinal);
        Assert.Contains("ProjectPath", wrongType.Message, StringComparison.Ordinal);
        Assert.Contains("must not be empty", empty.Message, StringComparison.Ordinal);
        Assert.IsType<MarkerGetterException>(getter.InnerException);
        Assert.Contains("ProjectPath is invalid", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_WrapsActivationAndCompositionFailuresAndCleansUp()
    {
        var activation = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, MissingDependencyProfile>());

        Assert.Contains("Profile activation failed", activation.Message, StringComparison.Ordinal);

        TestModule.LastProbe = null;
        var composition = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, ThrowingProfile>());

        Assert.Contains("Profile composition failed", composition.Message, StringComparison.Ordinal);
        Assert.IsType<CompositionException>(composition.InnerException);
        Assert.True(Assert.IsType<DisposalProbe>(TestModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_ProcessFatalCompositionFailureDisposesActivation()
    {
        TestModule.LastProbe = null;

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, FatalThrowingProfile>());

        Assert.Equal("fatal composition failure", exception.Message);
        Assert.True(Assert.IsType<DisposalProbe>(TestModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_CancellationDuringCompositionDisposesActivation()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationTriggerComponent.Source = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, CancellationProfile>(cancellation.Token));

        Assert.True(Assert.IsType<DisposalProbe>(TestModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_CancellationDuringActivationDisposesBuiltHost()
    {
        using var cancellation = new CancellationTokenSource();
        CancelingModule.Source = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, CancelingModule, EmptyProfile>(cancellation.Token));

        Assert.True(Assert.IsType<DisposalProbe>(CancelingModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_OperationCanceledWithoutCancelledTokenReturnsCancelledTask()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, UnboundCancellationProfile>());

        Assert.True(Assert.IsType<DisposalProbe>(TestModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_CleanupFailureDoesNotReplaceCompositionFailure()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, ThrowingCleanupModule, ThrowingCleanupProfile>());

        Assert.IsType<CompositionException>(exception.InnerException);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_ProcessFatalCleanupFailureDoesNotReplaceCompositionFailure()
    {
        FatalCleanupModule.LastProbe = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, FatalCleanupModule, FatalCleanupProfile>());

        Assert.IsType<CompositionException>(exception.InnerException);
        Assert.Equal(1, Assert.IsType<FatalThrowingDisposalProbe>(FatalCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_CleanupFailureDoesNotReplaceActivationFailure()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<
                TestAppHost,
                ThrowingActivationCleanupModule,
                ThrowingActivationProfile>());

        Assert.Contains("Profile activation failed", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ActivationException>(exception.InnerException);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingActivationCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_LoggerAcquisitionFailureDoesNotReplaceActivationFailure()
    {
        SelectiveFailureLoggerFactory.CreateFailureCategory = AspireProfileActivatorCategory;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<
                TestAppHost,
                ThrowingActivationCleanupModule,
                ThrowingActivationProfile>());

        Assert.IsType<ActivationException>(exception.InnerException);
        Assert.Equal(1, SelectiveFailureLoggerFactory.CreateFailureCount);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingActivationCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_LoggerFailureDoesNotReplaceActivationFailure()
    {
        SelectiveFailureLoggerFactory.LogFailureCategory = AspireProfileActivatorCategory;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<
                TestAppHost,
                ThrowingActivationCleanupModule,
                ThrowingActivationProfile>());

        Assert.IsType<ActivationException>(exception.InnerException);
        Assert.Equal(1, SelectiveFailureLoggerFactory.LogFailureCount);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingActivationCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_LoggerAcquisitionFailureDoesNotReplaceCompositionFailure()
    {
        SelectiveFailureLoggerFactory.CreateFailureCategory = typeof(AppSurfaceAspireTestingBuilder).FullName;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, ThrowingCleanupModule, ThrowingCleanupProfile>());

        Assert.IsType<CompositionException>(exception.InnerException);
        Assert.Equal(1, SelectiveFailureLoggerFactory.CreateFailureCount);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_LoggerFailureDoesNotReplaceCompositionFailure()
    {
        SelectiveFailureLoggerFactory.LogFailureCategory = typeof(AppSurfaceAspireTestingBuilder).FullName;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, ThrowingCleanupModule, ThrowingCleanupProfile>());

        Assert.IsType<CompositionException>(exception.InnerException);
        Assert.Equal(1, SelectiveFailureLoggerFactory.LogFailureCount);
        Assert.Equal(1, Assert.IsType<ThrowingDisposalProbe>(ThrowingCleanupModule.LastProbe).DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_ProcessFatalActivationFailureDisposesBuiltHost()
    {
        FatalActivationModule.LastProbe = null;

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, FatalActivationModule, FatalActivationProfile>());

        Assert.Equal("fatal activation failure", exception.Message);
        Assert.True(Assert.IsType<DisposalProbe>(FatalActivationModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAsyncOnlyActivationServices()
    {
        var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, AsyncActivationModule, AsyncActivationProfile>();

        await builder.DisposeAsync();

        Assert.True(Assert.IsType<AsyncActivationDisposalProbe>(AsyncActivationModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task CreateAsync_CompositionFailureDisposesAsyncOnlyActivationServices()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, AsyncActivationModule, AsyncActivationThrowingProfile>());

        Assert.IsType<CompositionException>(exception.InnerException);
        Assert.True(Assert.IsType<AsyncActivationDisposalProbe>(AsyncActivationModule.LastProbe).IsDisposed);
    }

    [Fact]
    public async Task BuildAsync_FailureIsTerminalAndDisposesActivation()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        var activation = A.Fake<IAsyncDisposable>();
        A.CallTo(() => inner.Build()).Throws(new InvalidOperationException("build failed"));
        var builder = CreateTestingBuilder(inner, activation);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());

        Assert.Equal("build failed", exception.Message);
        A.CallTo(() => activation.DisposeAsync()).MustHaveHappenedOnceExactly();
        Assert.Throws<InvalidOperationException>(() => _ = builder.Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());
        await builder.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
    }

    [Fact]
    public async Task BuildAsync_CleanupFailureDoesNotReplaceBuildFailure()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).Throws(new InvalidOperationException("primary build failure"));
        var activation = new AsyncOnlyDisposalProbe(throwOnDispose: true);
        var builder = CreateTestingBuilder(inner, activation);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());

        Assert.Equal("primary build failure", exception.Message);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task BuildAsync_ProcessFatalCleanupFailurePropagatesAndLeavesTerminalState()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).Throws(new InvalidOperationException("primary build failure"));
        var activation = new AsyncOnlyDisposalProbe(new OutOfMemoryException("fatal cleanup failure"));
        var builder = CreateTestingBuilder(inner, activation);

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.BuildAsync());

        Assert.Equal("fatal cleanup failure", exception.Message);
        Assert.Equal(1, activation.DisposeCount);
        await builder.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
    }

    [Fact]
    public async Task BuildAsync_ProcessFatalPrimaryFailurePropagatesAndLeavesDisposableTerminalState()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).Throws(new OutOfMemoryException("fatal build failure"));
        var activation = new AsyncOnlyDisposalProbe();
        var builder = CreateTestingBuilder(inner, activation);

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.BuildAsync());

        Assert.Equal("fatal build failure", exception.Message);
        Assert.Equal(0, activation.DisposeCount);
        await builder.DisposeAsync();
        Assert.Equal(1, activation.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
    }

    [Fact]
    public async Task BuildAsync_FailureCleanupRemainsInProgressUntilActivationDisposalCompletes()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).Throws(new InvalidOperationException("primary build failure"));
        var activation = new GatedAsyncDisposalProbe();
        var builder = CreateTestingBuilder(inner, activation);

        var buildTask = builder.BuildAsync();
        await activation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.DisposeAsync().AsTask());
        activation.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => buildTask);
        await builder.DisposeAsync();
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task BuildAsync_RealAspireFailureReproducesPinnedPartialHostLeak()
    {
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        OwnedBuildProbe? partialHostProbe = null;
        actualBuilder.Services.AddSingleton<OwnedBuildProbe>();
        actualBuilder.Services.AddSingleton<IHostLifetime>(services =>
        {
            partialHostProbe = services.GetRequiredService<OwnedBuildProbe>();
            throw new PartialHostBuildException();
        });
        var activation = new AsyncOnlyDisposalProbe();
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);

        await Assert.ThrowsAsync<PartialHostBuildException>(() => builder.BuildAsync());

        Assert.False(Assert.IsType<OwnedBuildProbe>(partialHostProbe).IsDisposed);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task DisposeDuringBuild_IsRejectedUntilBuildFailureSettles()
    {
        using var enteredBuild = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        var inner = A.Fake<IDistributedApplicationBuilder>();
        var activation = A.Fake<IAsyncDisposable>();
        A.CallTo(() => inner.Build()).ReturnsLazily(() =>
        {
            enteredBuild.Set();
            releaseBuild.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("expected failure");
        });
        var builder = new AppSurfaceAspireProfileTestingBuilder(inner, activation);

        var buildTask = Task.Run(async () => await builder.BuildAsync());
        Assert.True(enteredBuild.Wait(TimeSpan.FromSeconds(10)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.DisposeAsync().AsTask());
        Assert.Throws<InvalidOperationException>(() => builder.Dispose());
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());
        Assert.Throws<InvalidOperationException>(() => _ = builder.Services);

        releaseBuild.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => buildTask);
        await builder.DisposeAsync();
        A.CallTo(() => activation.DisposeAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void DelegatedMutationAndSynchronousDispose_UseInnerContracts()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        var activation = new DualDisposalProbe();
        var resource = new ParameterResource("delegated", _ => "value", secret: false);
        var resourceBuilder = A.Fake<IResourceBuilder<ParameterResource>>();
        A.CallTo(() => inner.AddResource(resource)).Returns(resourceBuilder);
        A.CallTo(() => inner.CreateResourceBuilder(resource)).Returns(resourceBuilder);
        var builder = CreateTestingBuilder(inner, activation);

        Assert.Same(resourceBuilder, builder.AddResource(resource));
        Assert.Same(resourceBuilder, builder.CreateResourceBuilder(resource));
        builder.Dispose();
        builder.Dispose();

        Assert.Equal(1, activation.SyncDisposeCount);
        Assert.Equal(0, activation.AsyncDisposeCount);
    }

    [Fact]
    public void SynchronousDispose_FallsBackToAsyncAndPreservesCleanupFailure()
    {
        var activation = new AsyncOnlyDisposalProbe(throwOnDispose: true);
        var builder = CreateTestingBuilder(A.Fake<IDistributedApplicationBuilder>(), activation);

        Assert.Throws<CleanupException>(() => builder.Dispose());
        Assert.Equal(1, activation.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
    }

    [Fact]
    public async Task DisposeAsync_PropagatesAndSharesCleanupFailure()
    {
        var activation = new AsyncOnlyDisposalProbe(throwOnDispose: true);
        var builder = CreateTestingBuilder(A.Fake<IDistributedApplicationBuilder>(), activation);

        await Assert.ThrowsAsync<CleanupException>(() => builder.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<CleanupException>(() => builder.DisposeAsync().AsTask());

        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ProcessFatalFailurePropagatesAndIsSharedWithRepeatedCallers()
    {
        var activation = new AsyncOnlyDisposalProbe(new OutOfMemoryException("fatal disposal failure"));
        var builder = CreateTestingBuilder(A.Fake<IDistributedApplicationBuilder>(), activation);

        var ownerException = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.DisposeAsync().AsTask());
        var repeatedException = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.DisposeAsync().AsTask());

        Assert.Equal("fatal disposal failure", ownerException.Message);
        Assert.Equal("fatal disposal failure", repeatedException.Message);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public void Dispose_ProcessFatalFailurePropagatesAndIsSharedWithRepeatedCallers()
    {
        var activation = new AsyncOnlyDisposalProbe(new OutOfMemoryException("fatal disposal failure"));
        var builder = CreateTestingBuilder(A.Fake<IDistributedApplicationBuilder>(), activation);

        var ownerException = Assert.Throws<OutOfMemoryException>(() => builder.Dispose());
        var repeatedException = Assert.Throws<OutOfMemoryException>(() => builder.Dispose());

        Assert.Equal("fatal disposal failure", ownerException.Message);
        Assert.Equal("fatal disposal failure", repeatedException.Message);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ApplicationFailureStillDisposesActivationAndRemainsShared()
    {
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<ThrowingAsyncApplicationDisposalProbe>();
        var activation = new AsyncOnlyDisposalProbe();
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<ThrowingAsyncApplicationDisposalProbe>();

        await Assert.ThrowsAsync<CleanupException>(() => builder.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<CleanupException>(() => builder.DisposeAsync().AsTask());

        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ProcessFatalApplicationFailureStillDisposesActivationAndRemainsShared()
    {
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<ThrowingAsyncApplicationDisposalProbe>(_ =>
            new ThrowingAsyncApplicationDisposalProbe(
                new OutOfMemoryException("fatal application disposal failure")));
        var activation = new AsyncOnlyDisposalProbe();
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<ThrowingAsyncApplicationDisposalProbe>();

        var ownerException = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.DisposeAsync().AsTask());
        var repeatedException = await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.DisposeAsync().AsTask());

        Assert.Equal("fatal application disposal failure", ownerException.Message);
        Assert.Equal("fatal application disposal failure", repeatedException.Message);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task BuildAsync_PreCancellationIsTerminalAndCleansUp()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inner = A.Fake<IDistributedApplicationBuilder>();
        var activation = new AsyncOnlyDisposalProbe();
        var builder = CreateTestingBuilder(inner, activation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cancellation.Token));

        A.CallTo(() => inner.Build()).MustNotHaveHappened();
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task BuildAsync_PostBuildCancellationDisposesUnreturnedApplication()
    {
        using var cancellation = new CancellationTokenSource();
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<OwnedBuildProbe>();
        OwnedBuildProbe? ownedProbe = null;
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).ReturnsLazily(() =>
        {
            var application = actualBuilder.Build();
            ownedProbe = application.Services.GetRequiredService<OwnedBuildProbe>();
            cancellation.Cancel();
            return application;
        });
        var activation = new AsyncOnlyDisposalProbe();
        var builder = CreateTestingBuilder(inner, activation, actualBuilder.Services);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cancellation.Token));

        Assert.True(Assert.IsType<OwnedBuildProbe>(ownedProbe).IsDisposed);
        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task BuildAsync_ApplicationCleanupFailureDoesNotReplacePostBuildCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<ThrowingAsyncApplicationDisposalProbe>();
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.Build()).ReturnsLazily(() =>
        {
            var application = actualBuilder.Build();
            _ = application.Services.GetRequiredService<ThrowingAsyncApplicationDisposalProbe>();
            cancellation.Cancel();
            return application;
        });
        var activation = new AsyncOnlyDisposalProbe();
        var builder = CreateTestingBuilder(inner, activation, actualBuilder.Services);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cancellation.Token));

        Assert.Equal(1, activation.DisposeCount);
    }

    [Fact]
    public async Task ExplicitInterfaceBuild_UsesOneBuildContract()
    {
        await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<TestAppHost, TestModule, EmptyProfile>();
        await using var application = ((IDistributedApplicationBuilder)builder).Build();

        Assert.Throws<InvalidOperationException>(() => _ = builder.Resources);
    }

    [Fact]
    public void MissingPinnedAssemblyFromInnerBuilderIsRejected()
    {
        var inner = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => inner.AppHostAssembly).Returns(null!);
        var builder = CreateTestingBuilder(inner, new AsyncOnlyDisposalProbe());

        Assert.Throws<InvalidOperationException>(() => _ = builder.AppHostAssembly);
        builder.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DisposesApplicationBeforeActivationAsFallback()
    {
        var disposalOrder = new List<string>();
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton(_ => new OrderedApplicationDisposalProbe(disposalOrder));
        var activation = new OrderedActivationDisposalProbe(disposalOrder);
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<OrderedApplicationDisposalProbe>();

        await builder.DisposeAsync();

        Assert.Equal(["application", "activation"], disposalOrder);
    }

    [Fact]
    public async Task Dispose_SynchronouslyWaitsForApplicationBeforeActivationAsFallback()
    {
        var disposalOrder = new List<string>();
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton(_ => new OrderedAsyncApplicationDisposalProbe(disposalOrder));
        var activation = new OrderedSynchronousActivationDisposalProbe(disposalOrder);
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<OrderedAsyncApplicationDisposalProbe>();

        builder.Dispose();

        Assert.Equal(["application", "activation"], disposalOrder);
    }

    [Fact]
    public async Task Dispose_ApplicationFailureStillDisposesActivation()
    {
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<ThrowingAsyncApplicationDisposalProbe>();
        var activation = new DualDisposalProbe();
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<ThrowingAsyncApplicationDisposalProbe>();

        Assert.Throws<CleanupException>(() => builder.Dispose());

        Assert.Equal(1, activation.SyncDisposeCount);
    }

    [Fact]
    public async Task Dispose_ProcessFatalApplicationFailureStillDisposesActivationAndRemainsShared()
    {
        var actualBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(TestAppHost).Assembly.GetName().Name,
            ProjectDirectory = TestAppHost.ProjectPath,
            DisableDashboard = true
        });
        actualBuilder.Services.AddSingleton<ThrowingAsyncApplicationDisposalProbe>(_ =>
            new ThrowingAsyncApplicationDisposalProbe(
                new OutOfMemoryException("fatal application disposal failure")));
        var activation = new DualDisposalProbe();
        var builder = new AppSurfaceAspireProfileTestingBuilder(actualBuilder, activation);
        var application = await builder.BuildAsync();
        _ = application.Services.GetRequiredService<ThrowingAsyncApplicationDisposalProbe>();

        var ownerException = Assert.Throws<OutOfMemoryException>(() => builder.Dispose());
        var repeatedException = Assert.Throws<OutOfMemoryException>(() => builder.Dispose());

        Assert.Equal("fatal application disposal failure", ownerException.Message);
        Assert.Equal("fatal application disposal failure", repeatedException.Message);
        Assert.Equal(1, activation.SyncDisposeCount);
        Assert.Equal(0, activation.AsyncDisposeCount);
    }

    private static AppSurfaceAspireProfileTestingBuilder CreateTestingBuilder(
        IDistributedApplicationBuilder inner,
        IAsyncDisposable activation,
        IServiceCollection? services = null)
    {
        A.CallTo(() => inner.Services).Returns(services ?? new ServiceCollection());
        return new AppSurfaceAspireProfileTestingBuilder(inner, activation);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", _originalDcpPath);
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", _originalDashboardPath);
        TestModule.LastProbe = null;
        TestModule.LastActivationArgs = null;
        CancelingModule.Source = null;
        CancelingModule.LastProbe = null;
        ThrowingCleanupModule.LastProbe = null;
        ThrowingActivationCleanupModule.LastProbe = null;
        AsyncActivationModule.LastProbe = null;
        CancellationTriggerComponent.Source = null;
        SelectiveFailureLoggerFactory.Reset();
    }

    private sealed class NonPublicModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }
}

public sealed class TestAppHost
{
    private TestAppHost()
    {
    }

    public static string ProjectPath => AppContext.BaseDirectory;
}

public sealed class MissingDirectoryAppHost
{
    private MissingDirectoryAppHost()
    {
    }

    public static string ProjectPath => Path.Join(AppContext.BaseDirectory, "missing-apphost-directory");
}

public sealed class MissingProjectPathAppHost
{
}

public sealed class WrongProjectPathTypeAppHost
{
    public static int ProjectPath => 42;
}

public sealed class EmptyProjectPathAppHost
{
    public static string ProjectPath => " ";
}

public sealed class ThrowingProjectPathAppHost
{
    public static string ProjectPath => throw new MarkerGetterException();
}

public sealed class InvalidProjectPathAppHost
{
    public static string ProjectPath => "\0";
}

public sealed class MarkerGetterException : Exception
{
}

public sealed class TestModule : IAppSurfaceHostModule
{
    public static DisposalProbe? LastProbe { get; set; }

    public static string[]? LastActivationArgs { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        LastActivationArgs = context.Args;
        services.AddSingleton(_ =>
        {
            var probe = new DisposalProbe();
            LastProbe = probe;
            return probe;
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class CancelingModule : IAppSurfaceHostModule
{
    public static CancellationTokenSource? Source { get; set; }

    public static DisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var probe = new DisposalProbe();
            LastProbe = probe;
            Source?.Cancel();
            return probe;
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class ThrowingCleanupModule : IAppSurfaceHostModule
{
    public static ThrowingDisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory, SelectiveFailureLoggerFactory>();
        services.AddSingleton(_ =>
        {
            var probe = new ThrowingDisposalProbe();
            LastProbe = probe;
            return probe;
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class ThrowingActivationCleanupModule : IAppSurfaceHostModule
{
    public static ThrowingDisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory, SelectiveFailureLoggerFactory>();
        services.AddSingleton(_ =>
        {
            var probe = new ThrowingDisposalProbe();
            LastProbe = probe;
            return probe;
        });
        services.AddSingleton<ThrowingActivationProfile>(serviceProvider =>
        {
            _ = serviceProvider.GetRequiredService<ThrowingDisposalProbe>();
            throw new ActivationException();
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class FatalCleanupModule : IAppSurfaceHostModule
{
    public static FatalThrowingDisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var probe = new FatalThrowingDisposalProbe();
            LastProbe = probe;
            return probe;
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class FatalActivationModule : IAppSurfaceHostModule
{
    public static DisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var probe = new DisposalProbe();
            LastProbe = probe;
            return probe;
        });
        services.AddSingleton<FatalActivationProfile>(serviceProvider =>
        {
            _ = serviceProvider.GetRequiredService<DisposalProbe>();
            throw new OutOfMemoryException("fatal activation failure");
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class AsyncActivationModule : IAppSurfaceHostModule
{
    public static AsyncActivationDisposalProbe? LastProbe { get; set; }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var probe = new AsyncActivationDisposalProbe();
            LastProbe = probe;
            return probe;
        });
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class DisposalProbe : IDisposable
{
    public bool IsDisposed { get; private set; }

    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        DisposeCount++;
    }
}

public sealed class BuilderCustomizationProbe
{
}

public sealed class OwnedBuildProbe : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

public sealed class ThrowingDisposalProbe : IDisposable
{
    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        throw new CleanupException();
    }
}

public sealed class FatalThrowingDisposalProbe : IDisposable
{
    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        throw new OutOfMemoryException("fatal cleanup failure");
    }
}

public sealed class OrderedApplicationDisposalProbe : IDisposable
{
    private readonly ICollection<string> _order;

    public OrderedApplicationDisposalProbe(ICollection<string> order)
    {
        _order = order;
    }

    public void Dispose() => _order.Add("application");
}

public sealed class OrderedActivationDisposalProbe : IAsyncDisposable
{
    private readonly ICollection<string> _order;

    public OrderedActivationDisposalProbe(ICollection<string> order)
    {
        _order = order;
    }

    public ValueTask DisposeAsync()
    {
        _order.Add("activation");
        return ValueTask.CompletedTask;
    }
}

public sealed class OrderedAsyncApplicationDisposalProbe : IAsyncDisposable
{
    private readonly ICollection<string> _order;

    public OrderedAsyncApplicationDisposalProbe(ICollection<string> order)
    {
        _order = order;
    }

    public ValueTask DisposeAsync()
    {
        _order.Add("application");
        return ValueTask.CompletedTask;
    }
}

public sealed class OrderedSynchronousActivationDisposalProbe : IDisposable, IAsyncDisposable
{
    private readonly ICollection<string> _order;

    public OrderedSynchronousActivationDisposalProbe(ICollection<string> order)
    {
        _order = order;
    }

    public void Dispose() => _order.Add("activation");

    public ValueTask DisposeAsync() => throw new InvalidOperationException("Synchronous disposal was expected.");
}

public sealed class AsyncActivationDisposalProbe : IAsyncDisposable
{
    public bool IsDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class ThrowingAsyncApplicationDisposalProbe : IAsyncDisposable
{
    private readonly Exception _exception;

    public ThrowingAsyncApplicationDisposalProbe()
        : this(new CleanupException())
    {
    }

    public ThrowingAsyncApplicationDisposalProbe(Exception exception)
    {
        _exception = exception;
    }

    public ValueTask DisposeAsync() => ValueTask.FromException(_exception);
}

public sealed class DualDisposalProbe : IDisposable, IAsyncDisposable
{
    public int SyncDisposeCount { get; private set; }

    public int AsyncDisposeCount { get; private set; }

    public void Dispose() => SyncDisposeCount++;

    public ValueTask DisposeAsync()
    {
        AsyncDisposeCount++;
        return ValueTask.CompletedTask;
    }
}

public sealed class AsyncOnlyDisposalProbe : IAsyncDisposable
{
    private readonly Exception? _disposeException;

    public AsyncOnlyDisposalProbe(bool throwOnDispose = false)
    {
        _disposeException = throwOnDispose ? new CleanupException() : null;
    }

    public AsyncOnlyDisposalProbe(Exception disposeException)
    {
        _disposeException = disposeException;
    }

    public int DisposeCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return _disposeException is not null
            ? ValueTask.FromException(_disposeException)
            : ValueTask.CompletedTask;
    }
}

public sealed class GatedAsyncDisposalProbe : IAsyncDisposable
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int DisposeCount { get; private set; }

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        Entered.SetResult();
        await Release.Task.ConfigureAwait(false);
    }
}

public sealed class CleanupException : Exception
{
}

public sealed class CleanupDiagnosticsException : Exception
{
}

public sealed class SelectiveFailureLoggerFactory : ILoggerFactory
{
    public static string? CreateFailureCategory { get; set; }

    public static string? LogFailureCategory { get; set; }

    public static int CreateFailureCount { get; private set; }

    public static int LogFailureCount { get; private set; }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (string.Equals(categoryName, CreateFailureCategory, StringComparison.Ordinal))
        {
            CreateFailureCount++;
            throw new CleanupDiagnosticsException();
        }

        return new SelectiveFailureLogger(categoryName);
    }

    public void Dispose()
    {
    }

    public static void Reset()
    {
        CreateFailureCategory = null;
        LogFailureCategory = null;
        CreateFailureCount = 0;
        LogFailureCount = 0;
    }

    private sealed class SelectiveFailureLogger : ILogger
    {
        private readonly string _categoryName;

        public SelectiveFailureLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (string.Equals(_categoryName, LogFailureCategory, StringComparison.Ordinal))
            {
                LogFailureCount++;
                throw new CleanupDiagnosticsException();
            }
        }
    }
}

public sealed class ActivationException : Exception
{
}

public sealed class PartialHostBuildException : Exception
{
}

public sealed class TestComponent : IAspireComponent<ParameterResource>
{
    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        appBuilder.AddParameter("typed-profile-value");
}

public sealed class ThrowingComponent : IAspireComponent<ParameterResource>
{
    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        throw new CompositionException();
}

public sealed class FatalThrowingComponent : IAspireComponent<ParameterResource>
{
    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        throw new OutOfMemoryException("fatal composition failure");
}

public sealed class CompositionException : Exception
{
}

public sealed class CancellationTriggerComponent : IAspireComponent<ParameterResource>
{
    public static CancellationTokenSource? Source { get; set; }

    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        Source?.Cancel();
        return appBuilder.AddParameter("cancelled-profile");
    }
}

public sealed class UnboundCancellationComponent : IAspireComponent<ParameterResource>
{
    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        throw new OperationCanceledException();
}

[Command("test")]
public sealed partial class TestProfile : AspireProfile
{
    private readonly TestComponent _component;

    public TestProfile(TestComponent component, DisposalProbe probe, ILogger<TestProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override string[] PassThroughArgs => ["--profile-setting", "profile-value"];

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("empty")]
public sealed partial class EmptyProfile : AspireProfile
{
    public EmptyProfile(DisposalProbe probe, ILogger<EmptyProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("bound")]
public sealed partial class BoundProfile : AspireProfile
{
    public BoundProfile(ILogger<BoundProfile> logger)
        : base(logger)
    {
    }

    [CommandOption("value")]
    public string? Value { get; set; }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("parameter-bound")]
public sealed partial class ParameterBoundProfile : AspireProfile
{
    public ParameterBoundProfile(ILogger<ParameterBoundProfile> logger)
        : base(logger)
    {
    }

    [CommandParameter(0)]
    public string? Value { get; set; }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

public sealed class MissingCommandProfile : AspireProfile
{
    public MissingCommandProfile(ILogger<MissingCommandProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("throwing")]
public sealed partial class ThrowingProfile : AspireProfile
{
    private readonly ThrowingComponent _component;

    public ThrowingProfile(ThrowingComponent component, DisposalProbe probe, ILogger<ThrowingProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("fatal-throwing")]
public sealed partial class FatalThrowingProfile : AspireProfile
{
    private readonly FatalThrowingComponent _component;

    public FatalThrowingProfile(
        FatalThrowingComponent component,
        DisposalProbe probe,
        ILogger<FatalThrowingProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("cancel")]
public sealed partial class CancellationProfile : AspireProfile
{
    private readonly CancellationTriggerComponent _component;

    public CancellationProfile(
        CancellationTriggerComponent component,
        DisposalProbe probe,
        ILogger<CancellationProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("unbound-cancel")]
public sealed partial class UnboundCancellationProfile : AspireProfile
{
    private readonly UnboundCancellationComponent _component;

    public UnboundCancellationProfile(
        UnboundCancellationComponent component,
        DisposalProbe probe,
        ILogger<UnboundCancellationProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("throwing-cleanup")]
public sealed partial class ThrowingCleanupProfile : AspireProfile
{
    private readonly ThrowingComponent _component;

    public ThrowingCleanupProfile(
        ThrowingComponent component,
        ThrowingDisposalProbe probe,
        ILogger<ThrowingCleanupProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("throwing-activation")]
public sealed partial class ThrowingActivationProfile : AspireProfile
{
    public ThrowingActivationProfile(ILogger<ThrowingActivationProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("fatal-cleanup")]
public sealed partial class FatalCleanupProfile : AspireProfile
{
    private readonly ThrowingComponent _component;

    public FatalCleanupProfile(
        ThrowingComponent component,
        FatalThrowingDisposalProbe probe,
        ILogger<FatalCleanupProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("fatal-activation")]
public sealed partial class FatalActivationProfile : AspireProfile
{
    public FatalActivationProfile(ILogger<FatalActivationProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("async-activation")]
public sealed partial class AsyncActivationProfile : AspireProfile
{
    public AsyncActivationProfile(
        AsyncActivationDisposalProbe probe,
        ILogger<AsyncActivationProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("async-activation-throwing")]
public sealed partial class AsyncActivationThrowingProfile : AspireProfile
{
    private readonly ThrowingComponent _component;

    public AsyncActivationThrowingProfile(
        AsyncActivationDisposalProbe probe,
        ThrowingComponent component,
        ILogger<AsyncActivationThrowingProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("missing-dependency")]
public sealed partial class MissingDependencyProfile : AspireProfile
{
    public MissingDependencyProfile(UnregisteredDependency dependency, ILogger<MissingDependencyProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

public sealed class UnregisteredDependency
{
}

public abstract class AbstractProfile : AspireProfile
{
    protected AbstractProfile(ILogger logger)
        : base(logger)
    {
    }
}
