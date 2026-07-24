using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

if (args.Length != 2
    || !Guid.TryParse(args[0], out var runtimeEpoch)
    || !Guid.TryParse(args[1], out var storeId))
{
    throw new ArgumentException("Expected runtime epoch and store id arguments.");
}

var connectionString = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_REFERENCE_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("APPSURFACE_POSTGRES_REFERENCE_CONNECTION is required.");
}

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var providerAssembly = typeof(PostgreSqlDurableWorkClient).Assembly;
var migrationResources = providerAssembly.GetManifestResourceNames()
    .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal))
    .ToArray();
if (migrationResources.Length == 0)
{
    throw new InvalidOperationException(
        $"Pinned v2 provider '{providerAssembly.Location}' contains no embedded migration resources.");
}

var registration = new V2WorkRegistration();
var registry = new DurableWorkRegistry([registration]);
var client = new PostgreSqlDurableWorkClient(
    dataSource,
    registry,
    new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId));
var scope = new DurableScopeId("v2-binary-compatibility");
var accepted = await client.EnqueueAsync(new DurableWorkRequest(
    scope,
    new DurableCommandId("v2-binary-command"),
    "v2-binary-idempotency",
    registration.WorkName,
    registration.WorkVersion,
    registration.WorkCodec.EncodeObject("v2-work"u8.ToArray()),
    DurableProviderSafety.Idempotent));
var acceptance = accepted.Value
    ?? throw new InvalidOperationException(accepted.Problem?.Problem ?? "V2 Work acceptance failed.");
var store = new PostgreSqlDurableWorkStore(dataSource, runtimeEpoch);
var candidate = (await store.DiscoverAsync(100)).Single(item =>
    item.ScopeId == scope && item.WorkId == acceptance.WorkId);
var claim = await store.TryClaimAsync(candidate, "v2-binary-worker")
    ?? throw new InvalidOperationException("V2 Work claim failed.");
var permit = await store.TryAcquireEffectPermitAsync(claim)
    ?? throw new InvalidOperationException("V2 Work permit failed.");
var completion = await store.RecordCompletionAsync(
    permit.Claim,
    new PostgreSqlWorkCompletion(
        PostgreSqlWorkCompletionKind.Succeeded,
        "v2_binary_completed",
        "{}",
        registration.ResultCodec.EncodeObject("v2-result"u8.ToArray())));

Console.WriteLine(JsonSerializer.Serialize(new
{
    Phase = "v2-terminal",
    ScopeId = scope.Value,
    WorkId = acceptance.WorkId.Value,
    State = completion.State.ToString(),
    Revision = completion.Revision,
}));

internal sealed class V2WorkRegistration() : DurableWorkRegistration(
    "compatibility.v2-work",
    "v1",
    DurableProviderSafety.Idempotent,
    new V2Codec("compatibility.v2-work"),
    new V2Codec("compatibility.v2-result"))
{
    public override bool CanReconcile => false;

    public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
        throw new NotSupportedException("The compatibility harness drives storage directly.");

    public override ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The compatibility harness drives storage directly.");

    public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The compatibility harness does not reconcile provider effects.");
}

internal sealed class V2Codec(string contractName) : IDurablePayloadCodec
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

    public object DecodeObject(DurableEncodedPayload payload) => payload.Content.ToArray();
}
