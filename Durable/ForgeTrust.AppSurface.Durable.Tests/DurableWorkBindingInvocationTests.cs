using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkBindingInvocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_prepare_and_invoke_preserve_typed_payload_result_identity_and_fence(bool useDefinitionBinding)
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("invoke.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("invoke.result");
        var executor = new DurableBindingRegistryTestExecutor();
        DurableWorkRegistration registration;
        IDurablePayloadCodec<string> selectedWorkCodec;
        IDurablePayloadCodec<string> selectedResultCodec;
        ServiceProvider services;
        if (useDefinitionBinding)
        {
            var definition = DurableWork.Define("invoke.work", "v1", workCodec, resultCodec,
                DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
            var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();
            var collection = new ServiceCollection();
            collection.AddDurableWork(binding);
            collection.AddSingleton(executor);
            services = collection.BuildServiceProvider();
            registration = services.GetRequiredService<IDurableWorkRegistry>().GetRequired("invoke.work", "v1");
            selectedWorkCodec = definition.WorkCodec;
            selectedResultCodec = definition.ResultCodec;
        }
        else
        {
            registration = new DurableWorkRegistration<string, string, DurableBindingRegistryTestExecutor>(
                "invoke.work", "v1", DurableProviderSafety.Idempotent, workCodec, resultCodec);
            services = new ServiceCollection().AddSingleton(executor).BuildServiceProvider();
            selectedWorkCodec = workCodec;
            selectedResultCodec = resultCodec;
        }

        var identity = DurableWorkerExecutionIdentity.Create("activity-7", 3, 9, 11, "epoch-2");
        var context = new DurableWorkExecutionContext(new DurableScopeId("scope-7"),
            new DurableWorkId("work-7"), "invoke.work", "v1", selectedWorkCodec.Encode("input"),
            DurableProviderSafety.Idempotent, identity);

        var prepared = registration.Prepare(services, context);
        var encoded = await prepared.InvokeAsync();

        Assert.Equal("result:input", selectedResultCodec.Decode(encoded));
        Assert.Equal("input", executor.Observed!.Payload);
        Assert.Equal(identity, executor.Observed.ExecutionIdentity);
        Assert.Equal("activity-7", executor.Observed.Correlation.InstanceId);
        Assert.Equal("work-7", executor.Observed.Correlation.WorkId);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(1, workCodec.DecodeCalls);
        Assert.Equal(1, resultCodec.EncodeCalls);
    }

    [Theory]
    [InlineData(DurableEffectReconciliationKind.Applied)]
    [InlineData(DurableEffectReconciliationKind.NotApplied)]
    [InlineData(DurableEffectReconciliationKind.Unknown)]
    public async Task Reconciliation_preserves_provider_truth_and_only_encodes_applied_results(
        DurableEffectReconciliationKind kind)
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("reconcile.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("reconcile.result");
        var result = kind == DurableEffectReconciliationKind.Applied
            ? DurableEffectReconciliation<string>.Applied("confirmed")
            : kind == DurableEffectReconciliationKind.NotApplied
                ? DurableEffectReconciliation<string>.NotApplied()
                : DurableEffectReconciliation<string>.Unknown();
        var registration = new DurableWorkRegistration<string, string, DurableBindingRegistryTestExecutor>(
            "reconcile.work", "v1", DurableProviderSafety.ReconcileBeforeRetry, workCodec, resultCodec,
            _ => new DurableBindingRegistryTestReconciler(result));
        using var services = new ServiceCollection().AddSingleton<DurableBindingRegistryTestExecutor>().BuildServiceProvider();
        var context = CreateContext("reconcile.work", DurableProviderSafety.ReconcileBeforeRetry, workCodec);

        var outcome = await registration.ReconcileAsync(services, context);

        Assert.Equal(kind, outcome.Kind);
        if (kind == DurableEffectReconciliationKind.Applied)
        {
            Assert.Equal("confirmed", resultCodec.Decode(outcome.Result!));
            Assert.Equal(1, resultCodec.EncodeCalls);
        }
        else
        {
            Assert.Null(outcome.Result);
            Assert.Equal(0, resultCodec.EncodeCalls);
        }
    }

    [Fact]
    public async Task Definition_reconciled_binding_resolves_the_transient_reconciler_at_the_existing_boundary()
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("reconcile.binding.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("reconcile.binding.result");
        var definition = DurableWork.Define("reconcile.binding", "v1", workCodec, resultCodec,
            DurableProviderSafety.ReconcileBeforeRetry, DurableWorkRetryPolicy.Default);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>()
            .ReconciledBy<DurableBindingRegistryTestReconciler>();
        var services = new ServiceCollection();
        services.AddDurableWork(binding);
        services.AddSingleton(new DurableBindingRegistryTestReconciler(
            DurableEffectReconciliation<string>.Applied("confirmed")));
        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("reconcile.binding", "v1");

        var outcome = await registration.ReconcileAsync(provider,
            CreateContext("reconcile.binding", DurableProviderSafety.ReconcileBeforeRetry, definition.WorkCodec));

        Assert.Equal(DurableEffectReconciliationKind.Applied, outcome.Kind);
        Assert.Equal("confirmed", definition.ResultCodec.Decode(outcome.Result!));
    }

    [Theory]
    [InlineData(DurableWorkExitKind.Succeeded)]
    [InlineData(DurableWorkExitKind.RetryBeforeEffect)]
    [InlineData(DurableWorkExitKind.FailedTerminal)]
    [InlineData(DurableWorkExitKind.AmbiguousExternalOutcome)]
    public async Task Exit_invocation_preserves_each_guarded_exit_and_legacy_boundary(
        DurableWorkExitKind kind)
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("exit.invoke.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("exit.invoke.result");
        var exit = kind switch
        {
            DurableWorkExitKind.Succeeded => DurableWorkExit<string>.Succeeded("done"),
            DurableWorkExitKind.RetryBeforeEffect => DurableWorkExit<string>.RetryBeforeEffect("app.retry"),
            DurableWorkExitKind.FailedTerminal => DurableWorkExit<string>.FailedTerminal("app.failed"),
            _ => DurableWorkExit<string>.AmbiguousExternalOutcome("app.unknown")
        };
        var registration = new DurableWorkExitRegistration<string, string, DurableBindingRegistryTestExitExecutor>(
            "exit.invoke", "v1", workCodec, resultCodec);
        var executor = new DurableBindingRegistryTestExitExecutor(exit);
        using var services = new ServiceCollection().AddSingleton(executor).BuildServiceProvider();
        var context = CreateContext("exit.invoke", DurableProviderSafety.ProviderKeyed, workCodec);

        var prepared = registration.Prepare(services, context);
        var encodedExit = await prepared.InvokeExitAsync();

        Assert.Equal(kind, encodedExit.Kind);
        Assert.Equal(exit.Code, encodedExit.Code);
        if (kind == DurableWorkExitKind.Succeeded)
        {
            Assert.Equal("done", resultCodec.Decode(encodedExit.Result!));
            var legacyResult = await prepared.InvokeAsync();
            Assert.Equal("done", resultCodec.Decode(legacyResult));
        }
        else
        {
            await Assert.ThrowsAsync<DurableWorkExitCompatibilityException>(async () => await prepared.InvokeAsync());
            Assert.Null(encodedExit.Result);
        }

        Assert.Equal("input", executor.Observed!.Payload);
        Assert.Equal(1, workCodec.DecodeCalls);
    }

    [Fact]
    public async Task Definition_exit_binding_uses_typed_exit_invocation_and_legacy_success_compatibility()
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("exit.binding.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("exit.binding.result");
        var definition = DurableWork.Define("exit.binding", "v1", workCodec, resultCodec,
            DurableProviderSafety.ProviderKeyed, DurableWorkRetryPolicy.Default);
        var binding = definition.ExecutedByExit<DurableBindingRegistryTestExitExecutor>();
        var executor = new DurableBindingRegistryTestExitExecutor(DurableWorkExit<string>.RetryBeforeEffect("app.retry"));
        var services = new ServiceCollection();
        services.AddDurableWork(binding);
        services.AddSingleton(executor);
        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("exit.binding", "v1");
        var prepared = registration.Prepare(provider,
            CreateContext("exit.binding", DurableProviderSafety.ProviderKeyed, definition.WorkCodec));

        var exit = await prepared.InvokeExitAsync();

        Assert.Equal(DurableWorkExitKind.RetryBeforeEffect, exit.Kind);
        await Assert.ThrowsAsync<DurableWorkExitCompatibilityException>(async () => await prepared.InvokeAsync());
        Assert.Equal("input", executor.Observed!.Payload);
    }

    [Fact]
    public async Task Null_executor_result_is_rejected_after_preparation()
    {
        var workCodec = new DurableBindingRegistryTestCodec<string>("null.invoke.input");
        var resultCodec = new DurableBindingRegistryTestCodec<string>("null.invoke.result");
        var registration = new DurableWorkRegistration<string, string, NullResultExecutor>(
            "null.invoke", "v1", DurableProviderSafety.Idempotent, workCodec, resultCodec);
        using var services = new ServiceCollection().AddSingleton<NullResultExecutor>().BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await registration.Prepare(services, CreateContext("null.invoke", DurableProviderSafety.Idempotent, workCodec))
                .InvokeAsync());
    }

    private static DurableWorkExecutionContext CreateContext(string name, DurableProviderSafety safety,
        IDurablePayloadCodec<string> codec) => new(new DurableScopeId("invoke.scope"), new DurableWorkId("invoke.work"),
        name, "v1", codec.Encode("input"), safety,
        DurableWorkerExecutionIdentity.Create("invoke.activity", 2, 4, 6, "invoke.epoch"));

    private sealed class NullResultExecutor : IDurableWorkerExecutor<string, string>
    {
        public ValueTask<string> ExecuteAsync(DurableWorkerEnvelope<string> work,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string>(null!);
    }
}
