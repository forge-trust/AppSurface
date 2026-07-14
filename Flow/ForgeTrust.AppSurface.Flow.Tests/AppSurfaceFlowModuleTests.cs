using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class AppSurfaceFlowModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersFlowServices()
    {
        var services = new ServiceCollection();
        var module = new AppSurfaceFlowModule();

        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IOptions<AppSurfaceFlowOptions>>().Value);
        Assert.IsType<FlowDefinitionRegistry>(provider.GetRequiredService<IFlowDefinitionRegistry>());
        var evaluator = provider.GetRequiredService<IFlowTransitionEvaluator<TestState>>();
        var runner = provider.GetRequiredService<IFlowRunner<TestState>>();
        Assert.IsType<FlowTransitionEvaluator<TestState>>(evaluator);
        Assert.IsType<InMemoryFlowRunner<TestState>>(runner);
        Assert.Same(evaluator, provider.GetRequiredService<IFlowTransitionEvaluator<TestState>>());
        Assert.Same(runner, provider.GetRequiredService<IFlowRunner<TestState>>());
    }

    [Fact]
    public void RegisterDependentModules_DoesNothing()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceFlowModule().RegisterDependentModules(builder);

        Assert.Empty(builder.Modules);
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
