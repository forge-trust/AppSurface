using System.ComponentModel.DataAnnotations;
using FakeItEasy;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Config.Tests;

public class TestConfig : Config<TestConfig>
{
}

public class TestHostModule : IRunnableHostModule
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

public class TrackingTestConfig : Config<TrackingTestConfig>
{
    public bool InitCalled { get; private set; }

    internal override void Init(IConfigManager configManager, IEnvironmentProvider environmentProvider, string keyPath)
    {
        InitCalled = true;
        base.Init(configManager, environmentProvider, keyPath);
    }
}

public class InvalidRegisteredOptions
{
    [Required]
    public string? Name { get; init; }
}

public class InvalidRegisteredConfig : Config<InvalidRegisteredOptions>
{
}

public class RunnableConfigModuleTests
{
    [Fact]
    public void ConfigureServices_AddsRequiredServices()
    {
        var services = new ServiceCollection();
        var rootModule = A.Fake<IRunnableHostModule>();
        var context = new StartupContext([], rootModule);
        var module = new RunnableConfigModule();

        module.ConfigureServices(context, services);

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigManager) && d.ImplementationType == typeof(DefaultConfigManager));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IEnvironmentConfigProvider)
                 && d.ImplementationType == typeof(EnvironmentConfigProvider));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigFileLocationProvider)
                 && d.ImplementationType == typeof(DefaultConfigFileLocationProvider));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigProvider) && d.ImplementationType == typeof(FileBasedConfigProvider));

        // One registration for the module's assembly scanning task
        Assert.Single(context.CustomRegistrations);
    }

    [Fact]
    public void CustomRegistrationTask_ScansAssembliesAndRegistersConfigs()
    {
        var services = new ServiceCollection();
        var rootModule = new TestHostModule();

        var context = new StartupContext([], rootModule)
        {
            OverrideEntryPointAssembly = typeof(RunnableConfigModuleTests).Assembly
        };
        context.Dependencies.AddModule<TestHostModule>();

        var module = new RunnableConfigModule();
        var moduleBuilder = new ModuleDependencyBuilder();
        module.RegisterDependentModules(moduleBuilder);

        module.ConfigureServices(context, services);
        var registrationTask = context.CustomRegistrations[0];

        // Invoke the task
        registrationTask(services);

        // Verify it registered TestConfig and TrackingTestConfig
        Assert.Contains(services, d => d.ServiceType == typeof(TestConfig));
        Assert.Contains(services, d => d.ServiceType == typeof(TrackingTestConfig));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigAuditKnownEntry)
                 && d.ImplementationInstance is ConfigAuditKnownEntry entry
                 && entry.ConfigType == typeof(TrackingTestConfig)
                 && entry.Key == nameof(TrackingTestConfig));

        // Test the factory activation
        var configManager = A.Fake<IConfigManager>();
        var envProvider = A.Fake<IEnvironmentProvider>();
        services.AddSingleton(configManager);
        services.AddSingleton(envProvider);
        services.AddSingleton(A.Fake<ILogger<DefaultConfigManager>>());

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<TrackingTestConfig>();

        Assert.NotNull(resolved);
        Assert.True(resolved.InitCalled);

        // Verify Init was called via ConfigManager too
        A.CallTo(() => configManager.GetValue<TrackingTestConfig>(A<string>._, A<string>._))
            .MustHaveHappened();
    }

    [Fact]
    public void CustomRegistrationTask_InvalidConfigThrowsDuringServiceActivation()
    {
        var services = new ServiceCollection();
        var rootModule = new TestHostModule();

        var context = new StartupContext([], rootModule)
        {
            OverrideEntryPointAssembly = typeof(RunnableConfigModuleTests).Assembly
        };
        context.Dependencies.AddModule<TestHostModule>();

        var module = new RunnableConfigModule();
        module.ConfigureServices(context, services);
        context.CustomRegistrations[0](services);

        var configManager = A.Fake<IConfigManager>();
        var envProvider = A.Fake<IEnvironmentProvider>();
        services.AddSingleton(configManager);
        services.AddSingleton(envProvider);
        services.AddSingleton(A.Fake<ILogger<DefaultConfigManager>>());

        A.CallTo(() => envProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<InvalidRegisteredOptions>(A<string>._, A<string>._))
            .Returns(new InvalidRegisteredOptions());

        var sp = services.BuildServiceProvider();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            sp.GetRequiredService<InvalidRegisteredConfig>());

        Assert.Equal("InvalidRegisteredConfig", exception.Key);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Name"]));
    }
}
