using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerRegistrationTests
{
    [Fact]
    public void ConfigureServices_Should_RegisterProviderClientOptionsAndConfigProvider()
    {
        var services = new ServiceCollection();
        var client = new FakeSecretManagerClient();
        services.UseAppSurfaceGoogleSecretManagerClient(client);
        services.ConfigureAppSurfaceGoogleSecretManager(options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
        });
        var module = new AppSurfaceGoogleSecretManagerModule();

        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceGoogleSecretManagerOptions>>().Value;
        Assert.Equal("project", options.ProjectId);
        Assert.Same(client, provider.GetRequiredService<IAppSurfaceGoogleSecretManagerClient>());
        Assert.Same(
            provider.GetRequiredService<GoogleSecretManagerConfigProvider>(),
            provider.GetServices<IConfigProvider>().Single(config => config is GoogleSecretManagerConfigProvider));
        Assert.Contains(
            provider.GetServices<IValidateOptions<AppSurfaceGoogleSecretManagerOptions>>(),
            validator => validator is AppSurfaceGoogleSecretManagerOptionsValidator);
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretManagerClient_Should_RegisterClientType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceGoogleSecretManagerClient>(new FakeSecretManagerClient());

        services.UseAppSurfaceGoogleSecretManagerClient<FakeSecretManagerClient>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<FakeSecretManagerClient>(provider.GetRequiredService<IAppSurfaceGoogleSecretManagerClient>());
        Assert.Single(provider.GetServices<IAppSurfaceGoogleSecretManagerClient>());
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretManagerClient_Should_ReplacePriorClientInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceGoogleSecretManagerClient>(new FakeSecretManagerClient());
        var replacement = new FakeSecretManagerClient();

        services.UseAppSurfaceGoogleSecretManagerClient(replacement);

        using var provider = services.BuildServiceProvider();
        Assert.Same(replacement, provider.GetRequiredService<IAppSurfaceGoogleSecretManagerClient>());
        Assert.Single(provider.GetServices<IAppSurfaceGoogleSecretManagerClient>());
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretManagerClient_Should_RejectNullArguments()
    {
        var services = new ServiceCollection();

        var nullServicesForType = Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionGoogleSecretManagerExtensions.UseAppSurfaceGoogleSecretManagerClient<FakeSecretManagerClient>(null!));
        var nullServicesForInstance = Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionGoogleSecretManagerExtensions.UseAppSurfaceGoogleSecretManagerClient(null!, new FakeSecretManagerClient()));
        var nullClient = Assert.Throws<ArgumentNullException>(() =>
            services.UseAppSurfaceGoogleSecretManagerClient(null!));

        Assert.Equal("services", nullServicesForType.ParamName);
        Assert.Equal("services", nullServicesForInstance.ParamName);
        Assert.Equal("client", nullClient.ParamName);
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretTransferClient_Should_RegisterClientType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceGoogleSecretTransferClient>(new FakeSecretTransferClient());

        var returned = services.UseAppSurfaceGoogleSecretTransferClient<FakeSecretTransferClient>();

        using var provider = services.BuildServiceProvider();
        Assert.Same(services, returned);
        Assert.IsType<FakeSecretTransferClient>(provider.GetRequiredService<IAppSurfaceGoogleSecretTransferClient>());
        Assert.Single(provider.GetServices<IAppSurfaceGoogleSecretTransferClient>());
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretTransferClient_Should_ReplacePriorClientInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceGoogleSecretTransferClient>(new FakeSecretTransferClient());
        var replacement = new FakeSecretTransferClient();

        var returned = services.UseAppSurfaceGoogleSecretTransferClient(replacement);

        using var provider = services.BuildServiceProvider();
        Assert.Same(services, returned);
        Assert.Same(replacement, provider.GetRequiredService<IAppSurfaceGoogleSecretTransferClient>());
        Assert.Single(provider.GetServices<IAppSurfaceGoogleSecretTransferClient>());
    }

    [Fact]
    public void UseAppSurfaceGoogleSecretTransferClient_Should_RejectNullArguments()
    {
        var services = new ServiceCollection();

        var nullServicesForType = Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionGoogleSecretManagerExtensions.UseAppSurfaceGoogleSecretTransferClient<FakeSecretTransferClient>(null!));
        var nullServicesForInstance = Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionGoogleSecretManagerExtensions.UseAppSurfaceGoogleSecretTransferClient(null!, new FakeSecretTransferClient()));
        var nullClient = Assert.Throws<ArgumentNullException>(() =>
            services.UseAppSurfaceGoogleSecretTransferClient(null!));

        Assert.Equal("services", nullServicesForType.ParamName);
        Assert.Equal("services", nullServicesForInstance.ParamName);
        Assert.Equal("client", nullClient.ParamName);
    }

    [Fact]
    public void RegisterDependentModules_Should_AddConfigModuleDependency()
    {
        var module = new AppSurfaceGoogleSecretManagerModule();
        var builder = new ModuleDependencyBuilder();

        module.RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, dependency => dependency is AppSurfaceConfigModule);
    }

    private sealed class FakeSecretManagerClient : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) =>
            new([], resourceName);
    }

    private sealed class FakeSecretTransferClient : IAppSurfaceGoogleSecretTransferClient
    {
        public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout) =>
            throw new NotSupportedException();

        public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout) =>
            throw new NotSupportedException();

        public AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout) =>
            throw new NotSupportedException();

        public AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}
