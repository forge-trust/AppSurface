using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.PostgreSql.Tests;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurablePackedExecutionPolicyConsumer;

public sealed partial class PlannedRetryPostgreSqlConsumerTests
{
    private static readonly DateTimeOffset AcceptanceAnchor =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task PackedProvider_HonorsAllFiveAcceptedPlanSlotsWithoutEarlyExecutions()
    {
        RetryBeforeEffectExecutor.Reset();
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);

        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "packed-consumer", "planned-retry-proof");
        var status = await schema.GetStatusAsync();
        Assert.True(status.IsCompatible);
        Assert.Equal(epoch, status.ActiveRuntimeEpoch);

        await using var dispatcher = database.CreateDataSource();
        var definition = CreateDefinition();
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedByExit<RetryBeforeEffectExecutor>());
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("postgres"));

        using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("packed-execution-policy");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            definition.CreateRequestWithExecutionPolicy(
                scope,
                new DurableCommandId("planned-retry"),
                "planned-retry-key",
                new PlannedRetryWork("probe")));
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);

        var pump = provider.GetRequiredService<IDurableRuntimePumpAdmission>();
        var pass = new DurableRuntimePumpRequest(maximumItems: 4, surfaces: DurableRuntimeSurface.Work);
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var offsets = new[] { 0, 5, 20, 60, 180 };
        for (var slot = 0; slot < offsets.Length; slot++)
        {
            await database.SetExecutionTimeAsync(AcceptanceAnchor.AddMinutes(offsets[slot]));
            var due = await pump.TryRunOnceAsync(pass);
            Assert.Equal(DurableRuntimePumpAttemptKind.Completed, due.Kind);
            Assert.Equal(slot + 1, Volatile.Read(ref RetryBeforeEffectExecutor.Calls));

            var observed = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
            Assert.True(observed.IsSuccess, observed.Problem?.Problem);
            Assert.Equal(AcceptanceAnchor, observed.Value!.AcceptedAtUtc);
            Assert.Equal(slot + 1, observed.Value.AttemptNumber);

            if (slot == offsets.Length - 1)
            {
                Assert.Equal(DurableWorkState.Succeeded, observed.Value.State);
                continue;
            }

            var nextSlot = AcceptanceAnchor.AddMinutes(offsets[slot + 1]);
            Assert.Equal(nextSlot, observed.Value.Execution!.NextEligibilityAtUtc);
            Assert.Equal(nextSlot, observed.Value.DueAtUtc);

            await database.SetExecutionTimeAsync(nextSlot.AddMinutes(-1));
            var early = await pump.TryRunOnceAsync(pass);
            Assert.Equal(DurableRuntimePumpAttemptKind.Completed, early.Kind);
            Assert.Equal(slot + 1, Volatile.Read(ref RetryBeforeEffectExecutor.Calls));
        }

        Assert.Equal(1, Volatile.Read(ref RetryBeforeEffectExecutor.LogicalEffects));
        Assert.Equal(5, Volatile.Read(ref RetryBeforeEffectExecutor.Calls));
    }

    [Fact]
    public async Task PackedProvider_RefusesAnExpiredDeadlineBeforeCallingExecutor()
    {
        RetryBeforeEffectExecutor.Reset();
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);

        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "packed-consumer", "expired-deadline-proof");
        var status = await schema.GetStatusAsync();
        Assert.True(status.IsCompatible);

        await using var dispatcher = database.CreateDataSource();
        var definition = CreateDefinition();
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedByExit<RetryBeforeEffectExecutor>());
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("postgres"));

        using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("packed-expired-deadline");
        var deadline = AcceptanceAnchor.AddMinutes(1);
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            definition.CreateRequestWithExecutionPolicy(
                scope,
                new DurableCommandId("expired-deadline"),
                "expired-deadline-key",
                new PlannedRetryWork("probe"),
                executionDeadline: new DurableExecutionDeadline(deadline)));
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);

        await database.SetExecutionTimeAsync(deadline);
        var pass = new DurableRuntimePumpRequest(maximumItems: 4, surfaces: DurableRuntimeSurface.Work);
        var attempted = await provider.GetRequiredService<IDurableRuntimePumpAdmission>().TryRunOnceAsync(pass);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempted.Kind);
        Assert.Equal(0, Volatile.Read(ref RetryBeforeEffectExecutor.Calls));
        Assert.Equal(0, Volatile.Read(ref RetryBeforeEffectExecutor.LogicalEffects));

        var closed = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(closed.IsSuccess, closed.Problem?.Problem);
        Assert.Equal(DurableWorkState.FailedTerminal, closed.Value!.State);
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, closed.Value.TerminalCode);
        Assert.Equal(0, closed.Value.AttemptNumber);
    }

    [Fact]
    public async Task PackedProvider_ReconcilesReadOnlyWithoutRepeatingTheLogicalEffect()
    {
        AmbiguousEffectExecutor.Reset();
        ReadOnlyEffectReconciler.Reset();
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);

        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "packed-consumer", "read-only-reconciliation-proof");
        var status = await schema.GetStatusAsync();
        Assert.True(status.IsCompatible);

        await using var dispatcher = database.CreateDataSource();
        var definition = CreateDefinition(DurableProviderSafety.ReconcileBeforeRetry);
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedBy<AmbiguousEffectExecutor>()
            .ReconciledBy<ReadOnlyEffectReconciler>());
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("postgres"));

        using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("packed-read-only-reconcile");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            definition.CreateRequestWithExecutionPolicy(
                scope,
                new DurableCommandId("ambiguous-provider-effect"),
                "ambiguous-provider-effect-key",
                new PlannedRetryWork("probe"),
                executionDeadline: new DurableExecutionDeadline(AcceptanceAnchor.AddMinutes(40))));
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);

        var pump = provider.GetRequiredService<IDurableRuntimePumpAdmission>();
        var pass = new DurableRuntimePumpRequest(maximumItems: 4, surfaces: DurableRuntimeSurface.Work);
        var first = await pump.TryRunOnceAsync(pass);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, first.Kind);
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.Calls));
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.LogicalEffects));

        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(suspended.IsSuccess, suspended.Problem?.Problem);
        Assert.Equal(DurableWorkState.Suspended, suspended.Value!.State);

        var reconcile = new DurableWorkReconcileRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("provider-truth"),
            "packed-test-operator",
            "read-only-proof",
            suspended.Value.Revision);
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();
        var reconciled = await operatorClient.ReconcileAsync(reconcile);
        Assert.True(reconciled.IsSuccess, reconciled.Problem?.Problem);
        Assert.Equal(DurableWorkState.Succeeded, reconciled.Value!.State);
        Assert.Equal(1, Volatile.Read(ref ReadOnlyEffectReconciler.Calls));
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.Calls));
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.LogicalEffects));

        var duplicate = await operatorClient.ReconcileAsync(reconcile);
        Assert.True(duplicate.IsSuccess, duplicate.Problem?.Problem);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(1, Volatile.Read(ref ReadOnlyEffectReconciler.Calls));
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.Calls));
        Assert.Equal(1, Volatile.Read(ref AmbiguousEffectExecutor.LogicalEffects));
    }

    private static DurableWorkDefinition<PlannedRetryWork, PlannedRetryResult> CreateDefinition(
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed)
    {
        var retryPolicy = new DurableWorkRetryPolicy(
            maximumAttempts: 5,
            maximumElapsedTime: TimeSpan.FromHours(6),
            initialRetryDelay: TimeSpan.FromMinutes(1),
            maximumRetryDelay: TimeSpan.FromMinutes(1),
            leaseDuration: TimeSpan.FromMinutes(1),
            renewalCadence: TimeSpan.FromSeconds(15),
            maximumLeaseLifetime: TimeSpan.FromMinutes(5),
            backoffAlgorithm: "exponential-v1");
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(
            retryPolicy,
            new DurableAttemptPlan(
                "attempt-plan-v1",
                [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)],
                TimeSpan.FromHours(6)));

        return DurableWork.DefineWithExecutionPolicy<PlannedRetryWork, PlannedRetryResult>(
            "tests.packed.planned-retry",
            "v2",
            PayloadCodecs.Work,
            PayloadCodecs.Result,
            safety,
            policy);
    }

    private sealed class RetryBeforeEffectExecutor : IDurableWorkExitExecutor<PlannedRetryWork, PlannedRetryResult>
    {
        internal static int Calls;
        internal static int LogicalEffects;

        internal static void Reset()
        {
            Interlocked.Exchange(ref Calls, 0);
            Interlocked.Exchange(ref LogicalEffects, 0);
        }

        public ValueTask<DurableWorkExit<PlannedRetryResult>> ExecuteAsync(
            DurableWorkerEnvelope<PlannedRetryWork> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = work.Payload ?? throw new InvalidOperationException("The packed Work payload was not decoded.");
            return Interlocked.Increment(ref Calls) < 5
                ? ValueTask.FromResult(DurableWorkExit<PlannedRetryResult>.RetryBeforeEffect("retry-before-effect"))
                : CompleteLogicalEffect();
        }

        private static ValueTask<DurableWorkExit<PlannedRetryResult>> CompleteLogicalEffect()
        {
            Interlocked.Increment(ref LogicalEffects);
            return ValueTask.FromResult(DurableWorkExit<PlannedRetryResult>.Succeeded(new PlannedRetryResult("done")));
        }
    }

    private sealed class AmbiguousEffectExecutor : IDurableWorkerExecutor<PlannedRetryWork, PlannedRetryResult>
    {
        internal static int Calls;
        internal static int LogicalEffects;

        internal static void Reset()
        {
            Interlocked.Exchange(ref Calls, 0);
            Interlocked.Exchange(ref LogicalEffects, 0);
        }

        public ValueTask<PlannedRetryResult> ExecuteAsync(
            DurableWorkerEnvelope<PlannedRetryWork> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = work.Payload ?? throw new InvalidOperationException("The packed Work payload was not decoded.");
            Interlocked.Increment(ref Calls);
            Interlocked.Increment(ref LogicalEffects);
            throw new InvalidOperationException("The simulated provider response was lost after one logical effect.");
        }
    }

    private sealed class ReadOnlyEffectReconciler : IDurableEffectReconciler<PlannedRetryWork, PlannedRetryResult>
    {
        internal static int Calls;

        internal static void Reset() => Interlocked.Exchange(ref Calls, 0);

        public ValueTask<DurableEffectReconciliation<PlannedRetryResult>> ReconcileAsync(
            DurableWorkerEnvelope<PlannedRetryWork> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = work.Payload ?? throw new InvalidOperationException("The packed reconciliation payload was not decoded.");
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(Volatile.Read(ref AmbiguousEffectExecutor.LogicalEffects) == 1
                ? DurableEffectReconciliation<PlannedRetryResult>.Applied(new PlannedRetryResult("observed-applied"))
                : DurableEffectReconciliation<PlannedRetryResult>.Unknown());
        }
    }

    private sealed record PlannedRetryWork(string Value);
    private sealed record PlannedRetryResult(string Value);

    private static class PayloadCodecs
    {
        internal static readonly IDurablePayloadCodec<PlannedRetryWork> Work =
            new SystemTextJsonDurablePayloadCodec<PlannedRetryWork>(
                "tests.packed.planned-retry.work",
                "v1",
                DurableDataClassification.ApprovedApplication,
                PlannedRetryJsonContext.Default.PlannedRetryWork,
                static _ => true);

        internal static readonly IDurablePayloadCodec<PlannedRetryResult> Result =
            new SystemTextJsonDurablePayloadCodec<PlannedRetryResult>(
                "tests.packed.planned-retry.result",
                "v1",
                DurableDataClassification.ApprovedApplication,
                PlannedRetryJsonContext.Default.PlannedRetryResult,
                static _ => true);
    }

    [JsonSerializable(typeof(PlannedRetryWork))]
    [JsonSerializable(typeof(PlannedRetryResult))]
    private sealed partial class PlannedRetryJsonContext : JsonSerializerContext;
}
