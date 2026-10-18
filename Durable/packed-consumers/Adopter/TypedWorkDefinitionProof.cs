using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Durable.Examples;

internal static class TypedWorkMigrationSnippets
{
    // docs:snippet durable-typed-work-migration-ordinary:start
    internal static void Ordinary(DurableScopeId scope, DurableCommandId command, string key,
        InvoiceWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
    {
        // Before and after are alternative host compositions; never register both in one provider.
        var legacy = new ServiceCollection();
        legacy.AddDurableWork<InvoiceWork, InvoiceResult, TypedWorkDefinitionProof.InvoiceExecutor>(
            "examples.invoice.send", "v1", DurableProviderSafety.Idempotent,
            TypedWorkCodecs.InvoiceWorkCodec, TypedWorkCodecs.InvoiceResultCodec);
        var before = new DurableWorkRequest(scope, command, key, "examples.invoice.send", "v1",
            TypedWorkCodecs.InvoiceWorkCodec.Encode(work), DurableProviderSafety.Idempotent, retryPolicy, dueAtUtc);

        var migrated = new ServiceCollection();
        migrated.AddDurableWork(TypedWorkDefinitionProof.OrdinaryDefinition.ExecutedBy<TypedWorkDefinitionProof.InvoiceExecutor>());
        var after = TypedWorkDefinitionProof.OrdinaryDefinition.CreateRequest(
            scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
        TypedWorkDefinitionProof.AssertRequestParity(before, after);
        using var beforeProvider = legacy.BuildServiceProvider();
        using var afterProvider = migrated.BuildServiceProvider();
        _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.invoice.send", "v1");
        _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.invoice.send", "v1");
    }
    // docs:snippet durable-typed-work-migration-ordinary:end

    // docs:snippet durable-typed-work-migration-reconciled:start
    internal static void Reconciled(DurableScopeId scope, DurableCommandId command, string key,
        LedgerWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
    {
        // Before and after are alternative host compositions; never register both in one provider.
        var legacy = new ServiceCollection();
        legacy.AddDurableWorkWithReconciler<LedgerWork, LedgerResult, TypedWorkDefinitionProof.LedgerExecutor, TypedWorkDefinitionProof.LedgerReconciler>(
            "examples.ledger.reconcile", "v1", TypedWorkCodecs.LedgerWorkCodec, TypedWorkCodecs.LedgerResultCodec);
        var before = new DurableWorkRequest(scope, command, key, "examples.ledger.reconcile", "v1",
            TypedWorkCodecs.LedgerWorkCodec.Encode(work), DurableProviderSafety.ReconcileBeforeRetry, retryPolicy, dueAtUtc);

        var migrated = new ServiceCollection();
        migrated.AddDurableWork(TypedWorkDefinitionProof.ReconciledDefinition.ExecutedBy<TypedWorkDefinitionProof.LedgerExecutor>()
            .ReconciledBy<TypedWorkDefinitionProof.LedgerReconciler>());
        var after = TypedWorkDefinitionProof.ReconciledDefinition.CreateRequest(
            scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
        TypedWorkDefinitionProof.AssertRequestParity(before, after);
        using var beforeProvider = legacy.BuildServiceProvider();
        using var afterProvider = migrated.BuildServiceProvider();
        _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.ledger.reconcile", "v1");
        _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.ledger.reconcile", "v1");
    }
    // docs:snippet durable-typed-work-migration-reconciled:end

    // docs:snippet durable-typed-work-migration-exit:start
    internal static void ExitAware(DurableScopeId scope, DurableCommandId command, string key,
        NotificationWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
    {
        // Before and after are alternative host compositions; never register both in one provider.
        var legacy = new ServiceCollection();
        legacy.AddDurableWorkExit<NotificationWork, NotificationResult, TypedWorkDefinitionProof.NotificationExitExecutor>(
            "examples.notification.send", "v1", TypedWorkCodecs.NotificationWorkCodec, TypedWorkCodecs.NotificationResultCodec);
        var before = new DurableWorkRequest(scope, command, key, "examples.notification.send", "v1",
            TypedWorkCodecs.NotificationWorkCodec.Encode(work), DurableProviderSafety.ProviderKeyed, retryPolicy, dueAtUtc);

        var migrated = new ServiceCollection();
        migrated.AddDurableWork(TypedWorkDefinitionProof.ExitDefinition.ExecutedByExit<TypedWorkDefinitionProof.NotificationExitExecutor>());
        var after = TypedWorkDefinitionProof.ExitDefinition.CreateRequest(
            scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
        TypedWorkDefinitionProof.AssertRequestParity(before, after);
        using var beforeProvider = legacy.BuildServiceProvider();
        using var afterProvider = migrated.BuildServiceProvider();
        _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.notification.send", "v1");
        _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.notification.send", "v1");
    }
    // docs:snippet durable-typed-work-migration-exit:end

}

// docs:snippet durable-typed-work-definition:start
internal static class TypedWorkDefinitionProof
{
    internal static readonly DurableWorkDefinition<InvoiceWork, InvoiceResult> OrdinaryDefinition =
        DurableWork.Define(
            workName: "examples.invoice.send",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.InvoiceWorkCodec,
            resultCodec: TypedWorkCodecs.InvoiceResultCodec,
            providerSafety: DurableProviderSafety.Idempotent,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    internal static readonly DurableWorkDefinition<LedgerWork, LedgerResult> ReconciledDefinition =
        DurableWork.Define(
            workName: "examples.ledger.reconcile",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.LedgerWorkCodec,
            resultCodec: TypedWorkCodecs.LedgerResultCodec,
            providerSafety: DurableProviderSafety.ReconcileBeforeRetry,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    internal static readonly DurableWorkDefinition<NotificationWork, NotificationResult> ExitDefinition =
        DurableWork.Define(
            workName: "examples.notification.send",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.NotificationWorkCodec,
            resultCodec: TypedWorkCodecs.NotificationResultCodec,
            providerSafety: DurableProviderSafety.ProviderKeyed,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    private static readonly IDurablePayloadCodec<FlowContext> FlowContextCodec =
        new SystemTextJsonDurablePayloadCodec<FlowContext>(
            "examples.flow.context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            TypedWorkJsonContext.Default.FlowContext,
            static _ => true);

    private static readonly DurableWorkRetryPolicy ExplicitRetry = new(
        maximumAttempts: 4,
        maximumElapsedTime: TimeSpan.FromHours(1),
        initialRetryDelay: TimeSpan.FromSeconds(2),
        maximumRetryDelay: TimeSpan.FromMinutes(5),
        leaseDuration: TimeSpan.FromMinutes(1),
        renewalCadence: TimeSpan.FromSeconds(15),
        maximumLeaseLifetime: TimeSpan.FromMinutes(5),
        backoffAlgorithm: "exponential-v1");

    // docs:snippet durable-execution-policy-chooser:start
    internal static void VerifyExecutionPolicyChooser()
    {
        var scope = new DurableScopeId("execution-policy-proof-scope");
        var command = new DurableCommandId("execution-policy-proof-command");
        var input = new LedgerWork("entry-1");
        var legacy = ReconciledDefinition.CreateRequest(
            scope, command, "legacy-key", input, retryPolicy: ExplicitRetry);
        if (legacy.ExecutionPolicy.AttemptPlan is not null
            || legacy.ExecutionDeadline is not null
            || legacy.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v1")
        {
            throw new InvalidOperationException("Legacy request construction changed its policy or fingerprint schema.");
        }

        var deadline = new DurableExecutionDeadline(
            new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var deadlineOnly = DurableWorkRequest.CreateWithExecutionPolicy(
            scope,
            command,
            "deadline-only-key",
            OrdinaryDefinition.WorkName,
            "v2",
            OrdinaryDefinition.WorkCodec.Encode(new InvoiceWork("invoice-1002")),
            OrdinaryDefinition.ProviderSafety,
            DurableWorkExecutionPolicy.FromRetryPolicy(ExplicitRetry),
            deadline);
        if (deadlineOnly.ExecutionPolicy.AttemptPlan is not null
            || deadlineOnly.ExecutionDeadline != deadline
            || deadlineOnly.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v2")
        {
            throw new InvalidOperationException("Deadline-only construction must retain legacy backoff with the opt-in fingerprint.");
        }

        var retry = new DurableWorkRetryPolicy(
            maximumAttempts: 5,
            maximumElapsedTime: TimeSpan.FromHours(4),
            initialRetryDelay: TimeSpan.FromMinutes(5),
            maximumRetryDelay: TimeSpan.FromHours(1),
            leaseDuration: TimeSpan.FromMinutes(1),
            renewalCadence: TimeSpan.FromSeconds(15),
            maximumLeaseLifetime: TimeSpan.FromMinutes(10),
            backoffAlgorithm: "exponential-v1");
        var plan = new DurableAttemptPlan(
            "attempt-plan-v1",
            [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20),
             TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)],
            TimeSpan.FromMinutes(240));
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(retry, plan);
        var definition = DurableWork.DefineWithExecutionPolicy<LedgerWork, LedgerResult>(
            "examples.ledger.reconcile", "v2",
            TypedWorkCodecs.LedgerWorkCodec, TypedWorkCodecs.LedgerResultCodec,
            DurableProviderSafety.ReconcileBeforeRetry, policy);
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedBy<LedgerExecutor>()
            .ReconciledBy<LedgerReconciler>());
        var request = definition.CreateRequestWithExecutionPolicy(
            scope, command, "ledger-reconcile-v2", input,
            executionDeadline: deadline);
        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IDurableWorkRegistry>()
            .GetRequired(definition.WorkName, definition.WorkVersion);
        if (!request.ExecutionPolicy.Equals(policy)
            || request.ExecutionDeadline != deadline
            || request.DueAtUtc is not null
            || request.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v2"
            || !registration.DefaultExecutionPolicy.Equals(policy))
        {
            throw new InvalidOperationException("Planned Work must preserve its named policy through request and registration.");
        }

    }
    // docs:snippet durable-execution-policy-chooser:end

    // docs:snippet durable-execution-policy-timing-proof:start
    internal static void VerifyExecutionPolicyTimingProof()
    {
        var acceptedAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var plan = new DurableAttemptPlan(
            "attempt-plan-v1",
            [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20),
             TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)],
            TimeSpan.FromMinutes(240));
        var expectedOffsets = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(180),
        };
        var expectedAttemptNumbers = new[] { 1, 2, 3, 4, 5 };
        var expectedEligibility = new[]
        {
            acceptedAt,
            acceptedAt.AddMinutes(5),
            acceptedAt.AddMinutes(20),
            acceptedAt.AddMinutes(60),
            acceptedAt.AddMinutes(180),
        };
        var exclusiveCutoff = acceptedAt.Add(plan.MaximumCircuitDuration);
        if (!plan.ElapsedOffsets.SequenceEqual(expectedOffsets)
            || plan.MaximumCircuitDuration != TimeSpan.FromMinutes(240)
            || plan.ElapsedOffsets[^1] >= plan.MaximumCircuitDuration)
        {
            throw new InvalidOperationException("The fixed five-slot plan must keep every slot before its exclusive circuit cutoff.");
        }

        for (var slot = 0; slot < expectedOffsets.Length; slot++)
        {
            var eligibility = acceptedAt.Add(plan.ElapsedOffsets[slot]);
            if (eligibility != expectedEligibility[slot]
                || expectedAttemptNumbers[slot] != slot + 1
                || eligibility >= exclusiveCutoff)
            {
                throw new InvalidOperationException("Zero-based offsets must map to one-based attempts at fixed acceptance-relative times.");
            }
        }

        // Schedule arithmetic only: these observations do not simulate database claims or provider I/O.
        var callerCommitAt = acceptedAt.AddMinutes(25);
        var delayedFirstClaimAt = acceptedAt.AddMinutes(30);
        var downtimeRecoveryAt = acceptedAt.AddMinutes(90);
        var nextSlotAfterSafeAttemptOne = expectedEligibility[1];
        var overdueSlotsAfterDowntime = expectedEligibility.Count(time => time < downtimeRecoveryAt);
        if (!(acceptedAt < callerCommitAt && callerCommitAt < delayedFirstClaimAt)
            || nextSlotAfterSafeAttemptOne != acceptedAt.AddMinutes(5)
            || nextSlotAfterSafeAttemptOne >= delayedFirstClaimAt
            || overdueSlotsAfterDowntime != 4
            || expectedEligibility[4] <= downtimeRecoveryAt)
        {
            throw new InvalidOperationException("Late commit and downtime must not rebase or skip fixed slots; only safe sequential retries advance them.");
        }
    }
    // docs:snippet durable-execution-policy-timing-proof:end

    // docs:snippet durable-execution-policy-one-slot:start
    internal static void VerifyOneSlotPolicyValidation()
    {
        static DurableWorkRetryPolicy CreateRetryPolicy(int maximumAttempts) => new(
            maximumAttempts: maximumAttempts,
            maximumElapsedTime: TimeSpan.FromHours(1),
            initialRetryDelay: TimeSpan.FromMinutes(1),
            maximumRetryDelay: TimeSpan.FromMinutes(1),
            leaseDuration: TimeSpan.FromMinutes(1),
            renewalCadence: TimeSpan.FromSeconds(15),
            maximumLeaseLifetime: TimeSpan.FromMinutes(5),
            backoffAlgorithm: "exponential-v1");

        var oneSlotPlan = new DurableAttemptPlan(
            "attempt-plan-v1",
            [TimeSpan.Zero],
            TimeSpan.FromMinutes(30));
        var oneAttemptPolicy = DurableWorkExecutionPolicy.ForAttemptPlan(
            CreateRetryPolicy(maximumAttempts: 1),
            oneSlotPlan);
        var acceptedPlan = oneAttemptPolicy.AttemptPlan;
        if (oneAttemptPolicy.RetryPolicy.MaximumAttempts != 1
            || acceptedPlan is null
            || acceptedPlan.ElapsedOffsets.Count != 1
            || acceptedPlan.ElapsedOffsets[0] != TimeSpan.Zero)
        {
            throw new InvalidOperationException("A one-slot plan permits exactly one execution and no planned retry.");
        }

        var retryCountMismatchRejected = false;
        try
        {
            _ = DurableWorkExecutionPolicy.ForAttemptPlan(
                CreateRetryPolicy(maximumAttempts: 2),
                oneSlotPlan);
        }
        catch (ArgumentException)
        {
            retryCountMismatchRejected = true;
        }

        if (!retryCountMismatchRejected)
        {
            throw new InvalidOperationException("A one-slot plan must reject a retry policy that permits another attempt.");
        }
    }
    // docs:snippet durable-execution-policy-one-slot:end

    internal static void Run()
    {
        VerifyExecutionPolicyChooser();
        VerifyExecutionPolicyTimingProof();
        VerifyOneSlotPolicyValidation();

        var services = new ServiceCollection();
        new AppSurfaceDurableModule().ConfigureServices(
            new StartupContext([], new PassiveHostModule()),
            services);

        var ordinaryBinding = OrdinaryDefinition.ExecutedBy<InvoiceExecutor>();
        var reconciledBinding = ReconciledDefinition.ExecutedBy<LedgerExecutor>()
            .ReconciledBy<LedgerReconciler>();
        var exitBinding = ExitDefinition.ExecutedByExit<NotificationExitExecutor>();

        services.AddDurableWork(ordinaryBinding);
        services.AddDurableWork(reconciledBinding);
        services.AddDurableWork(exitBinding);
        services.AddSingleton<IDurablePayloadCodec>(FlowContextCodec);
        services.AddSingleton<DurableFlowRegistration>(provider =>
        {
            var workRegistration = provider.GetRequiredService<IDurableWorkRegistry>()
                .GetRequired(OrdinaryDefinition.WorkName, OrdinaryDefinition.WorkVersion);
            var callsite = new FlowActivityCallsite<InvoiceWork, InvoiceResult>("send-invoice", 1, 1);
            var flowDefinition = FlowGraphBuilder<FlowContext>
                .Create("examples.invoice-flow", "v1")
                .AddNode("send", new InvoiceActivityNode(callsite))
                .StartAt("send")
                .Build();
            var activityBinding = new DurableFlowActivityBinding<FlowContext, InvoiceWork, InvoiceResult>(
                callsite,
                workRegistration,
                OrdinaryDefinition.WorkCodec,
                OrdinaryDefinition.ResultCodec);
            return new DurableFlowRegistration<FlowContext>(
                flowDefinition,
                FlowContextCodec,
                "typed-work-definition-proof-v1",
                new FlowTransitionEvaluator<FlowContext>(),
                [activityBinding]);
        });

        var scope = new DurableScopeId("typed-proof-scope");
        var command = new DurableCommandId("typed-proof-command");
        var due = new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.FromHours(-4));
        var input = new InvoiceWork("invoice-1001");
        var actual = OrdinaryDefinition.CreateRequest(
            scope,
            command,
            "typed-proof-key",
            input,
            retryPolicy: ExplicitRetry,
            dueAtUtc: due);
        var expected = new DurableWorkRequest(
            scope,
            command,
            "typed-proof-key",
            OrdinaryDefinition.WorkName,
            OrdinaryDefinition.WorkVersion,
            OrdinaryDefinition.WorkCodec.Encode(input),
            OrdinaryDefinition.ProviderSafety,
            ExplicitRetry,
            due);
        AssertRequestParity(expected, actual);
        var defaultRequest = OrdinaryDefinition.CreateRequest(scope, command, "default-key", input);
        AssertRequestParity(new DurableWorkRequest(scope, command, "default-key", OrdinaryDefinition.WorkName,
            OrdinaryDefinition.WorkVersion, TypedWorkCodecs.InvoiceWorkCodec.Encode(input), OrdinaryDefinition.ProviderSafety,
            OrdinaryDefinition.DefaultRetryPolicy), defaultRequest);
        TypedWorkMigrationSnippets.Ordinary(scope, command, "ordinary-migration", input, ExplicitRetry, due);
        TypedWorkMigrationSnippets.Reconciled(scope, command, "reconciled-migration", new LedgerWork("entry-1"), ExplicitRetry, due);
        TypedWorkMigrationSnippets.ExitAware(scope, command, "exit-migration", new NotificationWork("recipient-1"), ExplicitRetry, due);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        var workRegistry = provider.GetRequiredService<IDurableWorkRegistry>();
        var flow = provider.GetRequiredService<IDurableFlowRegistry>().GetRequired("examples.invoice-flow", "v1");
        var registeredWork = workRegistry.GetRequired(OrdinaryDefinition.WorkName, OrdinaryDefinition.WorkVersion);
        if (!ReferenceEquals(flow.ActivityWorkRegistrations.Single(), registeredWork)
            || !ReferenceEquals(registeredWork.WorkCodec, OrdinaryDefinition.WorkCodec)
            || !ReferenceEquals(registeredWork.ResultCodec, OrdinaryDefinition.ResultCodec))
        {
            throw new InvalidOperationException("Flow composition did not retain the exact defined Work registration and codec views.");
        }
        if (provider.GetService<IDurableWorkClient>() is not null
            || provider.GetService<IDurableFlowClient>() is not null
            || provider.GetService<IDurableScheduleClient>() is not null
            || provider.GetServices<IHostedService>().Any())
        {
            throw new InvalidOperationException("Typed Work proof unexpectedly installed a runtime.");
        }

        Console.WriteLine("typed Work contracts registered; request parity verified; no runtime installed");
    }

    internal static void AssertRequestParity(DurableWorkRequest expected, DurableWorkRequest actual)
    {
        if (expected.ScopeId != actual.ScopeId
            || expected.CommandId != actual.CommandId
            || expected.IdempotencyKey != actual.IdempotencyKey
            || expected.WorkName != actual.WorkName
            || expected.WorkVersion != actual.WorkVersion
            || expected.Payload != actual.Payload
            || expected.ProviderSafety != actual.ProviderSafety
            || expected.RetryPolicy != actual.RetryPolicy
            || expected.DueAtUtc != actual.DueAtUtc
            || expected.Fingerprint != actual.Fingerprint)
        {
            throw new InvalidOperationException("Typed Work request does not match equivalent direct construction.");
        }
    }

    private sealed class InvoiceActivityNode(FlowActivityCallsite<InvoiceWork, InvoiceResult> callsite)
        : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Activity(callsite, new InvoiceWork("invoice-from-flow"), context.State));
    }

    internal sealed class InvoiceExecutor : IDurableWorkerExecutor<InvoiceWork, InvoiceResult>
    {
        public ValueTask<InvoiceResult> ExecuteAsync(
            DurableWorkerEnvelope<InvoiceWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new InvoiceResult(work.Payload!.InvoiceId));
    }

    internal sealed class LedgerExecutor : IDurableWorkerExecutor<LedgerWork, LedgerResult>
    {
        public ValueTask<LedgerResult> ExecuteAsync(
            DurableWorkerEnvelope<LedgerWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LedgerResult(work.Payload!.EntryId));
    }

    internal sealed class LedgerReconciler : IDurableEffectReconciler<LedgerWork, LedgerResult>
    {
        public ValueTask<DurableEffectReconciliation<LedgerResult>> ReconcileAsync(
            DurableWorkerEnvelope<LedgerWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableEffectReconciliation<LedgerResult>.NotApplied());
    }

    internal sealed class NotificationExitExecutor : IDurableWorkExitExecutor<NotificationWork, NotificationResult>
    {
        public ValueTask<DurableWorkExit<NotificationResult>> ExecuteAsync(
            DurableWorkerEnvelope<NotificationWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableWorkExit<NotificationResult>.Succeeded(
                new NotificationResult(work.Payload!.Recipient)));
    }

    private sealed class PassiveHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
    }
}

internal sealed record InvoiceWork(string InvoiceId);
internal sealed record InvoiceResult(string InvoiceId);
internal sealed record LedgerWork(string EntryId);
internal sealed record LedgerResult(string EntryId);
internal sealed record NotificationWork(string Recipient);
internal sealed record NotificationResult(string Recipient);
internal sealed record FlowContext(string InvoiceId);

internal static class TypedWorkCodecs
{
    internal static readonly IDurablePayloadCodec<InvoiceWork> InvoiceWorkCodec =
        new SystemTextJsonDurablePayloadCodec<InvoiceWork>("examples.invoice.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.InvoiceWork, static _ => true);
    internal static readonly IDurablePayloadCodec<InvoiceResult> InvoiceResultCodec =
        new SystemTextJsonDurablePayloadCodec<InvoiceResult>("examples.invoice.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.InvoiceResult, static _ => true);
    internal static readonly IDurablePayloadCodec<LedgerWork> LedgerWorkCodec =
        new SystemTextJsonDurablePayloadCodec<LedgerWork>("examples.ledger.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.LedgerWork, static _ => true);
    internal static readonly IDurablePayloadCodec<LedgerResult> LedgerResultCodec =
        new SystemTextJsonDurablePayloadCodec<LedgerResult>("examples.ledger.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.LedgerResult, static _ => true);
    internal static readonly IDurablePayloadCodec<NotificationWork> NotificationWorkCodec =
        new SystemTextJsonDurablePayloadCodec<NotificationWork>("examples.notification.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.NotificationWork, static _ => true);
    internal static readonly IDurablePayloadCodec<NotificationResult> NotificationResultCodec =
        new SystemTextJsonDurablePayloadCodec<NotificationResult>("examples.notification.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.NotificationResult, static _ => true);
}

[JsonSerializable(typeof(InvoiceWork))]
[JsonSerializable(typeof(InvoiceResult))]
[JsonSerializable(typeof(LedgerWork))]
[JsonSerializable(typeof(LedgerResult))]
[JsonSerializable(typeof(NotificationWork))]
[JsonSerializable(typeof(NotificationResult))]
[JsonSerializable(typeof(FlowContext))]
internal sealed partial class TypedWorkJsonContext : JsonSerializerContext;
// docs:snippet durable-typed-work-definition:end
