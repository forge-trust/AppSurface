using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkBindingTests
{
    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ProviderKeyed)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public void Ordinary_binding_accepts_non_reconciled_safety_and_preserves_definition(DurableProviderSafety safety)
    {
        var definition = Define("binding." + safety, safety);

        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();

        Assert.Same(definition, binding.Definition);
    }

    [Fact]
    public void Reconcile_before_retry_requires_a_reconciler_before_registration_and_does_not_change_descriptors()
    {
        var definition = Define("binding.reconcile", DurableProviderSafety.ReconcileBeforeRetry);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();
        var services = new ServiceCollection().AddSingleton<object>();
        var before = services.ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddDurableWork(binding));

        Assert.Contains("requires a reconciler", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, services);
    }

    [Fact]
    public void Reconciled_binding_accepts_only_reconcile_before_retry_and_registers_exact_values()
    {
        var definition = Define("binding.reconciled", DurableProviderSafety.ReconcileBeforeRetry);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>()
            .ReconciledBy<DurableBindingRegistryTestReconciler>();
        var services = new ServiceCollection();

        services.AddDurableWork(binding);
        using var provider = services.BuildServiceProvider();

        Assert.Same(definition, provider.GetRequiredService<DurableWorkDefinition<string, string>>());
        Assert.Same(binding, provider.GetRequiredService<DurableReconciledWorkBinding<string, string,
            DurableBindingRegistryTestExecutor, DurableBindingRegistryTestReconciler>>());
        Assert.NotSame(provider.GetRequiredService<DurableBindingRegistryTestReconciler>(),
            provider.GetRequiredService<DurableBindingRegistryTestReconciler>());
        var registration = provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("binding.reconciled", "v1");
        Assert.True(registration.CanReconcile);
    }

    [Fact]
    public void Reconciled_by_rejects_other_safety_without_mutating_the_binding_or_definition()
    {
        var definition = Define("binding.invalid-reconciler", DurableProviderSafety.ProviderKeyed);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();

        var error = Assert.Throws<InvalidOperationException>(() =>
            binding.ReconciledBy<DurableBindingRegistryTestReconciler>());

        Assert.Contains("requires ReconcileBeforeRetry", error.Message, StringComparison.Ordinal);
        Assert.Same(definition, binding.Definition);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, definition.ProviderSafety);
    }

    [Fact]
    public void Exit_binding_is_provider_keyed_and_registration_uses_exact_definition_values()
    {
        var definition = Define("binding.exit", DurableProviderSafety.ProviderKeyed);
        var binding = definition.ExecutedByExit<DurableBindingRegistryTestExitExecutor>();
        var services = new ServiceCollection();

        services.AddDurableWork(binding);
        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("binding.exit", "v1");

        Assert.Same(definition, binding.Definition);
        Assert.Equal(definition.WorkName, registration.WorkName);
        Assert.Equal(definition.WorkVersion, registration.WorkVersion);
        Assert.Equal(definition.ProviderSafety, registration.ProviderSafety);
        Assert.Same(definition.WorkCodec, registration.WorkCodec);
        Assert.Same(definition.ResultCodec, registration.ResultCodec);
        Assert.False(registration.CanReconcile);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public void Exit_binding_rejects_every_non_provider_keyed_safety(DurableProviderSafety safety)
    {
        var definition = Define("binding.exit.invalid." + safety, safety);

        var error = Assert.Throws<InvalidOperationException>(() =>
            definition.ExecutedByExit<DurableBindingRegistryTestExitExecutor>());

        Assert.Contains("requires ProviderKeyed", error.Message, StringComparison.Ordinal);
        Assert.Equal(safety, definition.ProviderSafety);
    }

    [Fact]
    public void Null_services_and_bindings_fail_before_descriptor_changes()
    {
        var definition = Define("binding.null", DurableProviderSafety.Idempotent);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();

        Assert.Throws<ArgumentNullException>(() => DurableServiceCollectionExtensions.AddDurableWork<string, string,
            DurableBindingRegistryTestExecutor>(null!, binding));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddDurableWork(
            (DurableWorkBinding<string, string, DurableBindingRegistryTestExecutor>)null!));
    }

    private static DurableWorkDefinition<string, string> Define(string name, DurableProviderSafety safety) =>
        DurableWork.Define(name, "v1", new DurableBindingRegistryTestCodec<string>(name + ".input"),
            new DurableBindingRegistryTestCodec<string>(name + ".result"), safety, DurableWorkRetryPolicy.Default);
}
