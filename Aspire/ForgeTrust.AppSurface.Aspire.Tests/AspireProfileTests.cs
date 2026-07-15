using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx.Binding;
using CliFx.Infrastructure;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

public partial class AspireProfileTests
{
    [Fact]
    public void ActivationLease_SynchronousDisposeUsesHostFallbackOnce()
    {
        var host = A.Fake<Microsoft.Extensions.Hosting.IHost>();
        var profile = new TestProfile(A.Fake<ILogger<TestProfile>>(), [], []);
        var activation = new AspireProfileActivationLease<TestProfile>(host, profile);

        activation.Dispose();
        activation.Dispose();

        A.CallTo(() => host.Dispose()).MustHaveHappenedOnceExactly();
        Assert.Throws<ObjectDisposedException>(() => _ = activation.Services);
    }

    [Fact]
    public async Task ActivationLease_StaticAsyncCleanupUsesSynchronousHostFallback()
    {
        var host = A.Fake<Microsoft.Extensions.Hosting.IHost>();

        await AspireProfileActivationLease<TestProfile>.DisposeHostAsync(host);

        A.CallTo(() => host.Dispose()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void PassThroughArgs_DefaultsToEmpty()
    {
        var profile = new TestProfile(A.Fake<ILogger<TestProfile>>(), [], []);

        Assert.Empty(profile.PassThroughArgs);
    }

    [Fact]
    public async Task ExecuteAsync_UsesOverriddenPassThroughArgsForDistributedApplicationBuilder()
    {
        var capturedValue = default(string);
        var component = new InspectingComponent(appBuilder =>
        {
            capturedValue = appBuilder.Configuration["appsurface-profile-test"];
            throw new InvalidOperationException("Stop execution");
        });
        var console = A.Fake<IConsole>();

        var profile = new TestProfile(
            A.Fake<ILogger<TestProfile>>(),
            [],
            [component],
            ["--appsurface-profile-test", "from-pass-through"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.ExecuteAsync(console).AsTask());

        Assert.Equal("Stop execution", exception.Message);
        Assert.Equal("from-pass-through", capturedValue);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesDependenciesBeforeOwnComponents()
    {
        var callOrder = new List<string>();
        var dependencyComponent = A.Fake<IAspireComponent<IResource>>();
        var dependencyBuilder = A.Fake<IResourceBuilder<IResource>>();
        var component = A.Fake<IAspireComponent<IResource>>();
        var console = A.Fake<IConsole>();

        A.CallTo(() => dependencyComponent.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._))
            .Invokes((AspireStartupContext _, IDistributedApplicationBuilder _) => callOrder.Add("dependency"))
            .Returns(dependencyBuilder);

        A.CallTo(() => component.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._))
            .ReturnsLazily((AspireStartupContext _, IDistributedApplicationBuilder _) =>
            {
                callOrder.Add("component");
                throw new InvalidOperationException("Stop execution");
            });

        var profile = new TestProfile(
            A.Fake<ILogger<TestProfile>>(),
            [new TestProfile(A.Fake<ILogger<TestProfile>>(), [], [dependencyComponent])],
            [component]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.ExecuteAsync(console).AsTask());

        Assert.Equal("Stop execution", exception.Message);
        Assert.Equal(["dependency", "component"], callOrder);
        A.CallTo(() => dependencyComponent.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesDependenciesOnlyOnce()
    {
        var callOrder = new List<string>();
        var dependencyComponent = A.Fake<IAspireComponent<IResource>>();
        var dependencyBuilder = A.Fake<IResourceBuilder<IResource>>();
        var component = A.Fake<IAspireComponent<IResource>>();
        var console = A.Fake<IConsole>();

        A.CallTo(() => dependencyComponent.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._))
            .Invokes((AspireStartupContext _, IDistributedApplicationBuilder _) => callOrder.Add("dependency"))
            .Returns(dependencyBuilder);

        A.CallTo(() => component.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._))
            .ReturnsLazily((AspireStartupContext _, IDistributedApplicationBuilder _) =>
            {
                callOrder.Add("component");
                throw new InvalidOperationException("Stop execution");
            });

        var profile = new TestProfile(
            A.Fake<ILogger<TestProfile>>(),
            [
                new TestProfile(A.Fake<ILogger<TestProfile>>(), [], [dependencyComponent]),
                new TestProfile(A.Fake<ILogger<TestProfile>>(), [], [dependencyComponent])
            ],
            [component]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.ExecuteAsync(console).AsTask());

        Assert.Equal("Stop execution", exception.Message);
        Assert.Equal(["dependency", "component"], callOrder);
        A.CallTo(() => dependencyComponent.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Compose_HonorsCancellationBeforeGeneratingComponents()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var component = A.Fake<IAspireComponent<IResource>>();
        var profile = new TestProfile(A.Fake<ILogger<TestProfile>>(), [], [component]);

        Assert.Throws<OperationCanceledException>(() =>
            profile.Compose(A.Fake<IDistributedApplicationBuilder>(), cancellation.Token));

        A.CallTo(() => component.Generate(A<AspireStartupContext>._, A<IDistributedApplicationBuilder>._))
            .MustNotHaveHappened();
    }

    [Command("test-profile")]
    public sealed partial class TestProfile : AspireProfile
    {
        private readonly IReadOnlyList<AspireProfile>? _dependencies;
        private readonly IReadOnlyList<IAspireComponent>? _components;
        private readonly string[]? _passThroughArgs;

        public TestProfile(
            ILogger<TestProfile> logger,
            IReadOnlyList<AspireProfile>? dependencies = null,
            IReadOnlyList<IAspireComponent>? components = null,
            string[]? passThroughArgs = null) : base(logger)
        {
            _dependencies = dependencies;
            _components = components;
            _passThroughArgs = passThroughArgs;
        }

        public override string[] PassThroughArgs => _passThroughArgs ?? base.PassThroughArgs;

        public override IEnumerable<AspireProfile> GetDependencies() =>
            _dependencies ?? throw new InvalidOperationException("No dependencies configured.");

        public override IEnumerable<IAspireComponent> GetComponents() =>
            _components ?? throw new InvalidOperationException("No components configured.");
    }

    private sealed class InspectingComponent : IAspireComponent<IResource>
    {
        private readonly Action<IDistributedApplicationBuilder> _inspect;

        public InspectingComponent(Action<IDistributedApplicationBuilder> inspect)
        {
            _inspect = inspect;
        }

        public IResourceBuilder<IResource> Generate(AspireStartupContext context, IDistributedApplicationBuilder appBuilder)
        {
            _inspect(appBuilder);
            throw new InvalidOperationException("Stop execution");
        }
    }
}
