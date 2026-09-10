using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

internal static class GmailBackfillExitExample
{
    // docs:snippet durable-work-exit-gmail:start
    internal static void Register(IServiceCollection services) =>
        services.AddDurableWorkExit<GmailBackfillRequest, GmailBackfillResult, GmailBackfillExecutor>(
            "gmail.sender-list-backfill",
            "v2",
            new SystemTextJsonDurablePayloadCodec<GmailBackfillRequest>(
                "consumer.gmail-backfill.request",
                "v1",
                DurableDataClassification.ApprovedApplication,
                GmailBackfillJsonContext.Default.GmailBackfillRequest,
                static _ => true),
            new SystemTextJsonDurablePayloadCodec<GmailBackfillResult>(
                "consumer.gmail-backfill.result",
                "v1",
                DurableDataClassification.ApprovedApplication,
                GmailBackfillJsonContext.Default.GmailBackfillResult,
                static _ => true));
    // docs:snippet durable-work-exit-gmail:end
}

internal sealed record GmailBackfillRequest(bool SenderListWasRead);

internal sealed record GmailBackfillResult(int BackfilledCount);

internal sealed class GmailBackfillExecutor : IDurableWorkExitExecutor<GmailBackfillRequest, GmailBackfillResult>
{
    public ValueTask<DurableWorkExit<GmailBackfillResult>> ExecuteAsync(
        DurableWorkerEnvelope<GmailBackfillRequest> work,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            work.Payload!.SenderListWasRead
                ? DurableWorkExit<GmailBackfillResult>.Succeeded(new GmailBackfillResult(1))
                : DurableWorkExit<GmailBackfillResult>.RetryBeforeEffect("app.gmail.sender_list_transient"));
}

[JsonSerializable(typeof(GmailBackfillRequest))]
[JsonSerializable(typeof(GmailBackfillResult))]
internal sealed partial class GmailBackfillJsonContext : JsonSerializerContext;
