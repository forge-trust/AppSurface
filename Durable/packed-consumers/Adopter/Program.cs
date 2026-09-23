using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

ForgeTrust.AppSurface.Durable.Examples.TypedWorkDefinitionProof.Run();

var services = new ServiceCollection();
new AppSurfaceDurableModule().ConfigureServices(
    new StartupContext([], new PassiveHostModule()),
    services);
GmailBackfillExitExample.Register(services);
services.AddDurableWork<LegacyConsumerWork, LegacyConsumerResult, LegacyConsumerExecutor>(
    "consumer.legacy-work",
    "v1",
    DurableProviderSafety.Idempotent,
    new SystemTextJsonDurablePayloadCodec<LegacyConsumerWork>(
        "consumer.legacy-work.request",
        "v1",
        DurableDataClassification.ApprovedApplication,
        ConsumerJsonContext.Default.LegacyConsumerWork,
        static _ => true),
    new SystemTextJsonDurablePayloadCodec<LegacyConsumerResult>(
        "consumer.legacy-work.result",
        "v1",
        DurableDataClassification.ApprovedApplication,
        ConsumerJsonContext.Default.LegacyConsumerResult,
        static _ => true));
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
var succeededExit = DurableWorkExit<GmailBackfillResult>.Succeeded(new GmailBackfillResult(1));
var retryExit = DurableWorkExit<GmailBackfillResult>.RetryBeforeEffect("app.gmail.sender_list_transient");
if (succeededExit.Kind != DurableWorkExitKind.Succeeded
    || succeededExit.Code is not null
    || succeededExit.Result?.BackfilledCount != 1
    || retryExit.Kind != DurableWorkExitKind.RetryBeforeEffect
    || retryExit.Code != "app.gmail.sender_list_transient"
    || retryExit.Result is not null)
{
    throw new InvalidOperationException("Typed Durable Work exits must expose their documented kind, code, and result values.");
}

Console.WriteLine($"{work.Fingerprint.SchemaId}|{flow.Fingerprint.SchemaId}|{schedule.Kind}");

sealed record LegacyConsumerWork(string Value);

sealed record LegacyConsumerResult(string Value);

sealed class LegacyConsumerExecutor : IDurableWorkerExecutor<LegacyConsumerWork, LegacyConsumerResult>
{
    public ValueTask<LegacyConsumerResult> ExecuteAsync(
        DurableWorkerEnvelope<LegacyConsumerWork> work,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new LegacyConsumerResult(work.Payload!.Value));
}

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

[JsonSerializable(typeof(LegacyConsumerWork))]
[JsonSerializable(typeof(LegacyConsumerResult))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
