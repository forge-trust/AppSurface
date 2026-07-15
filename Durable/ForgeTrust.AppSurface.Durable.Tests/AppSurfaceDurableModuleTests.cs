using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class AppSurfaceDurableModuleTests
{
    [Fact]
    public void PassiveRegistrationProof_registers_only_host_neutral_registries()
    {
        var services = new ServiceCollection();

        new AppSurfaceDurableModule().ConfigureServices(
            new StartupContext([], new TestHostModule()),
            services);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DurablePayloadCodecRegistry>(provider.GetRequiredService<IDurablePayloadCodecRegistry>());
        Assert.IsType<DurableWorkRegistry>(provider.GetRequiredService<IDurableWorkRegistry>());
        Assert.IsType<DurableFlowRegistry>(provider.GetRequiredService<IDurableFlowRegistry>());
        Assert.Null(provider.GetService<IDurableWorkClient>());
        Assert.Null(provider.GetService<IDurableFlowClient>());
        Assert.Null(provider.GetService<IDurableScheduleClient>());
        Assert.Empty(provider.GetServices<IHostedService>());
        Console.WriteLine("contracts registered; no runtime installed");
    }

    [Fact]
    public void RegisterDependentModules_adds_flow_and_workers()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceDurableModule().RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, module => module is AppSurfaceFlowModule);
        Assert.Contains(builder.Modules, module => module is AppSurfaceWorkersModule);
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
