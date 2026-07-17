using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

var claim = new DurableClaimedWork(
    new DurableScopeId("consumer-scope"),
    new DurableWorkId("consumer-work"),
    "consumer-activity",
    "consumer.work",
    "v1",
    new DurableEncodedPayload(
        "consumer.payload",
        "v1",
        DurableDataClassification.Operational,
        "consumer"u8.ToArray()),
    DurableProviderSafety.ProviderKeyed,
    attemptNumber: 1,
    leaseGeneration: 1,
    scopeGeneration: 1,
    runtimeEpoch: "consumer-epoch");
var context = claim.ToExecutionContext();

if (context.ExecutionIdentity.ProviderKey != claim.ActivityId)
{
    throw new InvalidOperationException("Provider claim did not preserve its public execution identity contract.");
}

Console.WriteLine($"{DurableRuntimeHealthState.Healthy}|{context.ExecutionIdentity.ProviderKey}");
