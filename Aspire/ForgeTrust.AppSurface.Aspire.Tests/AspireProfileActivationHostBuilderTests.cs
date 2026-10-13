using Autofac;
using Autofac.Extensions.DependencyInjection;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Dependency.Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

public sealed class AspireProfileActivationHostBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStartup_HookFailureDrainsNestedConfigurationOnce(bool servicesFailure)
    {
        var hostProvider = new ProbeProvider();
        var appProvider = new ProbeProvider();
        var primary = new InvalidOperationException("unique-hook-failure");
        var module = new ProbeModule
        {
            Before = builder =>
            {
                builder.ConfigureHostConfiguration(config => config.Add(new ProbeSource(hostProvider)));
                builder.ConfigureAppConfiguration((_, config) => config.Add(new ProbeSource(appProvider)));
            }
        };
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = Startup(module, ownership);
        if (servicesFailure) builder.ConfigureServices((_, _) => throw primary);
        else builder.ConfigureAppConfiguration((_, _) => throw primary);

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, hostProvider.DisposeCount);
        Assert.Equal(servicesFailure ? 1 : 0, appProvider.DisposeCount);
        Assert.Equal(0, hostProvider.ActiveRegistrations);
        Assert.Equal(0, appProvider.ActiveRegistrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHost_PartialSourceBuildOrRootLoadFailureDisposesAcquiredProviders(bool loadFailure)
    {
        var prior = new ProbeProvider();
        var primary = new InvalidOperationException("unique-configuration-failure");
        var later = new ProbeProvider { LoadError = primary };
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Add(new ProbeSource(prior));
            config.Add(loadFailure ? new ProbeSource(later) : new FailingSource(primary));
        });

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, prior.DisposeCount);
        Assert.Equal(loadFailure ? 1 : 0, later.DisposeCount);
        Assert.Equal(0, prior.ActiveRegistrations);
        Assert.Equal(0, later.ActiveRegistrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHost_ClearedChainAndSharedProvidersAreDisposedOnce(bool sharedProvider)
    {
        var hostProvider = new ProbeProvider();
        var appProvider = sharedProvider ? hostProvider : new ProbeProvider();
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureHostConfiguration(config => config.Add(new ProbeSource(hostProvider)));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.Add(new ProbeSource(appProvider));
        });
        var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<IConfiguration>());
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, hostProvider.DisposeCount);
        Assert.Equal(1, appProvider.DisposeCount);
        Assert.Equal(0, appProvider.ActiveRegistrations);
    }

    [Fact]
    public async Task RealStartup_ReplacedFileProviderIsDisposedOnConfigurationFailure()
    {
        IFileProvider? original = null;
        var replacement = new ProbeFileProvider();
        var primary = new InvalidOperationException("unique-after-replacement");
        var module = new ProbeModule
        {
            Before = builder => builder.ConfigureAppConfiguration((context, _) =>
            {
                original = context.HostingEnvironment.ContentRootFileProvider;
                context.HostingEnvironment.ContentRootFileProvider = replacement;
            })
        };
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = Startup(module, ownership);
        builder.ConfigureAppConfiguration((_, _) => throw primary);

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        Assert.IsType<PhysicalFileProvider>(original);
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, replacement.AsyncDisposeCount);
        Assert.Equal(0, replacement.SyncDisposeCount);
    }

    [Fact]
    public async Task RealHost_SelectedProviderReturnedBeforeHostResolutionFailureIsDisposedWithoutWrapping()
    {
        var factory = new ProbeFactory();
        var primary = new InvalidOperationException("unique-IHost-resolution-failure");
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]).UseServiceProviderFactory(factory);
        builder.ConfigureServices((_, services) => services.AddSingleton<IHost>(_ => throw primary));

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        Assert.NotNull(factory.Returned);
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, factory.Returned!.AsyncDisposeCount);
        Assert.Equal(0, factory.Returned.SyncDisposeCount);
    }

    // Value: protects=one cleanup for a returned provider under reentry and concurrent callers;
    // fails_when=cleanup starts before its completion task is published or reentry joins itself;
    // why_new=existing concurrent cleanup covers provider leases, not ownership-scope reentry;
    // seam=none; no_claim=custom providers that fail before returning remain caller-owned.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHost_FailedHostResolutionReentrantCleanupDisposesProviderOnce(bool cleanupFails)
    {
        var ownership = new AspireProfileActivationHostBuilder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("reentrant-host-resolution-failure");
        var cleanup = new InvalidOperationException("reentrant-provider-cleanup-failure");
        var factory = new ProbeFactory();
        factory.BeforeDisposal = async () =>
        {
            if (factory.Returned!.AsyncDisposeCount == 1)
            {
                await ownership.DisposeAsync();
            }

            entered.TrySetResult();
            await release.Task;
            if (cleanupFails) throw cleanup;
        };
        var builder = ownership.ConfigureDefaults([]).UseServiceProviderFactory(factory);
        builder.ConfigureServices((_, services) => services.AddSingleton<IHost>(_ => throw primary));
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));

        var first = ownership.DisposeAsync().AsTask();
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = ownership.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, factory.Returned!.AsyncDisposeCount);
        }
        finally
        {
            release.TrySetResult();
        }

        if (cleanupFails)
        {
            Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => second!.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => ownership.DisposeAsync().AsTask()));
        }
        else
        {
            await Task.WhenAll(first, second!).WaitAsync(TimeSpan.FromSeconds(5));
            await ownership.DisposeAsync();
        }

        Assert.Equal(1, factory.Returned!.AsyncDisposeCount);
        Assert.Equal(0, factory.Returned.SyncDisposeCount);
    }

    [Fact]
    public async Task RealHost_SuccessRetainsSelectedProviderObjectAndHostDisposalPolicy()
    {
        var factory = new ProbeFactory();
        var replacement = new ProbeFileProvider();
        var ownedConfiguration = new ProbeProvider();
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]).UseServiceProviderFactory(_ => factory);
        builder.ConfigureAppConfiguration((context, config) =>
        {
            context.HostingEnvironment.ContentRootFileProvider = replacement;
            config.Add(new ProbeSource(ownedConfiguration));
        });
        var host = builder.Build();

        Assert.Same(factory.Returned, host.Services);
        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, factory.Returned!.AsyncDisposeCount);
        Assert.Equal(0, factory.Returned.SyncDisposeCount);
        Assert.Equal(1, replacement.AsyncDisposeCount);
        Assert.Equal(0, replacement.SyncDisposeCount);
        Assert.Equal(1, ownedConfiguration.DisposeCount);
    }

    [Fact]
    public async Task RealHost_SynchronousRootDisposalLeavesAsyncOnlyProviderForOwnerAndConcurrentCleanupJoins()
    {
        var provider = new AsyncOnlyProbeProvider("async-only", "present", holdDisposal: true);
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) => config.Add(new ProbeSource(provider)));
        var host = builder.Build();
        var root = Assert.IsAssignableFrom<IConfigurationRoot>(host.Services.GetRequiredService<IConfiguration>());
        var lease = Assert.Single(root.Providers, candidate => candidate.TryGet("async-only", out var value) && value == "present");

        var synchronousHostCleanup = Task.Run(host.Dispose);
        try
        {
            await synchronousHostCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            provider.ReleaseDisposal();
            throw;
        }

        Assert.Equal(0, provider.DisposeCount);

        var externalCleanup = Assert.IsAssignableFrom<IAsyncDisposable>(lease).DisposeAsync().AsTask();
        Task? ownerCleanup = null;

        try
        {
            await provider.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
            ownerCleanup = ownership.DisposeAsync().AsTask();

            Assert.False(ownerCleanup.IsCompleted);
            Assert.Equal(1, provider.DisposeCount);
        }
        finally
        {
            ownerCleanup ??= ownership.DisposeAsync().AsTask();
            provider.ReleaseDisposal();
        }

        await Task.WhenAll(externalCleanup, ownerCleanup!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task RealHost_AsyncOnlyProviderLoadFailureAwaitsCleanupAndDrainsOtherLeasesAfterCleanupFailure()
    {
        var prior = new AsyncOnlyProbeProvider("prior-async-only", "present");
        var primary = new InvalidOperationException("unique-async-provider-load-failure");
        var cleanup = new InvalidOperationException("unique-async-provider-cleanup-failure");
        var failing = new AsyncOnlyProbeProvider("failing-async-only", "present", primary, cleanup, holdDisposal: true);
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Add(new ProbeSource(prior));
            config.Add(new ProbeSource(failing));
        });

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        var ownerCleanup = ownership.DisposeAsync().AsTask();
        try
        {
            await failing.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ownerCleanup.IsCompleted);
            Assert.Equal(1, failing.DisposeCount);
        }
        finally
        {
            failing.ReleaseDisposal();
        }

        var observedCleanup = await Assert.ThrowsAsync<InvalidOperationException>(() => ownerCleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(cleanup, observedCleanup);
        await prior.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, prior.DisposeCount);
        Assert.Equal(1, failing.DisposeCount);
    }

    [Fact]
    public async Task RealStartup_ModulePropertiesAndRepeatedSourcesReuseOwnedProvider()
    {
        const string key = "activation-owned-provider";
        const string propertyName = "activation-module-marker";
        var provider = new ProbeProvider();
        provider.Seed(key, "module-value");
        var marker = new object();
        IDictionary<object, object>? moduleProperties = null;
        var module = new ProbeModule
        {
            Before = builder =>
            {
                moduleProperties = builder.Properties;
                builder.Properties[propertyName] = marker;
            }
        };
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = Startup(module, ownership);

        Assert.NotNull(moduleProperties);
        Assert.Same(moduleProperties, builder.Properties);
        Assert.Same(marker, builder.Properties[propertyName]);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.Add(new ProbeSource(provider));
            configuration.Add(new ReusingFirstSource());
        });

        var host = builder.Build();
        var root = Assert.IsAssignableFrom<IConfigurationRoot>(host.Services.GetRequiredService<IConfiguration>());
        var providers = root.Providers.ToArray();

        Assert.Equal(2, providers.Length);
        Assert.Same(providers[0], providers[1]);
        Assert.True(providers[0].TryGet(key, out var before));
        Assert.Equal("module-value", before);

        providers[0].Set(key, "updated-value");
        Assert.Equal("updated-value", root[key]);

        var token = providers[0].GetReloadToken();
        Assert.False(token.HasChanged);
        Assert.True(token.ActiveChangeCallbacks);
        provider.HasChanged = true;
        provider.ActiveChangeCallbacks = false;
        Assert.True(token.HasChanged);
        Assert.False(token.ActiveChangeCallbacks);

        await ownership.DisposeAsync();
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task RealHost_LateReloadRegistrationAfterOwnerCleanupIsDisposedImmediately()
    {
        const string key = "late-registration-provider";
        var provider = new ProbeProvider();
        provider.Seed(key, "available");
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.Add(new ProbeSource(provider));
        });

        var host = builder.Build();
        var root = Assert.IsAssignableFrom<IConfigurationRoot>(host.Services.GetRequiredService<IConfiguration>());
        var observedProvider = Assert.Single(root.Providers, candidate => candidate.TryGet(key, out var value) && value == "available");
        var token = observedProvider.GetReloadToken();
        await ownership.DisposeAsync();

        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal(0, provider.ActiveRegistrations);
        var registrationsBeforeLateCall = provider.RegistrationCount;
        var disposalsBeforeLateCall = provider.RegistrationDisposeCount;
        var callbackCount = 0;
        var registration = token.RegisterChangeCallback(_ => callbackCount++, null);
        try
        {
            Assert.Equal(registrationsBeforeLateCall + 1, provider.RegistrationCount);
            Assert.Equal(disposalsBeforeLateCall + 1, provider.RegistrationDisposeCount);
            Assert.Equal(0, provider.ActiveRegistrations);
            Assert.Equal(0, callbackCount);
        }
        finally
        {
            registration.Dispose();
        }

        Assert.Equal(disposalsBeforeLateCall + 1, provider.RegistrationDisposeCount);
    }

    [Fact]
    public async Task RealHost_FactoryCreatedSynchronousHostUsesOwnedAsyncCleanupFallback()
    {
        var factory = new ProbeFactory();
        var suppliedHost = new SynchronousOwnedHost(() => factory.Returned!);
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]).UseServiceProviderFactory(_ => factory);
        builder.ConfigureServices((_, services) => services.AddSingleton<IHost>(_ => suppliedHost));

        var host = builder.Build();

        Assert.NotSame(suppliedHost, host);
        Assert.IsAssignableFrom<IAsyncDisposable>(host);
        Assert.Same(factory.Returned, host.Services);

        await ownership.DisposeAsync();
        await ownership.DisposeAsync();

        Assert.Equal(1, suppliedHost.Disposals);
        Assert.Equal(1, factory.Returned!.SyncDisposeCount);
        Assert.Equal(0, factory.Returned.AsyncDisposeCount);
    }

    [Fact]
    public async Task RealHost_CallerOwnedSingletonAndBorrowedConfigurationRemainOwnedByCaller()
    {
        var callerProvider = new ProbeProvider();
        using var callerRoot = new ConfigurationRoot([callerProvider]);
        using var callerSingleton = new CallerSingleton();
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(callerRoot));
        builder.ConfigureServices((_, services) => services.AddSingleton(callerSingleton));
        var host = builder.Build();

        Assert.Same(callerSingleton, host.Services.GetRequiredService<CallerSingleton>());
        await ownership.DisposeAsync();

        Assert.Equal(0, callerSingleton.DisposeCount);
        Assert.Equal(0, callerProvider.DisposeCount);
    }

    [Fact]
    public async Task RealHost_SecondaryDisposalFailureDoesNotBlockOtherConfigurationCleanup()
    {
        var first = new ProbeProvider();
        var cleanupError = new InvalidOperationException("unique-cleanup-failure");
        var later = new ProbeProvider { DisposeError = cleanupError };
        var primary = new InvalidOperationException("unique-services-failure");
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) => config.Add(new ProbeSource(first)).Add(new ProbeSource(later)));
        builder.ConfigureServices((_, _) => throw primary);

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => builder.Build()));
        Assert.Same(cleanupError, await Assert.ThrowsAsync<InvalidOperationException>(() => ownership.DisposeAsync().AsTask()));
        Assert.Same(cleanupError, await Assert.ThrowsAsync<InvalidOperationException>(() => ownership.DisposeAsync().AsTask()));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, later.DisposeCount);
        Assert.Equal(0, first.ActiveRegistrations);
        Assert.Equal(0, later.ActiveRegistrations);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task RealHost_DefaultProviderRetainsEnvironmentValidation(string environment, bool rejectsInvalidScope)
    {
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]).UseEnvironment(environment);
        builder.ConfigureServices((_, services) => services.AddScoped<ScopedProbe>().AddSingleton<InvalidSingleton>());
        if (rejectsInvalidScope) Assert.Throws<AggregateException>(() => builder.Build());
        else Assert.IsType<InvalidSingleton>(builder.Build().Services.GetRequiredService<InvalidSingleton>());
        await ownership.DisposeAsync();
    }

    [Fact]
    public async Task RealHost_ReturnedHostCleanupFailureDrainsRemainingConfiguration()
    {
        var earlier = new ProbeProvider();
        var cleanup = new InvalidOperationException("returned-host-cleanup-failure");
        var later = new ProbeProvider { DisposeError = cleanup };
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureAppConfiguration((_, config) => config.Add(new ProbeSource(earlier)).Add(new ProbeSource(later)));
        builder.Build();

        Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => ownership.DisposeAsync().AsTask()));
        Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => ownership.DisposeAsync().AsTask()));
        Assert.Equal(1, earlier.DisposeCount);
        Assert.Equal(1, later.DisposeCount);
        Assert.Equal(0, earlier.ActiveRegistrations);
        Assert.Equal(0, later.ActiveRegistrations);
    }

    [Fact]
    public async Task RealHost_HostFactoryLeaseForwardsExplicitLifecycleWithoutStartingDuringBuild()
    {
        var sentinel = new HostedSentinel();
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]);
        builder.ConfigureServices((_, services) => services.AddHostedService(_ => sentinel));
        var host = builder.Build();

        Assert.Equal(0, sentinel.Starts);
        await host.StartAsync();
        await host.StopAsync();
        Assert.Equal(1, sentinel.Starts);
        Assert.Equal(1, sentinel.Stops);
        host.Dispose();
        await ownership.DisposeAsync();
    }

    [Fact]
    public async Task RealHost_InstanceRegistrationRetainsIdentityAndItsOwnDisposalPolicy()
    {
        var factory = new ProbeFactory();
        var suppliedHost = new SynchronousOwnedHost(() => factory.Returned!);
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = ownership.ConfigureDefaults([]).UseServiceProviderFactory(factory);
        builder.ConfigureServices((_, services) => services.AddSingleton<IHost>(suppliedHost));

        Assert.Same(suppliedHost, builder.Build());
        await ownership.DisposeAsync();
        Assert.Equal(1, suppliedHost.Disposals);
        Assert.Equal(1, factory.Returned!.SyncDisposeCount);
        Assert.Equal(0, factory.Returned.AsyncDisposeCount);
    }

    [Fact]
    public async Task RealAutofacModule_RetainsProviderAndLaterContainerPrecedence()
    {
        var module = new AutofacProbeModule();
        var ownership = new AspireProfileActivationHostBuilder();
        var builder = Startup(module, ownership);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            module.Events.Add("configuration-hook");
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["scenario"] = "hook" });
        });
        builder.ConfigureServices((_, services) =>
        {
            module.Events.Add("services-hook");
            Assert.Contains(services, descriptor => descriptor.ImplementationInstance is Choice { Value: "module-after" });
            services.RemoveAll<Choice>();
            services.AddSingleton(new Choice("hook"));
        });
        var host = builder.Build();
        Assert.IsType<AutofacServiceProvider>(host.Services);
        var constructed = host.Services.GetRequiredService<Constructed>();
        Assert.Equal("hook", constructed.Scenario);
        Assert.Equal("container", constructed.Choice);
        Assert.True(module.Events.IndexOf("configuration-hook") > module.Events.IndexOf("module-configuration"));
        Assert.True(module.Events.IndexOf("services-hook") > module.Events.IndexOf("module-after-services"));
        Assert.True(module.Events.IndexOf("constructor") > module.Events.IndexOf("container"));
        await ownership.DisposeAsync();
    }

    private static IHostBuilder Startup<TModule>(TModule module, AspireProfileActivationHostBuilder ownership)
        where TModule : IAppSurfaceHostModule, new() =>
        ((IAppSurfaceStartup)new AspireAppStartup<TModule>()).CreateHostBuilder(new StartupContext([], module)
        {
            OverrideEntryPointAssembly = typeof(TModule).Assembly,
            HostBuilderFactory = ownership.ConfigureDefaults
        });

    public sealed class ProbeModule : IAppSurfaceHostModule
    {
        public Action<IHostBuilder>? Before { get; init; }
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) => Before?.Invoke(builder);
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
    }

    public sealed class AutofacProbeModule : AppSurfaceAutofacHostModule
    {
        public List<string> Events { get; } = [];
        public override void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
            base.ConfigureHostBeforeServices(context, builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                Events.Add("module-configuration");
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["scenario"] = "module" });
            });
            builder.ConfigureServices((_, services) =>
            {
                services.AddSingleton(new Choice("module"));
                services.AddSingleton(new EventTrace(Events));
                services.AddSingleton<Constructed>();
            });
        }
        public override void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
            base.ConfigureHostAfterServices(context, builder);
            builder.ConfigureServices((_, services) =>
            {
                Events.Add("module-after-services");
                services.AddSingleton(new Choice("module-after"));
            });
            builder.ConfigureContainer<ContainerBuilder>((_, container) =>
            {
                Events.Add("container");
                container.RegisterInstance(new Choice("container"));
            });
        }
    }

    private sealed record Choice(string Value);
    private sealed record EventTrace(List<string> Events);
    private sealed class Constructed
    {
        public string? Scenario { get; }
        public string Choice { get; }
        public Constructed(IConfiguration configuration, Choice choice, EventTrace trace)
        {
            trace.Events.Add("constructor");
            Scenario = configuration["scenario"];
            Choice = choice.Value;
        }
    }

    private sealed class ProbeSource(IConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class ReusingFirstSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => builder.Sources[0].Build(builder);
    }

    private sealed class FailingSource(Exception primary) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => throw primary;
    }

    private sealed class ProbeProvider : IConfigurationProvider, IChangeToken, IDisposable
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        public Exception? LoadError { get; init; }
        public Exception? DisposeError { get; init; }
        public int DisposeCount { get; private set; }
        public int ActiveRegistrations { get; private set; }
        public int RegistrationCount { get; private set; }
        public int RegistrationDisposeCount { get; private set; }
        public bool HasChanged { get; set; }
        public bool ActiveChangeCallbacks { get; set; } = true;
        public bool TryGet(string key, out string? value) => _values.TryGetValue(key, out value);
        public void Set(string key, string? value) => _values[key] = value;
        internal void Seed(string key, string? value) => _values[key] = value;
        public void Load() { if (LoadError is not null) throw LoadError; }
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => earlierKeys;
        public IChangeToken GetReloadToken() => this;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            ActiveRegistrations++;
            RegistrationCount++;
            return new CallbackRegistration(() =>
            {
                ActiveRegistrations--;
                RegistrationDisposeCount++;
            });
        }
        public void Dispose() { DisposeCount++; if (DisposeError is not null) throw DisposeError; }
    }

    private sealed class AsyncOnlyProbeProvider : IConfigurationProvider, IAsyncDisposable
    {
        private readonly string _key;
        private readonly string _value;
        private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal AsyncOnlyProbeProvider(string key, string value, Exception? loadError = null, Exception? disposeError = null, bool holdDisposal = false)
        {
            _key = key;
            _value = value;
            LoadError = loadError;
            DisposeError = disposeError;
            if (!holdDisposal)
            {
                _disposeRelease.TrySetResult();
            }
        }

        internal Exception? LoadError { get; }
        internal Exception? DisposeError { get; }
        internal Task DisposeStarted => _disposeStarted.Task;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal void ReleaseDisposal() => _disposeRelease.TrySetResult();
        public bool TryGet(string key, out string? value)
        {
            value = key == _key ? _value : null;
            return value is not null;
        }
        public void Set(string key, string? value) { }
        public void Load() { if (LoadError is not null) throw LoadError; }
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => earlierKeys;
        public IChangeToken GetReloadToken() => NullChangeToken.Singleton;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposeStarted.TrySetResult();
            await _disposeRelease.Task.ConfigureAwait(false);
            if (DisposeError is not null) throw DisposeError;
        }
    }

    private sealed class CallbackRegistration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class ProbeFileProvider : IFileProvider, IDisposable, IAsyncDisposable
    {
        public int SyncDisposeCount { get; private set; }
        public int AsyncDisposeCount { get; private set; }
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
        public void Dispose() => SyncDisposeCount++;
        public ValueTask DisposeAsync() { AsyncDisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ProbeFactory : IServiceProviderFactory<IServiceCollection>
    {
        public ProbeServiceProvider? Returned { get; private set; }
        public Func<ValueTask>? BeforeDisposal { get; set; }
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;
        public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) =>
            Returned = new ProbeServiceProvider(containerBuilder.BuildServiceProvider(), BeforeDisposal);
    }

    private sealed class ProbeServiceProvider(ServiceProvider inner, Func<ValueTask>? beforeDisposal = null) : IServiceProvider, IDisposable, IAsyncDisposable
    {
        public int SyncDisposeCount { get; private set; }
        public int AsyncDisposeCount { get; private set; }
        public object? GetService(Type serviceType) => inner.GetService(serviceType);
        public void Dispose() { SyncDisposeCount++; inner.Dispose(); }
        public async ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            try
            {
                if (beforeDisposal is not null) await beforeDisposal();
            }
            finally
            {
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class CallerSingleton : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class HostedSentinel : IHostedService
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
    }

    private sealed class SynchronousOwnedHost(Func<IServiceProvider> provider) : IHost
    {
        public int Disposals { get; private set; }
        public IServiceProvider Services => provider();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { Disposals++; ((IDisposable)Services).Dispose(); }
    }

    private sealed class ScopedProbe;
    private sealed class InvalidSingleton(ScopedProbe dependency)
    {
        public ScopedProbe Dependency { get; } = dependency;
    }
}
