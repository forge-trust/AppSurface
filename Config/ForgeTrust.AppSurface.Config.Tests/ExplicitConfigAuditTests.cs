using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ExplicitConfigAuditTests
{
    [Theory]
    [InlineData(LegacyDotPathBehavior.Strict, "Explicit.LateOptions", "StrictString")]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, "Explicit:LateOptions", "TranslatedDot")]
    public async Task FinalizedParserOptionsReachExplicitRuntimeAndAuditWithTheSameKeyObject(
        LegacyDotPathBehavior policy,
        string expectedKey,
        string expectedOrigin)
    {
        var phase = new AuditPhaseProbe();
        var manager = new KeyRecordingManager();
        var auditProvider = new AuditValueProvider("audit-safe-sentinel");
        using var host = BuildModuleHost(
            discoverTestAssembly: false,
            policy,
            phase,
            manager,
            auditProvider,
            typeof(LateOptionsConfig),
            services => services.AddAppSurfaceConfig<LateOptionsConfig>());

        Assert.Equal(0, phase.Constructions);
        Assert.Equal(0, phase.Initializations);
        Assert.Equal(0, manager.Calls);
        Assert.Equal(0, auditProvider.Calls);
        await host.StartAsync();

        var registry = host.Services.GetRequiredService<ConfigDeclarationRegistry>();
        var entry = registry.GetForConfigType(typeof(LateOptionsConfig));
        Assert.Equal(expectedKey, entry.LogicalKey.Value);
        Assert.Equal(expectedOrigin, entry.LogicalKey.InputOrigin.ToString());
        Assert.Equal("Explicit.LateOptions", entry.LogicalKey.OriginalInput);
        Assert.Equal(0, phase.Constructions);
        Assert.Equal(0, phase.Initializations);
        Assert.Equal(0, manager.Calls);
        Assert.Equal(0, auditProvider.Calls);

        var report = host.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        Assert.Equal(1, auditProvider.Calls);
        Assert.Same(entry.LogicalKey, auditProvider.LastKey);
        Assert.Equal(0, manager.Calls);
        Assert.Equal(1, phase.Constructions);
        Assert.Equal(0, phase.Initializations);
        Assert.Equal(expectedKey, Assert.Single(report.Entries).Key);

        var config = host.Services.GetRequiredService<LateOptionsConfig>();
        Assert.Equal("runtime-safe-sentinel", config.Value);
        Assert.Same(entry.LogicalKey, manager.LastKey);
        Assert.Equal(1, manager.Calls);
        Assert.Equal(2, phase.Constructions);
        Assert.Equal(1, phase.Initializations);
        await host.StopAsync();
    }

    [Fact]
    public async Task ExplicitAndDiscoveredDeclarationsProduceSafeEquivalentAuditReportsAcrossPhases()
    {
        var explicitPhase = new AuditPhaseProbe();
        var discoveredPhase = new AuditPhaseProbe();
        var explicitManager = new KeyRecordingManager();
        var discoveredManager = new KeyRecordingManager();
        var explicitProvider = new AuditValueProvider("audit-secret-sentinel");
        var discoveredProvider = new AuditValueProvider("audit-secret-sentinel");
        var explicitCaller = new AuditParityConfig();
        var discoveredCaller = new AuditParityConfig();

        using var explicitHost = BuildModuleHost(
            discoverTestAssembly: false,
            LegacyDotPathBehavior.Strict,
            explicitPhase,
            explicitManager,
            explicitProvider,
            typeof(AuditParityConfig),
            services => services.AddAppSurfaceConfig<AuditParityConfig>(),
            explicitCaller);
        using var discoveredHost = BuildModuleHost(
            discoverTestAssembly: true,
            LegacyDotPathBehavior.Strict,
            discoveredPhase,
            discoveredManager,
            discoveredProvider,
            typeof(AuditParityConfig),
            services => services.AddAppSurfaceConfig<AuditParityConfig>(),
            discoveredCaller);

        AssertRegistrationAndStartupArePassive(explicitPhase, explicitManager, explicitProvider);
        AssertRegistrationAndStartupArePassive(discoveredPhase, discoveredManager, discoveredProvider);
        await explicitHost.StartAsync();
        await discoveredHost.StartAsync();
        AssertRegistrationAndStartupArePassive(explicitPhase, explicitManager, explicitProvider);
        AssertRegistrationAndStartupArePassive(discoveredPhase, discoveredManager, discoveredProvider);

        var explicitRegistry = explicitHost.Services.GetRequiredService<ConfigDeclarationRegistry>();
        var discoveredRegistry = discoveredHost.Services.GetRequiredService<ConfigDeclarationRegistry>();
        var explicitEntry = explicitRegistry.GetForConfigType(typeof(AuditParityConfig));
        var discoveredEntry = discoveredRegistry.GetForConfigType(typeof(AuditParityConfig));
        Assert.Equal("Explicit:AuditPassword", explicitEntry.LogicalKey.Value);
        Assert.Equal(explicitEntry.LogicalKey, discoveredEntry.LogicalKey);
        Assert.NotSame(explicitEntry.LogicalKey, discoveredEntry.LogicalKey);

        var explicitReport = explicitHost.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var discoveredReport = discoveredHost.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var explicitAuditEntry = Assert.Single(explicitReport.Entries);
        var discoveredAuditEntry = Assert.Single(discoveredReport.Entries);

        Assert.Equal(explicitAuditEntry.Key, discoveredAuditEntry.Key);
        Assert.Equal(explicitAuditEntry.ConfigPath, discoveredAuditEntry.ConfigPath);
        Assert.Equal(explicitAuditEntry.State, discoveredAuditEntry.State);
        Assert.Equal(explicitAuditEntry.DisplayValue, discoveredAuditEntry.DisplayValue);
        Assert.True(explicitAuditEntry.IsRedacted);
        Assert.True(discoveredAuditEntry.IsRedacted);
        Assert.Equal(explicitAuditEntry.Sources.Select(source => (source.ProviderName, source.Role)),
            discoveredAuditEntry.Sources.Select(source => (source.ProviderName, source.Role)));
        Assert.DoesNotContain("audit-secret-sentinel", JsonSerializer.Serialize(explicitReport), StringComparison.Ordinal);
        Assert.DoesNotContain("audit-secret-sentinel", JsonSerializer.Serialize(discoveredReport), StringComparison.Ordinal);
        var explicitText = explicitHost.Services.GetRequiredService<ConfigAuditTextRenderer>().Render(explicitReport);
        var discoveredText = discoveredHost.Services.GetRequiredService<ConfigAuditTextRenderer>().Render(discoveredReport);
        Assert.Contains("[redacted]", explicitText, StringComparison.Ordinal);
        Assert.Contains("[redacted]", discoveredText, StringComparison.Ordinal);
        Assert.DoesNotContain("audit-secret-sentinel", explicitText, StringComparison.Ordinal);
        Assert.DoesNotContain("audit-secret-sentinel", discoveredText, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", JsonSerializer.Serialize(explicitReport), StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", JsonSerializer.Serialize(discoveredReport), StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", explicitText, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", discoveredText, StringComparison.Ordinal);
        Assert.Same(explicitEntry.LogicalKey, explicitProvider.LastKey);
        Assert.Same(discoveredEntry.LogicalKey, discoveredProvider.LastKey);
        Assert.Equal(1, explicitProvider.Calls);
        Assert.Equal(1, discoveredProvider.Calls);
        Assert.Equal(1, explicitPhase.Constructions);
        Assert.Equal(1, discoveredPhase.Constructions);
        Assert.Equal(0, explicitPhase.Initializations);
        Assert.Equal(0, discoveredPhase.Initializations);
        Assert.Equal(0, explicitManager.Calls);
        Assert.Equal(0, discoveredManager.Calls);
        Assert.Same(explicitCaller, explicitHost.Services.GetRequiredService<AuditParityConfig>());
        Assert.Same(discoveredCaller, discoveredHost.Services.GetRequiredService<AuditParityConfig>());
        Assert.Equal("caller-owned-secret-sentinel", explicitCaller.CallerOwnedState);
        Assert.Equal("caller-owned-secret-sentinel", discoveredCaller.CallerOwnedState);
        Assert.Equal(0, explicitCaller.InitCalls);
        Assert.Equal(0, discoveredCaller.InitCalls);
        Assert.Equal(0, explicitCaller.InspectionCalls);
        Assert.Equal(0, discoveredCaller.InspectionCalls);

        Assert.Same(explicitCaller, explicitHost.Services.GetRequiredService<AuditParityConfig>());
        Assert.Same(discoveredCaller, discoveredHost.Services.GetRequiredService<AuditParityConfig>());
        Assert.Equal(1, explicitPhase.Constructions);
        Assert.Equal(1, discoveredPhase.Constructions);
        Assert.Equal(0, explicitPhase.Initializations);
        Assert.Equal(0, discoveredPhase.Initializations);
        Assert.Equal(0, explicitManager.Calls);
        Assert.Equal(0, discoveredManager.Calls);
        Assert.Equal(0, explicitCaller.InitCalls);
        Assert.Equal(0, discoveredCaller.InitCalls);
        Assert.Equal(0, explicitCaller.InspectionCalls);
        Assert.Equal(0, discoveredCaller.InspectionCalls);

        explicitProvider.Failure = new IOException("audit-error-secret-sentinel");
        discoveredProvider.Failure = new IOException("audit-error-secret-sentinel");
        var explicitErrorReport = explicitHost.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var discoveredErrorReport = discoveredHost.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var explicitErrorEntry = Assert.Single(explicitErrorReport.Entries);
        var discoveredErrorEntry = Assert.Single(discoveredErrorReport.Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, explicitErrorEntry.State);
        Assert.Equal(ConfigAuditEntryState.Invalid, discoveredErrorEntry.State);
        Assert.Contains(explicitErrorEntry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-get-value-threw");
        Assert.Contains(discoveredErrorEntry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-get-value-threw");
        var explicitErrorText = explicitHost.Services.GetRequiredService<ConfigAuditTextRenderer>().Render(explicitErrorReport);
        var discoveredErrorText = discoveredHost.Services.GetRequiredService<ConfigAuditTextRenderer>().Render(discoveredErrorReport);
        Assert.DoesNotContain("audit-error-secret-sentinel", JsonSerializer.Serialize(explicitErrorReport), StringComparison.Ordinal);
        Assert.DoesNotContain("audit-error-secret-sentinel", JsonSerializer.Serialize(discoveredErrorReport), StringComparison.Ordinal);
        Assert.DoesNotContain("audit-error-secret-sentinel", explicitErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("audit-error-secret-sentinel", discoveredErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", explicitErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned-secret-sentinel", discoveredErrorText, StringComparison.Ordinal);

        await explicitHost.StopAsync();
        await discoveredHost.StopAsync();
    }

    [Fact]
    public void ProviderErrorTextAndResolutionLogsDoNotExposeSentinelValuesOrExceptionMessages()
    {
        const string valueSentinel = "log-value-secret-sentinel";
        const string errorSentinel = "provider-error-secret-sentinel";
        var logger = new CapturingLogger<DefaultConfigManager>();
        var environment = new AuditEnvironmentProvider();
        var manager = new DefaultConfigManager(environment, [new AuditValueProvider(valueSentinel)], logger);
        var key = AppSurfaceConfigKey.Parse("Explicit:ApiKey");

        Assert.Equal(valueSentinel, manager.GetValue<string>("Production", key));
        Assert.DoesNotContain(valueSentinel, string.Join('\n', logger.Messages), StringComparison.Ordinal);

        var errorManager = new DefaultConfigManager(environment, [new AuditFailureProvider(errorSentinel)], logger);
        var exception = Assert.Throws<ConfigurationResolutionException>(
            () => errorManager.GetValue<string>("Production", key));

        Assert.Equal("config-provider-failed", exception.Diagnostic.Code);
        Assert.DoesNotContain(errorSentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(errorSentinel, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(errorSentinel, string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }

    private static IHost BuildModuleHost(
        bool discoverTestAssembly,
        LegacyDotPathBehavior policy,
        AuditPhaseProbe phase,
        KeyRecordingManager manager,
        AuditValueProvider auditProvider,
        Type configType,
        Action<IServiceCollection> explicitRegistration,
        AuditParityConfig? callerOwnedInstance = null)
    {
        var context = new StartupContext([], new NoHostModule())
        {
            OverrideEntryPointAssembly = discoverTestAssembly
                ? typeof(ExplicitConfigAuditTests).Assembly
                : typeof(NoHostModule).Assembly
        };

        return new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                new AppSurfaceConfigModule().ConfigureServices(context, services);
                if (callerOwnedInstance is not null)
                {
                    services.AddSingleton(callerOwnedInstance);
                }

                context.CustomRegistrations[0](services);
                if (!discoverTestAssembly)
                {
                    explicitRegistration(services);
                }

                // Keep only this path's raw typed declaration after the module's assembly callback.
                foreach (var descriptor in services.Where(item =>
                             item.ServiceType == typeof(ConfigAuditRawDeclaration)).ToArray())
                {
                    if (descriptor.ImplementationInstance is ConfigAuditRawDeclaration declaration
                        && declaration.ConfigType == configType)
                    {
                        continue;
                    }

                    services.Remove(descriptor);
                }

                services.AddSingleton(phase);
                services.AddSingleton<IEnvironmentProvider>(new AuditEnvironmentProvider());
                services.Replace(ServiceDescriptor.Singleton<IConfigManager>(manager));
                services.Replace(ServiceDescriptor.Singleton<IEnvironmentConfigProvider>(new AuditEnvironmentProvider()));
                services.RemoveAll<IConfigProvider>();
                services.AddSingleton<IConfigProvider>(auditProvider);
                services.AddLogging();
                services.PostConfigure<AppSurfaceConfigKeyOptions>(options => options.LegacyDotPathBehavior = policy);
            })
            .Build();
    }

    private static void AssertRegistrationAndStartupArePassive(
        AuditPhaseProbe phase,
        KeyRecordingManager manager,
        AuditValueProvider provider)
    {
        Assert.Equal(0, phase.Constructions);
        Assert.Equal(0, phase.Initializations);
        Assert.Equal(0, manager.Calls);
        Assert.Equal(0, provider.Calls);
    }

    private sealed class AuditPhaseProbe
    {
        private int _constructions;
        private int _initializations;
        public int Constructions => Volatile.Read(ref _constructions);
        public int Initializations => Volatile.Read(ref _initializations);
        public void Constructed() => Interlocked.Increment(ref _constructions);
        public void Initialized() => Interlocked.Increment(ref _initializations);
    }

    private sealed class KeyRecordingManager : IConfigManager
    {
        private int _calls;
        private AppSurfaceConfigKey? _lastKey;
        public int Calls => Volatile.Read(ref _calls);
        public AppSurfaceConfigKey? LastKey => Volatile.Read(ref _lastKey);

        public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
        {
            Interlocked.Increment(ref _calls);
            Volatile.Write(ref _lastKey, key);
            return typeof(T) == typeof(string) ? (T)(object)"runtime-safe-sentinel" : default;
        }

        public T? GetValue<T>(string environment, string key) => GetValue<T>(environment, AppSurfaceConfigKey.Parse(key));
    }

    private sealed class AuditEnvironmentProvider : IEnvironmentConfigProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public int Priority => 0;
        public string Name => nameof(AuditEnvironmentProvider);
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => ConfigProviderValueResult<T>.Missing();
    }

    private sealed class AuditValueProvider(string value) : IConfigProvider
    {
        private int _calls;
        private AppSurfaceConfigKey? _lastKey;
        public Exception? Failure { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public AppSurfaceConfigKey? LastKey => Volatile.Read(ref _lastKey);
        public int Priority => 10;
        public string Name => nameof(AuditValueProvider);

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Interlocked.Increment(ref _calls);
            Volatile.Write(ref _lastKey, request.Key);
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (typeof(T) == typeof(string))
            {
                return ConfigProviderValueResult<T>.Found((T)(object)value);
            }

            return ConfigProviderValueResult<T>.Missing();
        }
    }

    private sealed class AuditFailureProvider(string sentinel) : IConfigProvider
    {
        public int Priority => 10;
        public string Name => nameof(AuditFailureProvider);
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => throw new IOException(sentinel);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    [ConfigKey("Explicit.LateOptions", root: true)]
    private sealed class LateOptionsConfig : Config<string>
    {
        private readonly AuditPhaseProbe _probe;
        public LateOptionsConfig(AuditPhaseProbe probe)
        {
            _probe = probe;
            _probe.Constructed();
        }

        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            _probe.Initialized();
            base.Init(manager, environment, key);
        }
    }

    [ConfigKey("Explicit:AuditPassword", root: true)]
    private sealed class AuditParityConfig : Config<string>, IConfigInspectable
    {
        private readonly AuditPhaseProbe? _probe;
        private int _initCalls;
        private int _inspectionCalls;

        public AuditParityConfig()
        {
            CallerOwnedState = "caller-owned-secret-sentinel";
        }

        public AuditParityConfig(AuditPhaseProbe probe)
        {
            _probe = probe;
            CallerOwnedState = "caller-owned-secret-sentinel";
            _probe.Constructed();
        }

        public string CallerOwnedState { get; }
        public int InitCalls => Volatile.Read(ref _initCalls);
        public int InspectionCalls => Volatile.Read(ref _inspectionCalls);

        internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
        {
            Interlocked.Increment(ref _initCalls);
            _probe?.Initialized();
            base.Init(manager, environment, key);
        }

        ConfigWrapperInspection IConfigInspectable.Inspect(
            AppSurfaceConfigKey key,
            object? value,
            ConfigAuditEntryState state)
        {
            Interlocked.Increment(ref _inspectionCalls);
            return base.Inspect(key, value, state);
        }
    }
}
