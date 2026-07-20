using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

if (args.Length != 3
    || !Guid.TryParse(args[0], out var runtimeEpoch)
    || !Guid.TryParse(args[1], out var storeId)
    || !Enum.TryParse<DurableProviderSafety>(args[2], out var providerSafety))
{
    throw new ArgumentException("Expected runtime epoch, store id, and provider safety arguments.");
}

var connectionString = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_REFERENCE_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("APPSURFACE_POSTGRES_REFERENCE_CONNECTION is required.");
}

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var stopwatch = Stopwatch.StartNew();
var events = new List<ReferenceCheckpointEvent>();
var registration = new ReferenceWorkRegistration(providerSafety);
var registry = new DurableWorkRegistry([registration]);
var options = new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId);
var client = new PostgreSqlDurableWorkClient(dataSource, registry, options);
var scope = new DurableScopeId($"reference-{providerSafety.ToString().ToLowerInvariant()}");
var request = new DurableWorkRequest(
    scope,
    new DurableCommandId($"command-{providerSafety.ToString().ToLowerInvariant()}"),
    $"idempotency-{providerSafety.ToString().ToLowerInvariant()}",
    registration.WorkName,
    registration.WorkVersion,
    registration.WorkCodec.EncodeObject("reference-payload"u8.ToArray()),
    providerSafety,
    new DurableWorkRetryPolicy(
        maximumAttempts: 3,
        maximumElapsedTime: TimeSpan.FromMinutes(1),
        initialRetryDelay: TimeSpan.FromMilliseconds(50),
        maximumRetryDelay: TimeSpan.FromSeconds(1),
        leaseDuration: TimeSpan.FromSeconds(1),
        renewalCadence: TimeSpan.FromMilliseconds(250),
        maximumLeaseLifetime: TimeSpan.FromSeconds(2),
        backoffAlgorithm: "exponential-v1"));
var accepted = await client.EnqueueAsync(request);
var acceptance = accepted.Value ?? throw new InvalidOperationException(accepted.Problem?.Problem);
events.Add(new ReferenceCheckpointEvent(
    "work.accept",
    "committed",
    "provider-owned",
    stopwatch.ElapsedMilliseconds));
var store = new PostgreSqlDurableWorkStore(dataSource, runtimeEpoch);
var candidate = (await store.DiscoverAsync(10)).Single(item => item.WorkId == acceptance.WorkId);
events.Add(new ReferenceCheckpointEvent(
    "work.discover",
    "found",
    "read-only",
    stopwatch.ElapsedMilliseconds));
var claim = await store.TryClaimAsync(candidate, "reference-child")
    ?? throw new InvalidOperationException("Reference child could not claim accepted Work.");
events.Add(new ReferenceCheckpointEvent(
    "work.claim",
    "committed",
    "provider-owned",
    stopwatch.ElapsedMilliseconds));
var permit = await store.TryAcquireEffectPermitAsync(claim)
    ?? throw new InvalidOperationException("Reference child could not commit the effect permit.");
events.Add(new ReferenceCheckpointEvent(
    "effect-permit.acquire",
    "committed",
    "provider-owned",
    stopwatch.ElapsedMilliseconds));

Console.WriteLine(JsonSerializer.Serialize(new ReferenceCheckpoint(
    "permit-committed",
    scope.Value,
    acceptance.WorkId.Value,
    claim.ActivityId,
    claim.AttemptNumber,
    permit.PermittedAtUtc,
    events)));
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);

/// <summary>Locates the reference child-process assembly from integration tests.</summary>
public sealed class ReferenceWorkloadHostMarker;

/// <summary>Reports the exact durable checkpoint reached before the parent terminates the child.</summary>
/// <param name="Phase">Named protocol phase.</param>
/// <param name="ScopeId">Owning scope identity.</param>
/// <param name="WorkId">Accepted Work identity.</param>
/// <param name="ActivityId">Immutable provider activity identity.</param>
/// <param name="AttemptNumber">Provider attempt number.</param>
/// <param name="ObservedAtUtc">Authoritative permit timestamp.</param>
/// <param name="Events">Ordered application and transaction checkpoints before process loss.</param>
public sealed record ReferenceCheckpoint(
    string Phase,
    string ScopeId,
    string WorkId,
    string ActivityId,
    int AttemptNumber,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ReferenceCheckpointEvent> Events);

/// <summary>Reports one privacy-safe operation checkpoint reached by the crash helper.</summary>
/// <param name="Operation">Stable operation name.</param>
/// <param name="Outcome">Observed safe outcome.</param>
/// <param name="TransactionBoundary">Database transaction ownership or absence.</param>
/// <param name="ElapsedMilliseconds">Monotonic elapsed time since helper startup.</param>
public sealed record ReferenceCheckpointEvent(
    string Operation,
    string Outcome,
    string TransactionBoundary,
    long ElapsedMilliseconds);

internal sealed class ReferenceWorkRegistration(DurableProviderSafety providerSafety) : DurableWorkRegistration(
    $"reference.{providerSafety.ToString().ToLowerInvariant()}",
    "v1",
    providerSafety,
    new ReferenceCodec(),
    new ReferenceCodec())
{
    public override bool CanReconcile => false;

    public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
        throw new NotSupportedException("The crash helper stops after the effect permit and never invokes Work.");

    public override ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The crash helper stops after the effect permit and never invokes Work.");

    public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The crash helper does not reconcile provider effects.");
}

internal sealed class ReferenceCodec : IDurablePayloadCodec
{
    public Type PayloadType => typeof(byte[]);
    public string ContractName => "reference.payload";
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
