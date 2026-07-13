namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal static class PostgreSqlTestWorkContracts
{
    internal static IDurableWorkRegistry CreateRegistry(params PostgreSqlTestWorkContract[] contracts) =>
        new DurableWorkRegistry(contracts.Select(static contract => contract.CreateRegistration()));

    internal static IDurableWorkRegistry CreateDeleteProviderAccessRegistry() => CreateRegistry(
        Enum.GetValues<DurableProviderSafety>()
            .Select(static safety => new PostgreSqlTestWorkContract(
                DeleteProviderAccessName(safety),
                "v1",
                safety,
                "tests.delete-provider-access",
                "v1"))
            .ToArray());

    internal static string DeleteProviderAccessName(DurableProviderSafety safety) =>
        $"tests.delete-provider-access.{safety.ToString().ToLowerInvariant()}";
}

internal sealed record PostgreSqlTestWorkContract(
    string WorkName,
    string WorkVersion,
    DurableProviderSafety ProviderSafety,
    string ContractName,
    string ContractVersion,
    DurableDataClassification Classification = DurableDataClassification.ApprovedApplication)
{
    internal DurableWorkRegistration CreateRegistration()
    {
        var workCodec = new PostgreSqlOpaqueTestCodec(ContractName, ContractVersion, Classification);
        var resultCodec = new PostgreSqlOpaqueTestCodec($"{ContractName}.result", ContractVersion, Classification);
        return new PostgreSqlOpaqueTestWorkRegistration(
            WorkName,
            WorkVersion,
            ProviderSafety,
            workCodec,
            resultCodec);
    }
}

internal sealed class PostgreSqlOpaqueTestCodec(
    string contractName,
    string contractVersion,
    DurableDataClassification classification = DurableDataClassification.ApprovedApplication) : IDurablePayloadCodec
{
    public Type PayloadType => typeof(byte[]);

    public string ContractName { get; } = contractName;

    public string ContractVersion { get; } = contractVersion;

    public DurableDataClassification Classification { get; } = classification;

    public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

    public DurableEncodedPayload EncodeObject(object value)
    {
        var bytes = Assert.IsType<byte[]>(value);
        return new DurableEncodedPayload(
            ContractName,
            ContractVersion,
            Classification,
            bytes,
            RetentionPolicyId);
    }

    public object DecodeObject(DurableEncodedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(payload.ContractName, ContractName, StringComparison.Ordinal)
            || !string.Equals(payload.ContractVersion, ContractVersion, StringComparison.Ordinal)
            || payload.Classification != Classification
            || !string.Equals(payload.RetentionPolicyId, RetentionPolicyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test payload does not match its registered contract.");
        }

        return payload.Content.ToArray();
    }
}

internal sealed class PostgreSqlOpaqueTestWorkRegistration(
    string workName,
    string workVersion,
    DurableProviderSafety providerSafety,
    IDurablePayloadCodec workCodec,
    IDurablePayloadCodec resultCodec) :
    DurableWorkRegistration(workName, workVersion, providerSafety, workCodec, resultCodec)
{
    public override bool CanReconcile => false;

    public override DurablePreparedWorkInvocation Prepare(IServiceProvider services, DurableClaimedWork work) =>
        throw new NotSupportedException("Storage tests do not invoke this registration.");

    public override ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableClaimedWork work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage tests do not invoke this registration.");

    public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableClaimedWork work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage tests do not reconcile this registration.");
}
