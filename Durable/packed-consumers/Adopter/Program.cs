using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    throw new InvalidOperationException("Adopter-only registration unexpectedly installed a runtime.");
}

var scope = new DurableScopeId("consumer-scope");
var command = new DurableCommandId("consumer-command");
var payload = new DurableEncodedPayload(
    "consumer.payload",
    "v1",
    DurableDataClassification.Operational,
    "consumer"u8.ToArray());
var work = new DurableWorkRequest(
    scope,
    command,
    "consumer-work-retry",
    "consumer.work",
    "v1",
    payload,
    DurableProviderSafety.Idempotent);
var flow = new DurableFlowStartRequest(
    scope,
    command,
    "consumer-flow-retry",
    new DurableFlowInstanceId("consumer-flow"),
    "consumer.flow",
    "v1",
    payload);
var schedule = DurableSchedule.After(TimeSpan.FromMinutes(5));

Console.WriteLine($"{work.Fingerprint.SchemaId}|{flow.Fingerprint.SchemaId}|{schedule.Kind}");

sealed class PassiveHostModule : IAppSurfaceHostModule
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
