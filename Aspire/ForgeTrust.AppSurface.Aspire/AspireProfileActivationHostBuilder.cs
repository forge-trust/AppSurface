using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Observes resources acquired by one configured profile activation before an unstarted host is returned.
/// </summary>
/// <remarks>
/// Install this forwarding builder before Generic Host defaults and module callbacks. It preserves the selected
/// service-provider object and container type. Its singleton IHost factory receives an idempotent
/// host lease to prevent DI-driven disposal re-entry without wrapping the selected service provider. Configuration sources are decorated only after all configuration
/// callbacks, so callbacks retain their original source collection. Providers and reload registrations returned
/// during framework construction receive shared, idempotent disposal leases: nested roots, failed root construction,
/// detached host configuration and secondary disposal failures cannot cause duplicate leaf disposal. A custom source
/// or service-provider factory owns allocations it does not return; roots explicitly built inside user callbacks
/// remain caller-owned. After Build returns, host-owned files and services are disposed through the host, while the
/// guard retains detached configuration ownership. No hosted service is started by this builder.
/// </remarks>
internal sealed class AspireProfileActivationHostBuilder : IHostBuilder, IAsyncDisposable
{
    private readonly HostBuilder _inner = new();
    private readonly Dictionary<IConfigurationProvider, OwnedProvider> _providers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<OwnedRegistration> _registrations = [];
    private readonly object _ownershipLock = new();
    private HostBuilderContext? _context;
    private IConfiguration? _hostConfiguration;
    private IConfiguration? _applicationConfiguration;
    private IFileProvider? _defaultFileProvider;
    private IServiceProvider? _serviceProvider;
    private IHost? _host;
    private TaskCompletionSource? _disposeCompletion;
    private readonly AsyncLocal<bool> _insideDisposal = new();
    private bool _disposing;

    /// <summary>
    /// Registers ownership observation before defaults or modules can replace the public host context resources.
    /// </summary>
    internal AspireProfileActivationHostBuilder()
    {
        _inner.ConfigureAppConfiguration((context, _) =>
        {
            _context = context;
            _hostConfiguration = context.Configuration;
            _defaultFileProvider = context.HostingEnvironment.ContentRootFileProvider;
        });
        _inner.ConfigureServices((context, _) => _applicationConfiguration = context.Configuration);
    }

    /// <summary>
    /// Applies the same Generic Host defaults as Host.CreateDefaultBuilder through the observing factory seam.
    /// </summary>
    /// <param name="args">The normalized activation arguments.</param>
    /// <returns>This builder, with default provider validation options preserved.</returns>
    internal IHostBuilder ConfigureDefaults(string[] args) => HostingHostBuilderExtensions.ConfigureDefaults(this, args);

    /// <inheritdoc />
    public IDictionary<object, object> Properties => _inner.Properties;

    /// <inheritdoc />
    public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
    {
        _inner.ConfigureHostConfiguration(configureDelegate);
        return this;
    }

    /// <inheritdoc />
    public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
    {
        _inner.ConfigureAppConfiguration(configureDelegate);
        return this;
    }

    /// <inheritdoc />
    public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
    {
        _inner.ConfigureServices(configureDelegate);
        return this;
    }

    /// <inheritdoc />
    public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
    {
        _inner.ConfigureContainer(configureDelegate);
        return this;
    }

    /// <inheritdoc />
    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        where TContainerBuilder : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner.UseServiceProviderFactory(new ObservedFactory<TContainerBuilder>(factory, ObserveHostFactory, ObserveServiceProvider));
        return this;
    }

    /// <inheritdoc />
    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
        where TContainerBuilder : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner.UseServiceProviderFactory(context =>
        {
            var selected = factory(context);
            return selected is null ? null! : new ObservedFactory<TContainerBuilder>(selected, ObserveHostFactory, ObserveServiceProvider);
        });
        return this;
    }

    /// <inheritdoc />
    public IHost Build()
    {
        _inner.ConfigureHostConfiguration(ObserveSources);
        _inner.ConfigureAppConfiguration((_, configuration) => ObserveSources(configuration));
        _host = _inner.Build();
        return _host;
    }

    /// <summary>
    /// Joins idempotent asynchronous cleanup, preferring asynchronous host/provider/file disposal when supported.
    /// </summary>
    /// <returns>A value task that completes after every acquired ownership lease has been attempted.</returns>
    /// <remarks>
    /// A shared completion is published before cleanup starts. Calls from that cleanup flow return immediately;
    /// independent callers join its completion and observe the same first error.
    /// Cleanup continues after secondary errors and rethrows the first error. The activation factory suppresses these
    /// errors only when it already has a primary creation failure. Once a host exists, file/provider cleanup follows
    /// the host's disposal policy; configuration leases additionally drain detached and partially constructed trees.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_insideDisposal.Value)
        {
            return ValueTask.CompletedTask;
        }

        lock (_ownershipLock)
        {
            if (_disposeCompletion is null)
            {
                _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = CompleteOwnedDisposalAsync(_disposeCompletion);
            }

            return new ValueTask(_disposeCompletion.Task);
        }
    }

    private async Task CompleteOwnedDisposalAsync(TaskCompletionSource completion)
    {
        _insideDisposal.Value = true;
        try
        {
            await DisposeOwnedAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            _insideDisposal.Value = false;
        }
    }

    private static void ObserveHostFactory(IServiceCollection services)
    {
        // Hosting resolves IHost from the provider, which also owns factory-created singletons.
        // Guard the IHost factory so container disposal cannot recursively dispose the host's
        // files and the same raw provider again. Preserve other registration shapes verbatim.
        var descriptor = services.LastOrDefault(service => !service.IsKeyedService && service.ServiceType == typeof(IHost));
        if (descriptor is { Lifetime: ServiceLifetime.Singleton, ImplementationFactory: { } factory })
        {
            var index = services.IndexOf(descriptor);
            services[index] = ServiceDescriptor.Singleton(typeof(IHost), provider =>
            {
                var result = factory(provider);
                return result is IHost host && host is not OwnedHost ? new OwnedHost(host) : result;
            });
        }
    }

    private void ObserveServiceProvider(IServiceProvider provider) => _serviceProvider = provider;

    private void ObserveSources(IConfigurationBuilder builder)
    {
        for (var index = 0; index < builder.Sources.Count; index++)
        {
            builder.Sources[index] = new ObservedSource(builder.Sources[index], ObserveProvider);
        }
    }

    private IConfigurationProvider ObserveProvider(IConfigurationProvider provider)
    {
        if (provider is OwnedProvider owned && ReferenceEquals(owned.Owner, this))
        {
            return owned;
        }

        if (!_providers.TryGetValue(provider, out var lease))
        {
            lease = new OwnedProvider(provider, this);
            _providers.Add(provider, lease);
        }

        return lease;
    }

    private void ObserveRegistration(OwnedRegistration registration)
    {
        bool disposeImmediately;
        lock (_ownershipLock)
        {
            disposeImmediately = _disposing;
            if (!disposeImmediately)
            {
                _registrations.Add(registration);
            }
        }

        if (disposeImmediately)
        {
            registration.Dispose();
        }
    }

    private void ForgetRegistration(OwnedRegistration registration)
    {
        lock (_ownershipLock)
        {
            _registrations.Remove(registration);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        OwnedRegistration[] registrations;
        lock (_ownershipLock)
        {
            _disposing = true;
            registrations = _registrations.ToArray();
        }

        Exception? firstError = null;
        async Task AttemptAsync(object? resource)
        {
            try
            {
                if (resource is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (resource is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            catch (Exception error)
            {
                firstError ??= error;
            }
        }

        if (_host is not null)
        {
            await AttemptAsync(_host).ConfigureAwait(false);
        }
        else
        {
            await AttemptAsync(_serviceProvider).ConfigureAwait(false);
            var currentFileProvider = _context?.HostingEnvironment.ContentRootFileProvider;
            if (!ReferenceEquals(currentFileProvider, _defaultFileProvider))
            {
                await AttemptAsync(currentFileProvider).ConfigureAwait(false);
            }

            await AttemptAsync(_defaultFileProvider).ConfigureAwait(false);
        }

        await AttemptAsync(_applicationConfiguration).ConfigureAwait(false);
        await AttemptAsync(_hostConfiguration).ConfigureAwait(false);
        foreach (var provider in _providers.Values.Reverse())
        {
            await AttemptAsync(provider).ConfigureAwait(false);
        }

        foreach (var registration in registrations.Reverse())
        {
            await AttemptAsync(registration).ConfigureAwait(false);
        }

        if (firstError is not null)
        {
            ExceptionDispatchInfo.Capture(firstError).Throw();
        }
    }

    private sealed class ObservedFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> inner, Action<IServiceCollection> observeHostFactory, Action<IServiceProvider> observe)
        : IServiceProviderFactory<TContainerBuilder> where TContainerBuilder : notnull
    {
        public TContainerBuilder CreateBuilder(IServiceCollection services)
        {
            observeHostFactory(services);
            return inner.CreateBuilder(services);
        }
        public IServiceProvider CreateServiceProvider(TContainerBuilder containerBuilder)
        {
            var provider = inner.CreateServiceProvider(containerBuilder);
            observe(provider);
            return provider;
        }
    }

    private sealed class OwnedHost(IHost inner) : IHost, IAsyncDisposable
    {
        private readonly object _disposeLock = new();
        private readonly AsyncLocal<bool> _insideDisposal = new();
        private TaskCompletionSource? _disposeCompletion;

        public IServiceProvider Services => inner.Services;
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync()
        {
            // A provider may dispose its IHost singleton while this same host is disposing
            // that provider. Joining that task from inside itself would deadlock.
            if (_insideDisposal.Value)
            {
                return ValueTask.CompletedTask;
            }

            lock (_disposeLock)
            {
                if (_disposeCompletion is null)
                {
                    _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = DisposeInnerAsync(_disposeCompletion);
                }

                return new ValueTask(_disposeCompletion.Task);
            }
        }

        private async Task DisposeInnerAsync(TaskCompletionSource completion)
        {
            _insideDisposal.Value = true;
            try
            {
                if (inner is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    inner.Dispose();
                }

                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
            finally
            {
                _insideDisposal.Value = false;
            }
        }
    }

    private sealed class ObservedSource(IConfigurationSource inner, Func<IConfigurationProvider, IConfigurationProvider> observe)
        : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => observe(inner.Build(builder));
    }

    private sealed class OwnedProvider(IConfigurationProvider inner, AspireProfileActivationHostBuilder owner)
        : IConfigurationProvider, IDisposable, IAsyncDisposable
    {
        private readonly object _disposalLock = new();
        private TaskCompletionSource? _disposalCompletion;
        private ExceptionDispatchInfo? _disposalError;
        internal AspireProfileActivationHostBuilder Owner => owner;
        public bool TryGet(string key, out string? value) => inner.TryGet(key, out value);
        public void Set(string key, string? value) => inner.Set(key, value);
        public void Load() => inner.Load();
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => inner.GetChildKeys(earlierKeys, parentPath);
        public IChangeToken GetReloadToken() => new ObservedToken(inner.GetReloadToken(), owner);
        public void Dispose()
        {
            if (inner is not IDisposable synchronous)
            {
                return;
            }

            TaskCompletionSource? start = null;
            lock (_disposalLock)
            {
                if (_disposalCompletion is null)
                {
                    start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposalCompletion = start;
                }
            }

            if (start is null)
            {
                return;
            }

            try
            {
                synchronous.Dispose();
            }
            catch (Exception error)
            {
                _disposalError = ExceptionDispatchInfo.Capture(error);
                throw;
            }
            finally
            {
                start.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? start = null;
            Task completion;
            lock (_disposalLock)
            {
                if (_disposalCompletion is null)
                {
                    start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposalCompletion = start;
                }

                completion = _disposalCompletion.Task;
            }

            if (start is not null)
            {
                _ = DisposeInnerAsync(start);
            }

            return new ValueTask(JoinDisposalAsync(completion));
        }

        private async Task DisposeInnerAsync(TaskCompletionSource completion)
        {
            try
            {
                if (inner is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (inner is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            catch (Exception error)
            {
                _disposalError = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                completion.TrySetResult();
            }
        }

        private async Task JoinDisposalAsync(Task completion)
        {
            await completion.ConfigureAwait(false);
            _disposalError?.Throw();
        }
    }

    private sealed class ObservedToken(IChangeToken inner, AspireProfileActivationHostBuilder owner) : IChangeToken
    {
        public bool HasChanged => inner.HasChanged;
        public bool ActiveChangeCallbacks => inner.ActiveChangeCallbacks;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            var registration = new OwnedRegistration(inner.RegisterChangeCallback(callback, state), owner);
            owner.ObserveRegistration(registration);
            return registration;
        }
    }

    private sealed class OwnedRegistration(IDisposable inner, AspireProfileActivationHostBuilder owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    owner.ForgetRegistration(this);
                }
            }
        }
    }
}
