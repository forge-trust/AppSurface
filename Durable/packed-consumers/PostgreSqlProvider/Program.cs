using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

await using var dataSource = NpgsqlDataSource.Create(
    "Host=127.0.0.1;Port=5432;Database=durable_consumer;Username=durable");
var registry = new DurableWorkRegistry([]);
var options = new PostgreSqlDurableWorkOptions(
    runtimeEpoch: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
    expectedStoreId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

IDurableWorkTransactionWriter writer = new PostgreSqlDurableWorkTransactionWriter(
    dataSource,
    registry,
    options);
IDurableWorkClient client = new PostgreSqlDurableWorkClient(dataSource, registry, options);
IFlowRepairOperatorClient repair = new PostgreSqlDurableFlowRepairOperatorClient(dataSource, registry, options);

if (options.WakeNotificationMode != PostgreSqlDurableWakeNotificationMode.Disabled)
{
    throw new InvalidOperationException("PostgreSQL wake notifications must remain explicit and default off.");
}

var request = new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work);
var attempts = new ContractOnlyPump[]
{
    new(DurableRuntimePumpAttemptKind.Completed),
    new(DurableRuntimePumpAttemptKind.Refused),
    new(DurableRuntimePumpAttemptKind.Unavailable),
    new(DurableRuntimePumpAttemptKind.Incompatible),
};

foreach (var admission in attempts)
{
    var attempt = await admission.TryRunOnceAsync(request);
    switch (attempt.Kind)
    {
        case DurableRuntimePumpAttemptKind.Completed:
            if (attempt.Result is null || attempt.ProblemCode is not null)
            {
                throw new InvalidOperationException("Completed must contain only a result.");
            }

            break;
        case DurableRuntimePumpAttemptKind.Refused:
            if (attempt.Result is not null || attempt.ProblemCode is not null)
            {
                throw new InvalidOperationException("Refused must contain neither a result nor a problem code.");
            }

            break;
        case DurableRuntimePumpAttemptKind.Unavailable:
            if (attempt.Result is not null || attempt.ProblemCode != DurableProblemCodes.StoreUnavailable)
            {
                throw new InvalidOperationException("Unavailable must contain ASDUR103 and no result.");
            }

            break;
        case DurableRuntimePumpAttemptKind.Incompatible:
            if (attempt.Result is not null || attempt.ProblemCode is null)
            {
                throw new InvalidOperationException("Incompatible must contain a problem code and no result.");
            }

            break;
        default:
            throw new ArgumentOutOfRangeException(
                nameof(attempt.Kind),
                attempt.Kind,
                "Unknown durable pump attempt kind.");
    }

    var expectedExecutionCalls = attempt.Kind == DurableRuntimePumpAttemptKind.Completed ? 1 : 0;
    if (admission.ExecutionCalls != expectedExecutionCalls)
    {
        throw new InvalidOperationException(
            $"{attempt.Kind} recorded {admission.ExecutionCalls} execution calls; expected {expectedExecutionCalls}.");
    }
}

var canceledAdmission = new ContractOnlyPump(DurableRuntimePumpAttemptKind.Completed);
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        _ = await canceledAdmission.TryRunOnceAsync(request, cancellation.Token);
        throw new InvalidOperationException("A pre-canceled admission call must propagate cancellation.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        // Expected: Try does not convert caller cancellation into an attempt kind.
    }
}

if (canceledAdmission.ExecutionCalls != 0)
{
    throw new InvalidOperationException("A pre-canceled contract-only call must not enter execution.");
}

var expectedException = new InvalidOperationException("contract-only execution failure");
IDurableRuntimePumpAdmission throwingAdmission = new ThrowingContractOnlyAdmission(expectedException);
try
{
    _ = await throwingAdmission.TryRunOnceAsync(request);
    throw new InvalidOperationException("An admission exception must propagate.");
}
catch (InvalidOperationException exception) when (ReferenceEquals(exception, expectedException))
{
    // Expected: Try does not translate an execution exception into an attempt kind.
}

var compositionServices = new ServiceCollection();
compositionServices.AddSingleton<ContractOnlyPump>(_ => new ContractOnlyPump(DurableRuntimePumpAttemptKind.Completed));
compositionServices.AddSingleton<IDurableRuntimePump>(
    provider => provider.GetRequiredService<ContractOnlyPump>());
compositionServices.AddSingleton<IDurableRuntimePumpAdmission>(
    provider => provider.GetRequiredService<ContractOnlyPump>());
using var composition = compositionServices.BuildServiceProvider();
if (!ReferenceEquals(
        composition.GetRequiredService<IDurableRuntimePump>(),
        composition.GetRequiredService<IDurableRuntimePumpAdmission>()))
{
    throw new InvalidOperationException("Both public pump interfaces must resolve to the same singleton.");
}

Console.WriteLine(
    $"{writer.GetType().Name}|{client.GetType().Name}|{repair.GetType().Name}|{options.WakeNotificationMode}|" +
    "contract-fake-admission=Completed,Refused,Unavailable,Incompatible|" +
    "contract-fake-non-completed-execution-calls=0|contract-fake-cancellation=propagated|" +
    "contract-fake-exception=same-instance|composition=shared-singleton");

sealed class ContractOnlyPump(DurableRuntimePumpAttemptKind kind) : IDurableRuntimePump, IDurableRuntimePumpAdmission
{
    public int ExecutionCalls { get; private set; }

    public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionCalls++;
        return ValueTask.FromResult(EmptyResult());
    }

    public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return kind switch
        {
            DurableRuntimePumpAttemptKind.Completed => Completed(),
            DurableRuntimePumpAttemptKind.Refused => ValueTask.FromResult(new DurableRuntimePumpAttempt(kind, null, null)),
            DurableRuntimePumpAttemptKind.Unavailable => ValueTask.FromResult(new DurableRuntimePumpAttempt(
                kind, null, DurableProblemCodes.StoreUnavailable)),
            DurableRuntimePumpAttemptKind.Incompatible => ValueTask.FromResult(new DurableRuntimePumpAttempt(
                kind, null, DurableProblemCodes.SchemaUpgradeRequired)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown durable pump attempt kind."),
        };
    }

    private ValueTask<DurableRuntimePumpAttempt> Completed()
    {
        ExecutionCalls++;
        return ValueTask.FromResult(new DurableRuntimePumpAttempt(kind, EmptyResult(), null));
    }

    private static DurableRuntimePumpResult EmptyResult() =>
        new(0, 0, 0, 0, 0, false, null, TimeSpan.Zero);
}

sealed class ThrowingContractOnlyAdmission(InvalidOperationException exception) : IDurableRuntimePumpAdmission
{
    public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
    }
}
