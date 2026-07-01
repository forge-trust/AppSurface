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

    [Fact]
    public void ConfigureServices_RejectsNullInputs()
    {
        var module = new AppSurfaceWorkersDurableTaskModule();
        var context = new StartupContext([], new TestHostModule());
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => module.ConfigureServices(null!, services));
        Assert.Throws<ArgumentNullException>(() => module.ConfigureServices(context, null!));
    }

    [Fact]
    public void ConfigureServices_DoesNotOverwriteExistingRunnerRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDurableTaskWorkerChainRunner<string, string, string>, ExistingRunner>();

        new AppSurfaceWorkersDurableTaskModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ExistingRunner>(
            provider.GetRequiredService<IDurableTaskWorkerChainRunner<string, string, string>>());
    }

    [Fact]
    public void RegisterDependentModules_RejectsNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AppSurfaceWorkersDurableTaskModule().RegisterDependentModules(null!));
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

    private sealed class ExistingRunner : IDurableTaskWorkerChainRunner<string, string, string>
    {
        public ValueTask<DurableTaskWorkerDecision<string, string, string>> TryClaimAsync(
            IDurableWorkerProjectionContract<string, string, string> contract,
            string work,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DurableTaskWorkerDecision<string, string, string>> CompleteAsync(
            IDurableWorkerProjectionContract<string, string, string> contract,
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DurableTaskWorkerDecision<string, string, string>> ReconcileProjectionAsync(
            IDurableWorkerProjectionContract<string, string, string> contract,
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public DurableTaskWorkerDecision<string, string, string> WaitForExternalEvent(
            DurableWorkerCorrelation correlation,
            string eventName,
            FlowTimeout? timeout = null) =>
            throw new NotSupportedException();

        public DurableTaskWorkerDecision<string, string, string> TimedOut(
            DurableWorkerCorrelation correlation,
            string eventName,
            DurableWorkerDiagnostic? diagnostic = null) =>
            throw new NotSupportedException();

        public DurableTaskWorkerDecision<string, string, string> IgnoreLateSignal(
            DurableWorkerCorrelation correlation,
            string eventName,
            DurableWorkerDiagnostic? diagnostic = null) =>
            throw new NotSupportedException();
    }
}
