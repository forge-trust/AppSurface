// docs:snippet durable-passive-registration:start
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Durable.Examples;

internal static class PassiveRegistrationProof
{
    internal static void Run()
    {
        var services = new ServiceCollection();
        new AppSurfaceDurableModule().ConfigureServices(
            new StartupContext([], new PassiveHostModule()),
            services);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        _ = provider.GetRequiredService<IDurableWorkRegistry>();
        _ = provider.GetRequiredService<IDurableFlowRegistry>();
        if (provider.GetService<IDurableWorkClient>() is not null
            || provider.GetService<IDurableFlowClient>() is not null
            || provider.GetService<IDurableScheduleClient>() is not null
            || provider.GetServices<IHostedService>().Any())
        {
            throw new InvalidOperationException("Durable contract registration must remain passive.");
        }

        Console.WriteLine("contracts registered; no runtime installed");
    }

    private sealed class PassiveHostModule : IAppSurfaceHostModule
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
// docs:snippet durable-passive-registration:end
