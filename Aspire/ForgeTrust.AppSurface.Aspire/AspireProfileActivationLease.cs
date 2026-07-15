using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Owns the unstarted AppSurface host used to activate one typed Aspire profile.
/// </summary>
/// <typeparam name="TProfile">The activated profile type.</typeparam>
internal sealed class AspireProfileActivationLease<TProfile> : IDisposable, IAsyncDisposable
    where TProfile : AspireProfile
{
    private readonly object _disposeLock = new();
    private IHost? _host;
    private Task? _disposeTask;

    internal AspireProfileActivationLease(IHost host, TProfile profile)
    {
        _host = host;
        Profile = profile;
    }

    internal TProfile Profile { get; }

    internal IServiceProvider Services =>
        _host?.Services ?? throw new ObjectDisposedException(nameof(AspireProfileActivationLease<TProfile>));

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
        var host = _host;
        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host?.Dispose();
            }
        }
        finally
        {
            _host = null;
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
    internal static async Task<AspireProfileActivationLease<TProfile>> ActivateAsync<TAppHost, TModule, TProfile>(
        CancellationToken cancellationToken)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile
    {
        cancellationToken.ThrowIfCancellationRequested();

        var context = new StartupContext([], new TModule())
        {
            OverrideEntryPointAssembly = typeof(TAppHost).Assembly
        };
        var startup = (IAppSurfaceStartup)new AspireAppStartup<TModule>();
        var host = startup.CreateHostBuilder(context).Build();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = host.Services.GetRequiredService<TProfile>();
            cancellationToken.ThrowIfCancellationRequested();
            return new AspireProfileActivationLease<TProfile>(host, profile);
        }
        catch (Exception primaryException) when (!AspireExceptionUtilities.IsProcessFatal(primaryException))
        {
            ILogger? cleanupLogger = null;
            try
            {
                cleanupLogger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(AspireProfileActivator));
            }
            catch (Exception loggingException) when (!AspireExceptionUtilities.IsProcessFatal(loggingException))
            {
                // Logging is best-effort and must not interfere with cleanup.
            }

            try
            {
                await AspireProfileActivationLease<TProfile>.DisposeHostAsync(host).ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
            {
                try
                {
                    cleanupLogger?.LogWarning(
                        cleanupException,
                        "Aspire profile host cleanup failed while preserving {PrimaryExceptionType}.",
                        primaryException.GetType().FullName);
                }
                catch (Exception loggingException) when (!AspireExceptionUtilities.IsProcessFatal(loggingException))
                {
                    // Cleanup diagnostics never replace the primary activation or cancellation failure.
                }
            }

            throw;
        }
    }
}
