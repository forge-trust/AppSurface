using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryRegistrationTests
{
    [Fact]
    public void EvaluationContext_PreservesValidatedInputs()
    {
        var freshSince = new DateTimeOffset(2026, 7, 12, 12, 30, 45, TimeSpan.Zero);

        var context = new AppSurfaceCanaryEvaluationContext("forwarding.alpha-evidence", " deploy-42 ", freshSince);

        Assert.Equal("forwarding.alpha-evidence", context.Name);
        Assert.Equal(" deploy-42 ", context.Marker);
        Assert.Equal(freshSince, context.FreshSince);
    }

    [Fact]
    public void EvaluationContext_RejectsNullName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceCanaryEvaluationContext(null!, null, null));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Forwarding")]
    [InlineData("forwarding..proof")]
    [InlineData("-forwarding")]
    [InlineData("forwarding-")]
    [InlineData("forwarding_alpha")]
    [InlineData("proof\n")]
    [InlineData("proof\r")]
    public void EvaluationContext_RejectsInvalidName(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new AppSurfaceCanaryEvaluationContext(name, null, null));

        Assert.Equal("name", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass)]
    [InlineData(AppSurfaceCanaryStatus.Pending)]
    [InlineData(AppSurfaceCanaryStatus.Fail)]
    [InlineData(AppSurfaceCanaryStatus.Stale)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured)]
    public void Result_PreservesDefinedStatus(AppSurfaceCanaryStatus status)
    {
        var result = new AppSurfaceCanaryResult(status);

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void Result_RejectsUndefinedStatus()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppSurfaceCanaryResult((AppSurfaceCanaryStatus)99));

        Assert.Equal("status", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_ValidatesNullArguments()
    {
        IServiceCollection services = null!;

        Assert.Equal(
            "services",
            Assert.Throws<ArgumentNullException>(
                () => services.AddAppSurfaceCanary<PassingEvaluator>("proof")).ParamName);
        Assert.Equal(
            "name",
            Assert.Throws<ArgumentNullException>(
                () => new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(null!)).ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_ReturnsOriginalCollectionAndCapturesDefaults()
    {
        var services = new ServiceCollection();

        var returned = services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        Assert.Same(services, returned);
        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)).ImplementationInstance;
        var canary = Assert.IsType<AppSurfaceCanaryDescriptor>(descriptor);
        Assert.Equal("proof", canary.Name);
        Assert.Equal("proof", canary.DisplayName);
        Assert.Null(canary.Description);
        Assert.Empty(canary.Tags);
        Assert.False(canary.MarkerRequired);
        Assert.False(canary.FreshSinceRequired);
        Assert.Equal(typeof(PassingEvaluator), canary.EvaluatorType);
    }

    [Fact]
    public void AddAppSurfaceCanary_SnapshotsConfiguredMetadataAndRequirements()
    {
        AppSurfaceCanaryRegistrationOptions? captured = null;
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>(
            "proof.deploy",
            options =>
            {
                captured = options;
                options.DisplayName = "Deploy proof";
                options.Description = "Proves the current deployment.";
                options.Tags.Add("z-last");
                options.Tags.Add("deploy");
                options.Tags.Add("deploy");
                options.RequireMarker();
                options.RequireMarker();
                options.RequireFreshSince();
                options.RequireFreshSince();
            });

        captured!.DisplayName = "Changed later";
        captured.Description = "Changed later";
        captured.Tags.Clear();
        captured.Tags.Add("changed");

        var descriptor = GetDescriptor(services);
        Assert.Equal("Deploy proof", descriptor.DisplayName);
        Assert.Equal("Proves the current deployment.", descriptor.Description);
        Assert.Equal(new[] { "deploy", "z-last" }, descriptor.Tags);
        Assert.True(descriptor.MarkerRequired);
        Assert.True(descriptor.FreshSinceRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Proof")]
    [InlineData("proof..deploy")]
    [InlineData("proof/deploy")]
    [InlineData("proof-")]
    public void AddAppSurfaceCanary_RejectsInvalidNamesWithoutMutation(string name)
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceCanary<PassingEvaluator>(name));

        Assert.Equal("name", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsOverlongNameWithoutMutation()
    {
        var services = new ServiceCollection();
        var name = new string('a', AppSurfaceCanaryValidation.MaximumNameLength + 1);

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceCanary<PassingEvaluator>(name));

        Assert.Equal("name", exception.ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("description")]
    [InlineData("tag")]
    public void AddAppSurfaceCanary_InvalidConfiguredMetadataIsAtomic(string invalidField)
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options =>
                {
                    if (invalidField == "display")
                    {
                        options.DisplayName = " ";
                    }
                    else if (invalidField == "description")
                    {
                        options.Description = new string('x', AppSurfaceCanaryValidation.MaximumDescriptionLength + 1);
                    }
                    else
                    {
                        options.Tags.Add("Invalid_Tag");
                    }
                }));

        Assert.Equal("configure", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_ThrowingCallbackIsAtomic()
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<TestRegistrationException>(() =>
            services.AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                _ => throw new TestRegistrationException()));

        Assert.NotNull(exception);
        Assert.Equal(count, services.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two.parts")]
    [InlineData("deploy\n")]
    [InlineData("deploy\r")]
    public void AddAppSurfaceCanary_RejectsInvalidTags(string tag)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(tag)));

        Assert.Equal("configure", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsOverlongTag()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(new string('a', 65))));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsNullTag()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(null!)));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_RegistersDefaultEvaluatorAsTransient()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        var evaluator = Assert.Single(services, service => service.ServiceType == typeof(PassingEvaluator));
        Assert.Equal(ServiceLifetime.Transient, evaluator.Lifetime);
        Assert.Equal(typeof(PassingEvaluator), evaluator.ImplementationType);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void AddAppSurfaceCanary_PreservesExplicitEvaluatorLifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        ((ICollection<ServiceDescriptor>)services).Add(
            new ServiceDescriptor(typeof(PassingEvaluator), typeof(PassingEvaluator), lifetime));

        services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        var evaluator = Assert.Single(services, service => service.ServiceType == typeof(PassingEvaluator));
        Assert.Equal(lifetime, evaluator.Lifetime);
    }

    [Fact]
    public void AddAppSurfaceCanary_RegistersSharedInfrastructureOnce()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>("proof.one");
        services.AddAppSurfaceCanary<SecondPassingEvaluator>("proof.two");

        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryRegistry));
        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryEvaluationRunner));
        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryMappingState));
        Assert.Single(
            services,
            service => service.ServiceType == typeof(IStartupFilter)
                && service.ImplementationType == typeof(AppSurfaceCanaryStartupValidator));
        Assert.Equal(2, services.Count(service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)));
    }

    [Fact]
    public void Registry_RejectsDuplicateNamesWhenMaterialized()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppSurfaceCanary<PassingEvaluator>("proof");
        services.AddAppSurfaceCanary<SecondPassingEvaluator>("proof");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<AppSurfaceCanaryRegistry>());

        Assert.StartsWith("ASCAN102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("proof", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_UsesExactOrdinalNameLookup()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceCanary<PassingEvaluator>("proof.deploy");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<AppSurfaceCanaryRegistry>();

        Assert.True(registry.TryGet("proof.deploy", out var descriptor));
        Assert.Equal("proof.deploy", descriptor.Name);
        Assert.False(registry.TryGet("Proof.Deploy", out _));
    }

    private static AppSurfaceCanaryDescriptor GetDescriptor(IServiceCollection services) =>
        Assert.IsType<AppSurfaceCanaryDescriptor>(
            Assert.Single(
                services,
                service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)).ImplementationInstance);

    private sealed class PassingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass));
    }

    private sealed class SecondPassingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass));
    }

    private sealed class TestRegistrationException : Exception;
}
