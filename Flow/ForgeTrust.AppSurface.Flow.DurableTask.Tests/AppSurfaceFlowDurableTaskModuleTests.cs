using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class AppSurfaceFlowDurableTaskModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersDurableAdapterServices()
    {
        var services = new ServiceCollection();
        var module = new AppSurfaceFlowDurableTaskModule();

        new AppSurfaceFlowModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IOptions<AppSurfaceFlowDurableTaskOptions>>().Value);
        Assert.IsType<SystemTextJsonFlowContextSerializer>(provider.GetRequiredService<IFlowContextSerializer>());
        Assert.IsType<DenyAllFlowResumeAuthorizer>(provider.GetRequiredService<IFlowResumeAuthorizer>());
        Assert.IsType<DurableTaskFlowRunner<TestState>>(provider.GetRequiredService<IDurableTaskFlowRunner<TestState>>());
        Assert.IsType<DurableTaskFlowClient<TestState>>(provider.GetRequiredService<IDurableTaskFlowClient<TestState>>());
    }

    [Fact]
    public void RegisterDependentModules_AddsCoreFlowModule()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceFlowDurableTaskModule().RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, module => module is AppSurfaceFlowModule);
    }

    private sealed record TestState(string Value);

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
