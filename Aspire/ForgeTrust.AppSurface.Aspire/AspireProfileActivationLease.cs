using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Owns the unstarted AppSurface host used to activate one typed Aspire profile.
/// </summary>
/// <remarks>
/// Construction transfers ownership of the supplied host to the lease. Callers may use <see cref="Profile"/> and
/// <see cref="Services"/> only while the lease is active; profile dependencies may come from the owned service
/// provider. Disposal is idempotent, chooses asynchronous host disposal when available, and invalidates service access.
/// Callers must not dispose the owned host separately or use the profile after lease disposal begins.
/// </remarks>
/// <typeparam name="TProfile">The activated profile type.</typeparam>
internal sealed class AspireProfileActivationLease<TProfile> : IDisposable, IAsyncDisposable
    where TProfile : AspireProfile
{
    private readonly object _disposeLock = new();
    private IHost? _host;
    private Task? _disposeTask;
    private readonly IAsyncDisposable? _ownership;

    /// <summary>
    /// Initializes a lease and transfers ownership of the activation host to it.
    /// </summary>
    /// <param name="host">The unstarted AppSurface host that owns the profile's service provider.</param>
    /// <param name="profile">The profile resolved from <paramref name="host"/>.</param>
    /// <param name="ownership">Optional configured-activation ownership, including detached configuration trees.</param>
    internal AspireProfileActivationLease(IHost host, TProfile profile, IAsyncDisposable? ownership = null)
    {
        _host = host;
        _ownership = ownership;
        Profile = profile;
    }

    /// <summary>
    /// Gets the activated profile while the lease and its constructor-injected services remain valid.
    /// </summary>
    internal TProfile Profile { get; }

    /// <summary>
    /// Gets the owned activation host's service provider before disposal begins.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease is disposing or has been disposed.</exception>
    internal IServiceProvider Services
    {
        get
        {
            var host = Volatile.Read(ref _host);
            if (host is null)
            {
                throw new ObjectDisposedException(nameof(AspireProfileActivationLease<TProfile>));
            }

            return host.Services;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GetOrStartDisposeTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return new ValueTask(GetOrStartDisposeTask());
    }

    /// <summary>
    /// Disposes a host asynchronously when its concrete implementation supports asynchronous cleanup.
    /// </summary>
    /// <param name="host">The host to dispose.</param>
    /// <returns>A task that completes after host cleanup.</returns>
    internal static Task DisposeHostAsync(IHost host)
    {
        return DisposeHostCoreAsync(host);
    }

    private Task GetOrStartDisposeTask()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= DisposeOwnedHostAsync();
        }
    }

    private async Task DisposeOwnedHostAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (_ownership is not null)
        {
            await _ownership.DisposeAsync().ConfigureAwait(false);
        }
        else if (host is not null)
        {
            await DisposeHostCoreAsync(host).ConfigureAwait(false);
        }
    }

    private static async Task DisposeHostCoreAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            host.Dispose();
        }
    }
}

/// <summary>
/// Activates Aspire profiles through the same AppSurface host composition used at runtime.
/// </summary>
internal static class AspireProfileActivator
{
    /// <summary>
    /// Builds an unstarted AppSurface host for <typeparamref name="TAppHost"/> and resolves one typed Aspire profile.
    /// </summary>
    /// <typeparam name="TAppHost">
    /// The public generated AppHost marker type whose assembly supplies activation identity.
    /// </typeparam>
    /// <typeparam name="TModule">The public AppSurface module used to compose the activation host.</typeparam>
    /// <typeparam name="TProfile">The public concrete Aspire profile resolved from the activation host.</typeparam>
    /// <param name="cancellationToken">A token observed before host creation and around profile resolution.</param>
    /// <returns>
    /// A lease that owns the unstarted host, profile, and service provider. The caller must dispose the returned lease.
    /// </returns>
    /// <remarks>
    /// Activation uses empty command-line arguments and pins the entry-point assembly to <typeparamref name="TAppHost"/>.
    /// The host is never started. If activation or cancellation prevents ownership from being returned, the method
    /// disposes the host before propagating the primary failure.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Cancellation is requested before activation completes.</exception>
    internal static Task<AspireProfileActivationLease<TProfile>> ActivateAsync<TAppHost, TModule, TProfile>(
        CancellationToken cancellationToken)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile =>
        ActivateCoreAsync<TAppHost, TModule, TProfile>(null, null, false, cancellationToken);

    /// <summary>
    /// Activates a profile after final per-call configuration and services callbacks with early ownership observation.
    /// </summary>
    /// <typeparam name="TAppHost">The marker whose assembly pins activation identity.</typeparam>
    /// <typeparam name="TModule">The module composed through ordinary AppSurface startup.</typeparam>
    /// <typeparam name="TProfile">The profile resolved after both callbacks.</typeparam>
    /// <param name="configureConfiguration">The optional final application-configuration callback.</param>
    /// <param name="configureServices">The optional final service-collection callback.</param>
    /// <param name="cancellationToken">A token checked around callbacks and profile construction.</param>
    /// <returns>A lease owning the unstarted host and detached or partially constructed configuration.</returns>
    /// <remarks>
    /// The selected provider factory is retained, including later container precedence. Callback exceptions are
    /// preserved for the Testing factory's phase diagnostics. Cleanup and logging failures cannot replace them.
    /// </remarks>
    internal static Task<AspireProfileActivationLease<TProfile>> ActivateConfiguredAsync<TAppHost, TModule, TProfile>(
        Action<IConfigurationBuilder>? configureConfiguration,
        Action<IServiceCollection>? configureServices,
        CancellationToken cancellationToken)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile =>
        ActivateCoreAsync<TAppHost, TModule, TProfile>(configureConfiguration, configureServices, true, cancellationToken);

    private static async Task<AspireProfileActivationLease<TProfile>> ActivateCoreAsync<TAppHost, TModule, TProfile>(
        Action<IConfigurationBuilder>? configureConfiguration,
        Action<IServiceCollection>? configureServices,
        bool configured,
        CancellationToken cancellationToken)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownership = configured ? new AspireProfileActivationHostBuilder() : null;
        IHost? host = null;
        try
        {
            var context = new StartupContext([], new TModule())
            {
                OverrideEntryPointAssembly = typeof(TAppHost).Assembly,
                HostBuilderFactory = ownership is null ? null : ownership.ConfigureDefaults
            };
            var startup = (IAppSurfaceStartup)new AspireAppStartup<TModule>();
            var builder = startup.CreateHostBuilder(context);
            if (configured)
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    configureConfiguration?.Invoke(configuration);
                    cancellationToken.ThrowIfCancellationRequested();
                });
                builder.ConfigureServices((_, services) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    configureServices?.Invoke(services);
                    cancellationToken.ThrowIfCancellationRequested();
                });
            }

            host = builder.Build();
            cancellationToken.ThrowIfCancellationRequested();
            var profile = host.Services.GetRequiredService<TProfile>();
            cancellationToken.ThrowIfCancellationRequested();
            return new AspireProfileActivationLease<TProfile>(host, profile, ownership);
        }
        catch (Exception primaryException)
        {
            ILogger? cleanupLogger = null;
            try
            {
                cleanupLogger = host?.Services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(AspireProfileActivator));
            }
            catch (Exception)
            {
                // Logging is best-effort and must not interfere with cleanup.
            }

            try
            {
                if (ownership is not null)
                {
                    await ownership.DisposeAsync().ConfigureAwait(false);
                }
                else if (host is not null)
                {
                    await AspireProfileActivationLease<TProfile>.DisposeHostAsync(host).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupException)
            {
                try
                {
                    cleanupLogger?.LogWarning(
                        cleanupException,
                        "Aspire profile host cleanup failed while preserving {PrimaryExceptionType}.",
                        primaryException.GetType().FullName);
                }
                catch (Exception)
                {
                    // Cleanup diagnostics never replace the primary activation or cancellation failure.
                }
            }

            throw;
        }
    }
}
