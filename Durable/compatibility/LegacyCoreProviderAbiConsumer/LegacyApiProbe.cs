using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.LegacyAbi;

/// <summary>Compiled against the exact historical package graph, then loaded by the current package host.</summary>
public static class LegacyApiProbe
{
    /// <summary>Exercises historical Core/Provider constructors and invocation behavior.</summary>
    public static async Task<string> RunAsync()
    {
        var scope = new DurableScopeId("legacy-abi-scope");
        var workId = new DurableWorkId("legacy-abi-work");
        var payload = new DurableEncodedPayload(
            "legacy-abi.payload",
            "v1",
            DurableDataClassification.Operational,
            new byte[] { 1, 2, 3 });
        var identity = DurableWorkerExecutionIdentity.Create(
            "legacy-abi-activity",
            attemptNumber: 2,
            leaseGeneration: 3,
            scopeGeneration: 4,
            runtimeEpoch: "legacy-abi-epoch");
        var executionContext = new DurableWorkExecutionContext(
            scope,
            workId,
            "legacy-abi.work",
            "v1",
            payload,
            DurableProviderSafety.Idempotent,
            identity);
        var claim = new DurableClaimedWork(
            scope,
            workId,
            "legacy-abi-activity",
            "legacy-abi.work",
            "v1",
            payload,
            DurableProviderSafety.Idempotent,
            attemptNumber: 2,
            leaseGeneration: 3,
            scopeGeneration: 4,
            runtimeEpoch: "legacy-abi-epoch");
        var projectedContext = claim.ToExecutionContext();
        var inspected = new DurableWorkSnapshot(
            scope,
            workId,
            "legacy-abi-activity",
            "legacy-abi.work",
            "v1",
            DurableWorkState.Ready,
            DurableProviderSafety.Idempotent,
            "legacy-abi-provider-key",
            attemptNumber: 0,
            revision: 1,
            acceptedAtUtc: DateTimeOffset.UnixEpoch,
            dueAtUtc: DateTimeOffset.UnixEpoch,
            updatedAtUtc: DateTimeOffset.UnixEpoch,
            terminalAtUtc: null,
            terminalCode: null,
            result: null);

        using var services = new ServiceCollection().BuildServiceProvider();
        var registration = new LegacyRegistration();
        var prepared = DurableProviderWorkAdapter.Prepare(registration, services, claim);
        var result = await prepared.InvokeAsync().ConfigureAwait(false);
        if (executionContext.WorkId != workId
            || projectedContext.WorkId != workId
            || projectedContext.ExecutionIdentity.ProviderKey != "legacy-abi-activity"
            || inspected.WorkId != workId
            || result.Content.Span.SequenceEqual(new byte[] { 9, 8, 7 }) is false)
        {
            throw new InvalidOperationException("A historical Core/Provider API call changed its observed contract behavior.");
        }

        return string.Join(
            '|',
            typeof(DurableWorkExecutionContext).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            typeof(DurableClaimedWork).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            "context-constructor",
            "claim-constructor-and-projection",
            "inspection-constructor",
            "registration-override-and-prepared-invocation");
    }

    private sealed class LegacyRegistration() : DurableWorkRegistration(
        "legacy-abi.work",
        "v1",
        DurableProviderSafety.Idempotent,
        new LegacyPayloadCodec("legacy-abi.payload"),
        new LegacyPayloadCodec("legacy-abi.result"))
    {
        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(work);
            return new LegacyPreparedWork();
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent legacy ABI Work does not reconcile.");
    }

    private sealed class LegacyPreparedWork : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DurableEncodedPayload(
                "legacy-abi.result",
                "v1",
                DurableDataClassification.Operational,
                new byte[] { 9, 8, 7 }));
        }
    }

    private sealed class LegacyPayloadCodec(string contractName) : IDurablePayloadCodec
    {
        public Type PayloadType => typeof(byte[]);
        public string ContractName { get; } = contractName;
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload EncodeObject(object value) => new(
            ContractName,
            ContractVersion,
            Classification,
            (byte[])value,
            RetentionPolicyId);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            if (!StringComparer.Ordinal.Equals(payload.ContractName, ContractName))
            {
                throw new InvalidDataException("The historical ABI payload name changed.");
            }

            return payload.Content.ToArray();
        }
    }
}
