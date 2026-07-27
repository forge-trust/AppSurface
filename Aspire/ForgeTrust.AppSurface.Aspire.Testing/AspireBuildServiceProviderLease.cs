using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Captures the root service provider created while Aspire resolves its host so a failed build can release it.
/// </summary>
/// <remarks>
/// Aspire 13.4.4 registers <see cref="IHost"/> as a singleton factory. The factory receives the root provider before
/// host construction can fail, while <c>DistributedApplicationBuilder.Build()</c> does not otherwise expose that
/// partial provider. This lease decorates the verified unkeyed registration shape when it is present and ignores
/// unrelated keyed <see cref="IHost"/> registrations. If a consumer-selected Aspire version changes that shape,
/// installation warns and yields to Aspire without the additional failed-build cleanup. A successful build transfers
/// provider ownership to the returned distributed application by calling
/// <see cref="Release"/>. Non-process-fatal failures dispose the lease immediately; process-fatal failures retain it
/// for best-effort cleanup when the testing builder is later disposed.
/// </remarks>
internal sealed class AspireBuildServiceProviderLease : IAsyncDisposable
{
    private IServiceProvider? _serviceProvider;

    private AspireBuildServiceProviderLease()
    {
    }

    /// <summary>
    /// Attempts to decorate Aspire's verified host factory and returns the lease that will capture its root provider.
    /// </summary>
    /// <param name="services">The mutable Aspire builder service collection immediately before build.</param>
    /// <param name="warningSink">Receives a compatibility warning when the verified registration shape is absent.</param>
    /// <returns>
    /// A lease that owns a captured provider until released after a successful build, or <see langword="null"/> when
    /// the consumer-selected Aspire version does not expose the verified registration shape.
    /// </returns>
    internal static AspireBuildServiceProviderLease? TryInstall(
        IServiceCollection services,
        Action<string>? warningSink = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.LastOrDefault(candidate =>
            candidate.ServiceType == typeof(IHost) && !candidate.IsKeyedService);
        if (descriptor is null ||
            descriptor.Lifetime != ServiceLifetime.Singleton ||
            descriptor.ImplementationFactory is null)
        {
            AspireTestingDiagnostics.TryWrite(
                warningSink,
                "The Aspire IHost service registration does not have the singleton factory shape verified with " +
                "Aspire 13.4.4. Build will continue without AppSurface's partial-provider cleanup; verify failed-build " +
                "cleanup before relying on this consumer-selected Aspire version.");
            return null;
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
