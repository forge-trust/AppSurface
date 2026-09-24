using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Binds an ordinary executor to a definition without resolving services or executing code.</summary>
/// <typeparam name="TWork">Work input type.</typeparam>
/// <typeparam name="TResult">Successful result type.</typeparam>
/// <typeparam name="TExecutor">Transient ordinary executor implementation.</typeparam>
public sealed class DurableWorkBinding<TWork, TResult, TExecutor>
    where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
{
    /// <summary>Retains the exact immutable definition supplied by its binding method.</summary>
    internal DurableWorkBinding(DurableWorkDefinition<TWork, TResult> definition)
    {
        Definition = definition;
    }

    /// <summary>Gets the exact definition owning this binding's facts.</summary>
    public DurableWorkDefinition<TWork, TResult> Definition { get; }

    /// <summary>Completes a reconcile-before-retry binding with its side-effect-free reconciler.</summary>
    /// <typeparam name="TReconciler">Transient reconciler for the exact Work/result types.</typeparam>
    /// <returns>A complete immutable binding.</returns>
    /// <exception cref="InvalidOperationException">The definition does not declare ReconcileBeforeRetry safety.</exception>
    public DurableReconciledWorkBinding<TWork, TResult, TExecutor, TReconciler> ReconciledBy<TReconciler>()
        where TReconciler : class, IDurableEffectReconciler<TWork, TResult>
    {
        if (Definition.ProviderSafety != DurableProviderSafety.ReconcileBeforeRetry)
        {
            throw new InvalidOperationException("A reconciler requires ReconcileBeforeRetry safety. Define that safety before calling ReconciledBy.");
        }

        return new(Definition);
    }
}

/// <summary>Complete reconcile-before-retry binding; no repeated contract facts or mutable options.</summary>
/// <typeparam name="TWork">Work input type.</typeparam>
/// <typeparam name="TResult">Successful result type.</typeparam>
/// <typeparam name="TExecutor">Transient ordinary executor.</typeparam>
/// <typeparam name="TReconciler">Transient side-effect-free reconciler.</typeparam>
public sealed class DurableReconciledWorkBinding<TWork, TResult, TExecutor, TReconciler>
    where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
    where TReconciler : class, IDurableEffectReconciler<TWork, TResult>
{
    /// <summary>Retains a definition whose safety was validated by the ordinary binding.</summary>
    internal DurableReconciledWorkBinding(DurableWorkDefinition<TWork, TResult> definition)
    {
        Definition = definition;
    }

    /// <summary>Gets the exact definition owning this binding's facts.</summary>
    public DurableWorkDefinition<TWork, TResult> Definition { get; }
}

/// <summary>Complete ProviderKeyed exit binding; reconciliation is deliberately unavailable.</summary>
/// <typeparam name="TWork">Work input type.</typeparam>
/// <typeparam name="TResult">Successful result type.</typeparam>
/// <typeparam name="TExecutor">Transient exit-aware executor.</typeparam>
public sealed class DurableExitWorkBinding<TWork, TResult, TExecutor>
    where TExecutor : class, IDurableWorkExitExecutor<TWork, TResult>
{
    /// <summary>Retains a definition whose ProviderKeyed safety was validated by its binding method.</summary>
    internal DurableExitWorkBinding(DurableWorkDefinition<TWork, TResult> definition)
    {
        Definition = definition;
    }

    /// <summary>Gets the exact definition owning this binding's facts.</summary>
    public DurableWorkDefinition<TWork, TResult> Definition { get; }
}

public static partial class DurableServiceCollectionExtensions
{
    /// <summary>Registers exact definition/binding singleton values and a transient ordinary executor.</summary>
    /// <typeparam name="TWork">Work input type.</typeparam>
    /// <typeparam name="TResult">Successful result type.</typeparam>
    /// <typeparam name="TExecutor">Ordinary executor.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="binding">Complete ordinary binding.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">Services or binding is null.</exception>
    /// <exception cref="InvalidOperationException">ReconcileBeforeRetry binding lacks its reconciler.</exception>
    /// <remarks>Validation precedes descriptor changes. Duplicate Work identities fail when default registries resolve.</remarks>
    public static IServiceCollection AddDurableWork<TWork, TResult, TExecutor>(this IServiceCollection services,
        DurableWorkBinding<TWork, TResult, TExecutor> binding)
        where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Definition.ProviderSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            throw new InvalidOperationException("ReconcileBeforeRetry Work requires a reconciler. Call ReconciledBy before AddDurableWork.");
        }

        var registration = new DurableWorkRegistration<TWork, TResult, TExecutor>(binding.Definition.Snapshot, true);
        services.AddSingleton(binding.Definition);
        services.AddSingleton(binding);
        return AddWorkRegistration<TExecutor>(services, registration, registration.WorkCodec, registration.ResultCodec);
    }

    /// <summary>Registers exact contract values and transient executor/reconciler implementations.</summary>
    /// <typeparam name="TWork">Work input type.</typeparam>
    /// <typeparam name="TResult">Successful result type.</typeparam>
    /// <typeparam name="TExecutor">Ordinary executor.</typeparam>
    /// <typeparam name="TReconciler">Side-effect-free reconciler.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="binding">Complete reconcile-before-retry binding.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">Services or binding is null.</exception>
    public static IServiceCollection AddDurableWork<TWork, TResult, TExecutor, TReconciler>(this IServiceCollection services,
        DurableReconciledWorkBinding<TWork, TResult, TExecutor, TReconciler> binding)
        where TExecutor : class, IDurableWorkerExecutor<TWork, TResult>
        where TReconciler : class, IDurableEffectReconciler<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binding);
        var registration = new DurableWorkRegistration<TWork, TResult, TExecutor>(binding.Definition.Snapshot, true,
            static provider => provider.GetRequiredService<TReconciler>());
        services.AddSingleton(binding.Definition);
        services.AddSingleton(binding);
        services.AddTransient<TReconciler>();
        return AddWorkRegistration<TExecutor>(services, registration, registration.WorkCodec, registration.ResultCodec);
    }

    /// <summary>Registers exact contract values and a transient ProviderKeyed exit-aware executor.</summary>
    /// <typeparam name="TWork">Work input type.</typeparam>
    /// <typeparam name="TResult">Successful result type.</typeparam>
    /// <typeparam name="TExecutor">Exit-aware executor.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="binding">Complete exit binding.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">Services or binding is null.</exception>
    /// <remarks>Deploy exit-capable workers before using new exit-aware Work versions; historical executor semantics remain immutable.</remarks>
    public static IServiceCollection AddDurableWork<TWork, TResult, TExecutor>(this IServiceCollection services,
        DurableExitWorkBinding<TWork, TResult, TExecutor> binding)
        where TExecutor : class, IDurableWorkExitExecutor<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binding);
        var registration = new DurableWorkExitRegistration<TWork, TResult, TExecutor>(binding.Definition.Snapshot, true);
        services.AddSingleton(binding.Definition);
        services.AddSingleton(binding);
        return AddWorkRegistration<TExecutor>(services, registration, registration.WorkCodec, registration.ResultCodec);
    }
}
