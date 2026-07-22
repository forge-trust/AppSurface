using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Captures the root service provider created while Aspire resolves its host so a failed build can release it.
/// </summary>
/// <remarks>
/// Aspire 13.4.4 registers <see cref="IHost"/> as a singleton factory. The factory receives the root provider before
/// host construction can fail, while <c>DistributedApplicationBuilder.Build()</c> does not otherwise expose that
/// partial provider. This lease decorates that exact pinned registration shape and deliberately fails closed when the
/// shape changes. A successful build transfers provider ownership to the returned distributed application by calling
/// <see cref="Release"/>; only a failed build calls <see cref="DisposeAsync"/>.
/// </remarks>
internal sealed class AspireBuildServiceProviderLease : IAsyncDisposable
{
    private IServiceProvider? _serviceProvider;

    private AspireBuildServiceProviderLease()
    {
    }

    /// <summary>
    /// Decorates Aspire's pinned host factory and returns the lease that will capture its root provider.
    /// </summary>
    /// <param name="services">The mutable Aspire builder service collection immediately before build.</param>
    /// <returns>A lease that owns a captured provider until released after a successful build.</returns>
    /// <exception cref="InvalidOperationException">
    /// Aspire's final <see cref="IHost"/> registration is missing, keyed, not singleton, or not factory-backed.
    /// </exception>
    internal static AspireBuildServiceProviderLease Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.LastOrDefault(candidate => candidate.ServiceType == typeof(IHost));
        if (descriptor is null ||
            descriptor.IsKeyedService ||
            descriptor.Lifetime != ServiceLifetime.Singleton ||
            descriptor.ImplementationFactory is null)
        {
            throw new InvalidOperationException(
                "The pinned Aspire IHost service registration no longer has the expected singleton factory shape. " +
                "Update ForgeTrust.AppSurface.Aspire.Testing for this Aspire version before building.");
        }

        var originalFactory = descriptor.ImplementationFactory;
        var lease = new AspireBuildServiceProviderLease();
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Singleton(typeof(IHost), serviceProvider =>
        {
            lease.Capture(serviceProvider);
            return originalFactory(serviceProvider);
        }));

        return lease;
    }

    /// <summary>
    /// Transfers ownership of any captured provider to the successfully built distributed application.
    /// </summary>
    internal void Release() => Interlocked.Exchange(ref _serviceProvider, null);

    /// <summary>
    /// Releases a provider captured before host construction failed.
    /// </summary>
    /// <returns>A value task that completes after asynchronous or synchronous provider cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        var serviceProvider = Interlocked.Exchange(ref _serviceProvider, null);
        if (serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Capture(IServiceProvider serviceProvider)
    {
        if (Interlocked.CompareExchange(ref _serviceProvider, serviceProvider, null) is not null)
        {
            throw new InvalidOperationException("Aspire resolved its singleton IHost registration more than once.");
        }
    }
}
