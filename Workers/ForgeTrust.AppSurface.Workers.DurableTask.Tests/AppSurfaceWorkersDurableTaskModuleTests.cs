using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Workers;
using ForgeTrust.AppSurface.Workers.DurableTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Workers.DurableTask.Tests;

public sealed class AppSurfaceWorkersDurableTaskModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersDurableWorkerAdapter()
    {
        var services = new ServiceCollection();
        var module = new AppSurfaceWorkersDurableTaskModule();

        new AppSurfaceWorkersModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        new AppSurfaceFlowModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        new AppSurfaceFlowDurableTaskModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<AppSurfaceWorkersDurableTaskOptions>>().Value);
        Assert.IsType<DurableTaskWorkerChainRunner<string, string, string>>(
            provider.GetRequiredService<IDurableTaskWorkerChainRunner<string, string, string>>());
    }

    [Fact]
    public void RegisterDependentModules_AddsWorkersAndFlowDurableTaskModules()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceWorkersDurableTaskModule().RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, module => module is AppSurfaceWorkersModule);
        Assert.Contains(builder.Modules, module => module is AppSurfaceFlowDurableTaskModule);
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
