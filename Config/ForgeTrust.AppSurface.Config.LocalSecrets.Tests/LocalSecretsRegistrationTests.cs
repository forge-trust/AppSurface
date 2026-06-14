using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretsRegistrationTests
{
    [Fact]
    public void ConfigureServices_Should_RegisterProviderAndConfigProvider()
    {
        var services = new ServiceCollection();
        services.UseAppSurfaceLocalSecretStore(new InMemoryAppSurfaceLocalSecretStore());
        services.ConfigureAppSurfaceLocalSecrets(options =>
        {
            options.ApplicationName = "MyApp";
            options.Posture = LocalSecretsPostureMode.SingleMachineSelfHosted;
        });
        var module = new AppSurfaceLocalSecretsModule();

        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceLocalSecretsOptions>>().Value;
        Assert.Equal("MyApp", options.ApplicationName);
        Assert.Equal(LocalSecretsPostureMode.SingleMachineSelfHosted, options.Posture);
        Assert.IsType<InMemoryAppSurfaceLocalSecretStore>(provider.GetRequiredService<IAppSurfaceLocalSecretStore>());
        Assert.Same(
            provider.GetRequiredService<AppSurfaceLocalSecretProvider>(),
            provider.GetServices<IConfigProvider>().Single(config => config is AppSurfaceLocalSecretProvider));
    }

    [Fact]
    public void UseAppSurfaceLocalSecretStore_Should_RegisterStoreType()
    {
        var services = new ServiceCollection();

        services.UseAppSurfaceLocalSecretStore<InMemoryAppSurfaceLocalSecretStore>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryAppSurfaceLocalSecretStore>(provider.GetRequiredService<IAppSurfaceLocalSecretStore>());
    }

    [Fact]
    public void RegisterDependentModules_Should_AddConfigModuleDependency()
    {
        var module = new AppSurfaceLocalSecretsModule();
        var builder = new ModuleDependencyBuilder();

        module.RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, dependency => dependency is AppSurfaceConfigModule);
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
