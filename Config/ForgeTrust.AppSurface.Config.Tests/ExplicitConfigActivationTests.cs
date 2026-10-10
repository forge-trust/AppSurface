using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ExplicitConfigActivationTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task ExplicitSingletonIsLazyAndActivatesOncePerProviderUnderConcurrentResolution()
    {
        var manager = new RecordingConfigManager((_, _) => "ready");
        var services = NewServices(manager);
        services.AddAppSurfaceConfig<ConcurrentActivationConfig>();
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(ConcurrentActivationConfig));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);

        using var firstProvider = services.BuildServiceProvider();
        using var secondProvider = services.BuildServiceProvider();
        Assert.Equal(0, manager.Calls);
        var firstProbe = firstProvider.GetRequiredService<ActivationProbe>();
        firstProbe.ArmGate();

        const int workerCount = 8;
        using var ready = new CountdownEvent(workerCount);
        using var attempted = new CountdownEvent(workerCount);
        using var start = new ManualResetEventSlim();
        var firstTasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                ready.Signal();
                if (!start.Wait(BarrierTimeout))
                {
                    throw new TimeoutException("Concurrent activation start barrier timed out.");
                }

                attempted.Signal();
                return firstProvider.GetRequiredService<ConcurrentActivationConfig>();
            }))
            .ToArray();

        try
        {
            Assert.True(ready.Wait(BarrierTimeout), "Resolution workers did not reach the start barrier.");
            start.Set();
            Assert.True(firstProbe.ConstructorEntered.Wait(BarrierTimeout), "The first constructor did not enter its gate.");
            Assert.True(attempted.Wait(BarrierTimeout), "Not all workers attempted resolution while construction was gated.");
        }
        finally
        {
            start.Set();
            firstProbe.ReleaseConstructor();
            try
            {
                await Task.WhenAll(firstTasks).WaitAsync(BarrierTimeout);
            }
            catch (Exception)
            {
                // Drain bounded worker tasks so a failed assertion cannot leave blocked thread-pool work behind.
            }
        }

        var firstInstances = await Task.WhenAll(firstTasks).WaitAsync(BarrierTimeout);
        Assert.All(firstInstances, instance => Assert.Same(firstInstances[0], instance));
        Assert.Equal(1, firstProbe.Constructions);
        Assert.Equal(1, firstProbe.Initializations);
        Assert.Equal(1, manager.Calls);
        Assert.Equal("ready", firstInstances[0].Value);
        Assert.Same(firstProvider.GetRequiredService<ConcurrentActivationConfig>(), firstInstances[0]);

        var second = secondProvider.GetRequiredService<ConcurrentActivationConfig>();
        Assert.NotSame(firstInstances[0], second);
        Assert.Equal(1, secondProvider.GetRequiredService<ActivationProbe>().Constructions);
        Assert.Equal(1, secondProvider.GetRequiredService<ActivationProbe>().Initializations);
        Assert.Equal(2, manager.Calls);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void ExistingCallerSingletonRemainsCallerOwnedAndIsNeverFrameworkInitialized(string registrationKind)
    {
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        var callerInstance = new CallerOwnedConfig();
        var factoryCalls = 0;
        ServiceDescriptor descriptor = registrationKind switch
        {
            "instance" => ServiceDescriptor.Singleton(callerInstance),
            "factory" => ServiceDescriptor.Singleton<CallerOwnedConfig>(_ =>
            {
                factoryCalls++;
                return callerInstance;
            }),
            _ => ServiceDescriptor.Singleton(typeof(CallerOwnedConfig), typeof(CallerOwnedConfig))
        };
        services.Add(descriptor);

        Assert.Same(services, services.AddAppSurfaceConfig<CallerOwnedConfig>());

        Assert.Same(descriptor, Assert.Single(services, item => item.ServiceType == typeof(CallerOwnedConfig)));
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<CallerOwnedConfig>();

        if (registrationKind is "instance" or "factory")
        {
            Assert.Same(callerInstance, resolved);
        }

        Assert.Equal(registrationKind == "factory" ? 1 : 0, factoryCalls);
        Assert.Equal(0, resolved.InitCalls);
        Assert.Equal("Explicit:CallerOwned", provider.GetRequiredService<ConfigDeclarationRegistry>()
            .GetForConfigType(typeof(CallerOwnedConfig)).LogicalKey.Value);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void DiscoveryCallbackPreservesExistingCallerSingletonAndNeverFrameworkInitializesIt(string registrationKind)
    {
        var manager = new RecordingConfigManager((_, _) => "unused");
        var services = NewServices(manager);
        var callerInstance = new CallerOwnedConfig();
        var factoryCalls = 0;
        ServiceDescriptor descriptor = registrationKind switch
        {
            "instance" => ServiceDescriptor.Singleton(callerInstance),
            "factory" => ServiceDescriptor.Singleton<CallerOwnedConfig>(_ =>
            {
                factoryCalls++;
                return callerInstance;
            }),
            _ => ServiceDescriptor.Singleton(typeof(CallerOwnedConfig), typeof(CallerOwnedConfig))
        };
        services.Add(descriptor);

        var context = new StartupContext([], new NoHostModule())
        {
            OverrideEntryPointAssembly = typeof(ExplicitConfigActivationTests).Assembly
        };
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        context.CustomRegistrations[0](services);
        foreach (var rawDescriptor in services.Where(item =>
                     item.ServiceType == typeof(ConfigAuditRawDeclaration)).ToArray())
        {
            if (rawDescriptor.ImplementationInstance is ConfigAuditRawDeclaration declaration
                && declaration.ConfigType == typeof(CallerOwnedConfig))
            {
                continue;
            }

            services.Remove(rawDescriptor);
        }

        services.Replace(ServiceDescriptor.Singleton<IConfigManager>(manager));
        Assert.Same(descriptor, Assert.Single(services, item => item.ServiceType == typeof(CallerOwnedConfig)));
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<CallerOwnedConfig>();

        if (registrationKind is "instance" or "factory")
        {
            Assert.Same(callerInstance, resolved);
        }

        Assert.Equal(registrationKind == "factory" ? 1 : 0, factoryCalls);
        Assert.Equal(0, resolved.InitCalls);
        Assert.Equal("Explicit:CallerOwned", provider.GetRequiredService<ConfigDeclarationRegistry>()
            .GetForConfigType(typeof(CallerOwnedConfig)).LogicalKey.Value);
    }

    [Fact]
    public void ValidSingletonReplaceKeepsDeclarationMetadataAndCallerOwnership()
    {
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        services.AddAppSurfaceConfig<ReplacedCallerConfig>();
        var replacement = new ReplacedCallerConfig();
        services.Replace(ServiceDescriptor.Singleton(replacement));

        using var provider = services.BuildServiceProvider();
        Assert.Same(replacement, provider.GetRequiredService<ReplacedCallerConfig>());
        Assert.Equal(0, replacement.InitCalls);
        Assert.Equal("Explicit:ReplacedCaller", provider.GetRequiredService<ConfigDeclarationRegistry>()
            .GetForConfigType(typeof(ReplacedCallerConfig)).LogicalKey.Value);
    }

    [Fact]
    public void ClonedCollectionsKeepSelectionAndActivationStateProviderLocal()
    {
        var sharedInstance = new ClonedInstanceConfig();
        var services = NewServices(new RecordingConfigManager((_, _) => "cloned"));
        services.AddSingleton<ProviderLifetimeProbe>();
        services.AddSingleton(sharedInstance);
        services.AddSingleton<ClonedFactoryConfig>(provider =>
            new ClonedFactoryConfig(provider.GetRequiredService<ProviderLifetimeProbe>()));
        services.AddAppSurfaceConfig<ClonedInstanceConfig>();
        services.AddAppSurfaceConfig<ClonedFactoryConfig>();
        services.AddAppSurfaceConfig<ClonedTypeConfig>();

        var firstCollection = new ServiceCollection();
        var secondCollection = new ServiceCollection();
        foreach (var descriptor in services)
        {
            firstCollection.Add(descriptor);
            secondCollection.Add(descriptor);
        }

        firstCollection.AddAppSurfaceConfig<OnlyFirstCloneConfig>();
        using var firstProvider = firstCollection.BuildServiceProvider();
        using var secondProvider = secondCollection.BuildServiceProvider();

        var firstType = firstProvider.GetRequiredService<ClonedTypeConfig>();
        var secondType = secondProvider.GetRequiredService<ClonedTypeConfig>();
        var firstFactory = firstProvider.GetRequiredService<ClonedFactoryConfig>();
        var secondFactory = secondProvider.GetRequiredService<ClonedFactoryConfig>();
        var firstInstance = firstProvider.GetRequiredService<ClonedInstanceConfig>();
        var secondInstance = secondProvider.GetRequiredService<ClonedInstanceConfig>();
        var firstProbe = firstProvider.GetRequiredService<ProviderLifetimeProbe>();
        var secondProbe = secondProvider.GetRequiredService<ProviderLifetimeProbe>();

        Assert.NotSame(firstType, secondType);
        Assert.NotSame(firstFactory, secondFactory);
        Assert.NotSame(firstProbe, secondProbe);
        Assert.Same(firstProbe, firstType.Probe);
        Assert.Same(secondProbe, secondType.Probe);
        Assert.Same(firstProbe, firstFactory.Probe);
        Assert.Same(secondProbe, secondFactory.Probe);
        Assert.Equal(1, firstProbe.TypeConstructions);
        Assert.Equal(1, secondProbe.TypeConstructions);
        Assert.Equal(1, firstProbe.FactoryInvocations);
        Assert.Equal(1, secondProbe.FactoryInvocations);
        Assert.Equal(1, firstProbe.TypeInitializations);
        Assert.Equal(1, secondProbe.TypeInitializations);
        Assert.Same(sharedInstance, firstInstance);
        Assert.Same(sharedInstance, secondInstance);
        Assert.Equal(0, sharedInstance.InitCalls);

        var firstRegistry = firstProvider.GetRequiredService<ConfigDeclarationRegistry>();
        var secondRegistry = secondProvider.GetRequiredService<ConfigDeclarationRegistry>();
        Assert.NotSame(firstRegistry, secondRegistry);
        Assert.NotSame(firstRegistry.GetForConfigType(typeof(ClonedTypeConfig)),
            secondRegistry.GetForConfigType(typeof(ClonedTypeConfig)));
        Assert.Same(
            Assert.Single(firstCollection, item => item.ServiceType == typeof(ClonedTypeConfig)),
            Assert.Single(secondCollection, item => item.ServiceType == typeof(ClonedTypeConfig)));
        Assert.Same(
            Assert.Single(firstCollection, item => item.ServiceType == typeof(ClonedFactoryConfig)),
            Assert.Single(secondCollection, item => item.ServiceType == typeof(ClonedFactoryConfig)));
        Assert.Same(
            Assert.Single(firstCollection, item => item.ServiceType == typeof(ClonedInstanceConfig)),
            Assert.Single(secondCollection, item => item.ServiceType == typeof(ClonedInstanceConfig)));
        Assert.Equal(4, firstRegistry.Entries.Count);
        Assert.Equal(3, secondRegistry.Entries.Count);
        Assert.Equal("Explicit:CloneOnlyFirst", firstRegistry.GetForConfigType(typeof(OnlyFirstCloneConfig)).LogicalKey.Value);
        Assert.Throws<InvalidOperationException>(() => secondRegistry.GetForConfigType(typeof(OnlyFirstCloneConfig)));
        Assert.Single(firstCollection, item => item.ServiceType == typeof(OnlyFirstCloneConfig));
        Assert.DoesNotContain(secondCollection, item => item.ServiceType == typeof(OnlyFirstCloneConfig));
    }

    [Fact]
    public void ActivatorUtilitiesUsesOptionalAndRegisteredConstructorDependencies()
    {
        var dependency = new OptionalActivationDependency();
        var injectedServices = NewServices(new RecordingConfigManager((_, _) => "injected"));
        injectedServices.AddSingleton(dependency);
        injectedServices.AddAppSurfaceConfig<OptionalDependencyConfig>();
        using var injectedProvider = injectedServices.BuildServiceProvider();

        var injected = injectedProvider.GetRequiredService<OptionalDependencyConfig>();
        Assert.Same(dependency, injected.Dependency);
        Assert.Equal("injected", injected.Value);

        var optionalServices = NewServices(new RecordingConfigManager((_, _) => "optional"));
        optionalServices.AddAppSurfaceConfig<OptionalDependencyConfig>();
        using var optionalProvider = optionalServices.BuildServiceProvider();
        var optional = optionalProvider.GetRequiredService<OptionalDependencyConfig>();
        Assert.Null(optional.Dependency);
        Assert.Equal("optional", optional.Value);
    }

    [Fact]
    public void MissingConstructorDependencyFailsOnlyWhenTheSelectedSingletonIsResolved()
    {
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        services.AddAppSurfaceConfig<MissingDependencyConfig>();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<MissingDependencyConfig>());

        Assert.Contains(typeof(MissingActivationDependency).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivatorUtilitiesPreservesAmbiguousConstructorFailure()
    {
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        services.AddSingleton<AmbiguousDependencyA>();
        services.AddSingleton<AmbiguousDependencyB>();
        services.AddSingleton<AmbiguousDependencyC>();
        services.AddSingleton<AmbiguousDependencyD>();
        services.AddAppSurfaceConfig<AmbiguousDependencyConfig>();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<AmbiguousDependencyConfig>());

        Assert.Contains("multiple constructors", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivatorUtilitiesPreservesThrowingDependencyException()
    {
        var expected = new InvalidOperationException("dependency sentinel");
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        services.AddSingleton<ThrowingActivationDependency>(_ => throw expected);
        services.AddAppSurfaceConfig<ThrowingDependencyConfig>();
        using var provider = services.BuildServiceProvider();

        var actual = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ThrowingDependencyConfig>());

        Assert.Same(expected, actual);
        Assert.Contains("dependency sentinel", actual.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredClassAndStructFailuresPreserveValidationAndRetryFailedSingletonActivation()
    {
        var manager = new RecordingConfigManager((_, _) => null);
        var services = NewServices(manager);
        services.AddAppSurfaceConfig<RequiredClassConfig>();
        services.AddAppSurfaceConfig<RequiredStructConfig>();
        using var provider = services.BuildServiceProvider();

        var classFirst = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<RequiredClassConfig>());
        var classSecond = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<RequiredClassConfig>());
        var structFirst = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<RequiredStructConfig>());
        var structSecond = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<RequiredStructConfig>());

        AssertValidationFailure(classFirst, typeof(RequiredClassConfig), typeof(string), "Explicit:RequiredClass");
        AssertValidationFailure(classSecond, typeof(RequiredClassConfig), typeof(string), "Explicit:RequiredClass");
        AssertValidationFailure(structFirst, typeof(RequiredStructConfig), typeof(int), "Explicit:RequiredStruct");
        AssertValidationFailure(structSecond, typeof(RequiredStructConfig), typeof(int), "Explicit:RequiredStruct");
        Assert.Equal(4, manager.Calls);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Constructions);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("object")]
    public void ObjectDataAnnotationFailuresRetainStructuredValidationAndRetry(string source)
    {
        var value = source == "object" ? new RequiredOptions { Name = null } : null;
        var manager = new RecordingConfigManager((_, _) => value);
        var services = NewServices(manager);
        services.AddAppSurfaceConfig<InvalidObjectConfig>();
        using var provider = services.BuildServiceProvider();

        var first = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<InvalidObjectConfig>());
        var second = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<InvalidObjectConfig>());

        AssertValidationFailure(first, typeof(InvalidObjectConfig), typeof(RequiredOptions), "Explicit:InvalidObject");
        AssertValidationFailure(second, typeof(InvalidObjectConfig), typeof(RequiredOptions), "Explicit:InvalidObject");
        Assert.Contains(first.Failures, failure => failure.MemberNames.Contains(nameof(RequiredOptions.Name)));
        Assert.Equal(2, manager.Calls);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Initializations);
    }

    [Fact]
    public void ScalarValidationFailureRetainsStructuredFailureAndRetriesActivation()
    {
        var manager = new RecordingConfigManager((_, _) => string.Empty);
        var services = NewServices(manager);
        services.AddAppSurfaceConfig<InvalidScalarConfig>();
        using var provider = services.BuildServiceProvider();

        var first = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<InvalidScalarConfig>());
        var second = Assert.Throws<ConfigurationValidationException>(
            () => provider.GetRequiredService<InvalidScalarConfig>());

        AssertValidationFailure(first, typeof(InvalidScalarConfig), typeof(string), "Explicit:InvalidScalar");
        AssertValidationFailure(second, typeof(InvalidScalarConfig), typeof(string), "Explicit:InvalidScalar");
        Assert.Equal(2, manager.Calls);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Constructions);
    }

    [Fact]
    public void EnvironmentConversionFailureRetainsDiagnosticAndRetriesFailedWrapperActivation()
    {
        var environment = new StaticEnvironmentProvider(new Dictionary<string, string>
        {
            ["PRODUCTION__EXPLICIT_CONVERSION"] = "not-an-integer"
        });
        var services = new ServiceCollection();
        services.AddOptions<AppSurfaceConfigKeyOptions>();
        services.AddOptions<AppSurfaceEnvironmentConfigOptions>().Configure(options =>
            options.MapKey(AppSurfaceConfigKey.Parse("Explicit:Conversion"), "EXPLICIT_CONVERSION"));
        services.AddOptions<ConfigResourceOptions>();
        services.AddLogging();
        services.AddSingleton<IEnvironmentProvider>(environment);
        services.AddSingleton<IEnvironmentConfigProvider, EnvironmentConfigProvider>();
        services.AddSingleton<IConfigManager, DefaultConfigManager>();
        services.AddSingleton<ActivationProbe>();
        services.AddAppSurfaceConfig<ConversionConfig>();
        using var provider = services.BuildServiceProvider();

        var first = Assert.Throws<ConfigurationResolutionException>(
            () => provider.GetRequiredService<ConversionConfig>());
        var second = Assert.Throws<ConfigurationResolutionException>(
            () => provider.GetRequiredService<ConversionConfig>());

        Assert.Equal("config-patch-failed", first.Diagnostic.Code);
        Assert.Equal("config-patch-failed", second.Diagnostic.Code);
        Assert.Equal("Explicit:Conversion", first.Key);
        Assert.Equal("Explicit:Conversion", second.Key);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Constructions);
    }

    [Fact]
    public void ManagerExceptionAndCancellationPropagateUnchangedAndRetryActivation()
    {
        var failure = new InvalidOperationException("manager failure sentinel");
        var manager = new RecordingConfigManager((_, _) => throw failure);
        var services = NewServices(manager);
        services.AddAppSurfaceConfig<ManagerFailureConfig>();
        using var provider = services.BuildServiceProvider();

        var first = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ManagerFailureConfig>());
        var second = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ManagerFailureConfig>());

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(2, manager.Calls);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Initializations);

        var cancellation = new OperationCanceledException("cancelled by caller");
        var cancelManager = new RecordingConfigManager((_, _) => throw cancellation);
        var cancelServices = NewServices(cancelManager);
        cancelServices.AddAppSurfaceConfig<CancellationConfig>();
        using var cancelProvider = cancelServices.BuildServiceProvider();

        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(
            () => cancelProvider.GetRequiredService<CancellationConfig>()));
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(
            () => cancelProvider.GetRequiredService<CancellationConfig>()));
        Assert.Equal(2, cancelManager.Calls);
        Assert.Equal(2, cancelProvider.GetRequiredService<ActivationProbe>().Constructions);
    }

    [Fact]
    public void InitExceptionPropagatesUnchangedAndRetriesTheFailedSingletonActivation()
    {
        var expected = new InvalidOperationException("init failure sentinel");
        var services = NewServices(new RecordingConfigManager((_, _) => "ready"));
        services.AddSingleton(expected);
        services.AddAppSurfaceConfig<InitFailureConfig>();
        using var provider = services.BuildServiceProvider();

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<InitFailureConfig>()));
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<InitFailureConfig>()));
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Constructions);
        Assert.Equal(2, provider.GetRequiredService<ActivationProbe>().Initializations);
    }

    [Fact]
    public void CallerFactoryExceptionPropagatesUnchangedAndFactoryRetries()
    {
        var failure = new InvalidOperationException("caller factory sentinel");
        var calls = 0;
        var services = NewServices(new RecordingConfigManager((_, _) => "unused"));
        services.AddSingleton<FactoryFailureConfig>(_ =>
        {
            calls++;
            throw failure;
        });
        services.AddAppSurfaceConfig<FactoryFailureConfig>();
        using var provider = services.BuildServiceProvider();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<FactoryFailureConfig>()));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<FactoryFailureConfig>()));
        Assert.Equal(2, calls);
        Assert.Equal(0, provider.GetRequiredService<ActivationProbe>().Constructions);
    }

    private static ServiceCollection NewServices(RecordingConfigManager manager)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigManager>(manager);
        services.AddSingleton<IEnvironmentProvider>(new StaticEnvironmentProvider());
        services.AddSingleton<ActivationProbe>();
        return services;
    }

    private static void AssertValidationFailure(
        ConfigurationValidationException exception,
        Type configType,
        Type valueType,
        string key)
    {
        Assert.Equal(key, exception.Key);
        Assert.Equal(configType, exception.ConfigType);
        Assert.Equal(valueType, exception.ValueType);
        Assert.NotEmpty(exception.Failures);
    }

    private sealed class RecordingConfigManager(Func<Type, AppSurfaceConfigKey, object?> valueFactory) : IConfigManager
    {
        private int _calls;
        private readonly ConcurrentQueue<AppSurfaceConfigKey> _keys = new();

        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyCollection<AppSurfaceConfigKey> Keys => _keys.ToArray();

        public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
        {
            Interlocked.Increment(ref _calls);
            _keys.Enqueue(key);
            var value = valueFactory(typeof(T), key);
            return value is null ? default : (T)value;
        }

        public T? GetValue<T>(string environment, string key) => GetValue<T>(environment, AppSurfaceConfigKey.Parse(key));
    }

    private sealed class StaticEnvironmentProvider(IReadOnlyDictionary<string, string>? values = null) : IEnvironmentProvider
    {
        private readonly IReadOnlyDictionary<string, string> _values = values ?? new Dictionary<string, string>();

        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            _values.TryGetValue(name, out var value) ? value : defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => _values;
    }

    private sealed class ActivationProbe
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly ManualResetEventSlim _constructorEntered = new();
        private int _gateEnabled;
        private int _constructions;
        private int _initializations;

        public ActivationProbe() { }

        public int Constructions => Volatile.Read(ref _constructions);
        public int Initializations => Volatile.Read(ref _initializations);
        public ManualResetEventSlim ConstructorEntered => _constructorEntered;
        public void ArmGate() => Volatile.Write(ref _gateEnabled, 1);
        public void ReleaseConstructor() => _release.Set();
        public void Construct()
        {
            Interlocked.Increment(ref _constructions);
            if (Interlocked.Exchange(ref _gateEnabled, 0) == 1)
            {
                _constructorEntered.Set();
                if (!_release.Wait(BarrierTimeout))
                {
                    throw new TimeoutException("Concurrent wrapper construction gate timed out.");
                }
            }
        }

        public void Initialize() => Interlocked.Increment(ref _initializations);
    }

    private sealed class ProviderLifetimeProbe
    {
        private int _typeConstructions;
        private int _factoryInvocations;
        private int _typeInitializations;

        public ProviderLifetimeProbe() { }

        public int TypeConstructions => Volatile.Read(ref _typeConstructions);
        public int FactoryInvocations => Volatile.Read(ref _factoryInvocations);
        public int TypeInitializations => Volatile.Read(ref _typeInitializations);
        public void TypeConstructed() => Interlocked.Increment(ref _typeConstructions);
        public void FactoryInvoked() => Interlocked.Increment(ref _factoryInvocations);
        public void TypeInitialized() => Interlocked.Increment(ref _typeInitializations);
    }

    private sealed class OptionalActivationDependency { }
    private sealed class MissingActivationDependency { }
    private sealed class ThrowingActivationDependency { }
    private sealed class AmbiguousDependencyA { public AmbiguousDependencyA() { } }
    private sealed class AmbiguousDependencyB { public AmbiguousDependencyB() { } }
    private sealed class AmbiguousDependencyC { public AmbiguousDependencyC() { } }
    private sealed class AmbiguousDependencyD { public AmbiguousDependencyD() { } }

    private sealed class RequiredOptions
    {
        [Required]
        public string? Name { get; init; }
    }

    [ConfigKey("Explicit:ConcurrentActivation", root: true)]
    private sealed class ConcurrentActivationConfig : Config<string>
    {
        private readonly ActivationProbe _probe;

        public ConcurrentActivationConfig(ActivationProbe probe)
        {
            _probe = probe;
            _probe.Construct();
        }

        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialize();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:CallerOwned", root: true)]
    private sealed class CallerOwnedConfig : Config<string>
    {
        public CallerOwnedConfig() { }
        internal int InitCalls { get; private set; }
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            InitCalls++;
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:ReplacedCaller", root: true)]
    private sealed class ReplacedCallerConfig : Config<string>
    {
        public ReplacedCallerConfig() { }
        internal int InitCalls { get; private set; }
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            InitCalls++;
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:CloneInstance", root: true)]
    private sealed class ClonedInstanceConfig : Config<string>
    {
        public ClonedInstanceConfig() { }
        internal int InitCalls { get; private set; }
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            InitCalls++;
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:CloneFactory", root: true)]
    private sealed class ClonedFactoryConfig : Config<string>
    {
        public ClonedFactoryConfig(ProviderLifetimeProbe probe)
        {
            Probe = probe;
            probe.FactoryInvoked();
        }

        public ProviderLifetimeProbe Probe { get; }
    }

    [ConfigKey("Explicit:CloneType", root: true)]
    private sealed class ClonedTypeConfig : Config<string>
    {
        private readonly ProviderLifetimeProbe _probe;
        public ClonedTypeConfig(ProviderLifetimeProbe probe)
        {
            _probe = probe;
            Probe = probe;
            probe.TypeConstructed();
        }

        public ProviderLifetimeProbe Probe { get; }
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.TypeInitialized();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:CloneOnlyFirst", root: true)]
    private sealed class OnlyFirstCloneConfig : Config<string>
    {
        public OnlyFirstCloneConfig() { }
    }

    [ConfigKey("Explicit:OptionalDependency", root: true)]
    private sealed class OptionalDependencyConfig : Config<string>
    {
        public OptionalDependencyConfig(OptionalActivationDependency? dependency = null) => Dependency = dependency;
        public OptionalActivationDependency? Dependency { get; }
    }

    [ConfigKey("Explicit:MissingDependency", root: true)]
    private sealed class MissingDependencyConfig : Config<string>
    {
        public MissingDependencyConfig(MissingActivationDependency dependency) => _ = dependency;
    }

    [ConfigKey("Explicit:AmbiguousDependency", root: true)]
    private sealed class AmbiguousDependencyConfig : Config<string>
    {
        public AmbiguousDependencyConfig(AmbiguousDependencyA first, AmbiguousDependencyB second) => _ = (first, second);
        public AmbiguousDependencyConfig(AmbiguousDependencyC first, AmbiguousDependencyD second) => _ = (first, second);
    }

    [ConfigKey("Explicit:ThrowingDependency", root: true)]
    private sealed class ThrowingDependencyConfig : Config<string>
    {
        public ThrowingDependencyConfig(ThrowingActivationDependency dependency) => _ = dependency;
    }

    [ConfigKeyRequired]
    [ConfigKey("Explicit:RequiredClass", root: true)]
    private sealed class RequiredClassConfig : Config<string>
    {
        private readonly ActivationProbe _probe;
        public RequiredClassConfig(ActivationProbe probe) => _probe = probe;
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialize();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKeyRequired]
    [ConfigKey("Explicit:RequiredStruct", root: true)]
    private sealed class RequiredStructConfig : ConfigStruct<int>
    {
        public RequiredStructConfig(ActivationProbe probe)
        {
            probe.Construct();
        }
    }

    [ConfigKey("Explicit:InvalidObject", root: true)]
    private sealed class InvalidObjectConfig : Config<RequiredOptions>
    {
        private readonly ActivationProbe _probe;
        public InvalidObjectConfig(ActivationProbe probe) => _probe = probe;
        public override RequiredOptions? DefaultValue => new();
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialize();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:InvalidScalar", root: true)]
    [ConfigValueNotEmpty]
    private sealed class InvalidScalarConfig : Config<string>
    {
        private readonly ActivationProbe _probe;
        public InvalidScalarConfig(ActivationProbe probe)
        {
            _probe = probe;
            _probe.Construct();
        }
    }

    [ConfigKey("Explicit:Conversion", root: true)]
    private sealed class ConversionConfig : ConfigStruct<int>
    {
        public ConversionConfig(ActivationProbe probe)
        {
            probe.Construct();
        }
    }

    [ConfigKey("Explicit:ManagerFailure", root: true)]
    private sealed class ManagerFailureConfig : Config<string>
    {
        private readonly ActivationProbe _probe;
        public ManagerFailureConfig(ActivationProbe probe) => _probe = probe;
        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialize();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:Cancellation", root: true)]
    private sealed class CancellationConfig : Config<string>
    {
        private readonly ActivationProbe _probe;
        public CancellationConfig(ActivationProbe probe)
        {
            _probe = probe;
            _probe.Construct();
        }
    }

    [ConfigKey("Explicit:FactoryFailure", root: true)]
    private sealed class FactoryFailureConfig : Config<string>
    {
        public FactoryFailureConfig() { }
    }

    [ConfigKey("Explicit:InitFailure", root: true)]
    private sealed class InitFailureConfig : Config<string>
    {
        private readonly ActivationProbe _probe;
        private readonly InvalidOperationException _failure;

        public InitFailureConfig(ActivationProbe probe, InvalidOperationException failure)
        {
            _probe = probe;
            _failure = failure;
            _probe.Construct();
        }

        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialize();
            throw _failure;
        }
    }
}
