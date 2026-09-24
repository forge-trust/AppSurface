using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public class TestConfig : Config<TestConfig>
{
}

public class TestHostModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public class TrackingTestConfig : Config<TrackingTestConfig>
{
    public bool InitCalled { get; private set; }

    internal override void Init(IConfigManager configManager, IEnvironmentProvider environmentProvider, AppSurfaceConfigKey keyPath)
    {
        InitCalled = true;
        base.Init(configManager, environmentProvider, keyPath);
    }
}

public class InvalidRegisteredOptions
{
    [Required]
    public string? Name { get; init; }
}

public class InvalidRegisteredConfig : Config<InvalidRegisteredOptions>
{
}

public sealed class AuditEndpoint
{
    public string? Name { get; set; }

    public string? Password { get; set; }
}

[ConfigKey("Audit.Services", root: true)]
[ConfigAuditCollectionTraversal]
public sealed class AuditServicesConfig : Config<List<AuditEndpoint>>
{
}

[ConfigKey("Audit.OpaqueServices", root: true)]
public sealed class AuditOpaqueServicesConfig : Config<List<AuditEndpoint>>
{
}

[ConfigKey("Audit.LimitedServices", root: true)]
[ConfigAuditCollectionTraversal(MaxCollectionElements = 1)]
public sealed class AuditLimitedServicesConfig : Config<List<AuditEndpoint>>
{
}

[ConfigKey("Audit.InvalidServices", root: true)]
[ConfigAuditCollectionTraversal(MaxCollectionDepth = -1, MaxCollectionElements = -1, MaxReportNodes = 0)]
public sealed class AuditInvalidServicesConfig : Config<List<AuditEndpoint>>
{
}

[ConfigAuditCollectionTraversal(MaxCollectionElements = 1)]
public abstract class AuditInheritedServicesConfig : Config<List<AuditEndpoint>>
{
}

[ConfigKey("Audit.InheritedServices", root: true)]
public sealed class AuditInheritedChildServicesConfig : AuditInheritedServicesConfig
{
}

[ConfigKey("Audit.OverrideServices", root: true)]
[ConfigAuditCollectionTraversal(MaxCollectionElements = 2)]
public sealed class AuditOverrideServicesConfig : AuditInheritedServicesConfig
{
}

[ConfigKey("Audit.HiddenDictionary", root: true)]
[ConfigAuditCollectionTraversal(DisplayDictionaryKeys = false)]
public sealed class AuditHiddenDictionaryConfig : Config<Dictionary<string, string>>
{
}

public class AppSurfaceConfigModuleTests
{
    [Fact]
    public void CustomRegistrationTask_SupportsDirectTypedIConfigImplementations()
    {
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule())
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceConfigModuleTests).Assembly
        };
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        context.CustomRegistrations[0](services);
        services.AddSingleton(A.Fake<IConfigManager>());
        services.AddSingleton(A.Fake<IEnvironmentProvider>());
        using var provider = services.BuildServiceProvider();

        var entry = provider.GetRequiredService<ConfigDeclarationRegistry>().GetForConfigType(typeof(DirectConfig));
        var config = provider.GetRequiredService<DirectConfig>();

        Assert.Equal(typeof(object), entry.ValueType);
        Assert.Same(entry.LogicalKey, config.Key);
    }

    [ConfigKey("Lifecycle:DirectConfig", root: true)]
    private sealed class DirectConfig : IConfig
    {
        public AppSurfaceConfigKey? Key { get; private set; }

        public void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key) => Key = key;
    }

    [Fact]
    public async Task StartupAndRegistry_DoNotActivateDiscoveredWrappers()
    {
        var probe = new WrapperConstructionProbe();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                var context = new StartupContext([], new TestHostModule())
                {
                    OverrideEntryPointAssembly = typeof(AppSurfaceConfigModuleTests).Assembly
                };
                new AppSurfaceConfigModule().ConfigureServices(context, services);
                context.CustomRegistrations[0](services);
                Assert.Equal(0, probe.Activations);
                services.AddSingleton(probe);
                var environment = A.Fake<IEnvironmentProvider>();
                A.CallTo(() => environment.Environment).Returns("Production");
                services.AddSingleton(environment);
            })
            .Build();

        var registry = host.Services.GetRequiredService<ConfigDeclarationRegistry>();
        Assert.Equal(typeof(ThrowingStartupWrapper), registry.GetForConfigType(typeof(ThrowingStartupWrapper)).ConfigType);
        Assert.Equal(0, probe.Activations);
        await host.StartAsync();
        Assert.Same(registry, host.Services.GetRequiredService<ConfigDeclarationRegistry>());
        Assert.Equal(0, probe.Activations);

        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ThrowingStartupWrapper>());
        Assert.Equal(1, probe.Activations);
        await host.StopAsync();
    }

    private sealed class WrapperConstructionProbe
    {
        public int Activations { get; set; }
    }

    [ConfigKey("Lifecycle:ThrowingWrapper", root: true)]
    private sealed class ThrowingStartupWrapper : Config<string>
    {
        public ThrowingStartupWrapper(WrapperConstructionProbe probe)
        {
            probe.Activations++;
            throw new InvalidOperationException("The fixture must never activate during registry/startup validation.");
        }
    }

    [Fact]
    public void CustomRegistrationTask_RejectsOldContractMetadataBeforeEnumeratingTypes()
    {
        var services = new ServiceCollection();
        var assembly = new PreviousContractAssembly();
        var context = new StartupContext([], new TestHostModule()) { OverrideEntryPointAssembly = assembly };
        new AppSurfaceConfigModule().ConfigureServices(context, services);

        var exception = Assert.Throws<AppSurfacePackageCompatibilityException>(() =>
            context.CustomRegistrations[0](services));

        Assert.Equal("config-package-version-mismatch", exception.Code);
        Assert.False(assembly.TypesEnumerated);
    }

    [Theory]
    [InlineData("parser")]
    [InlineData("resources")]
    [InlineData("environment")]
    public async Task Startup_ValidatesFinalizedOptionsBeforeProviderTraversal(string invalidOptions)
    {
        var traversed = false;
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                var context = new StartupContext([], new TestHostModule());
                new AppSurfaceConfigModule().ConfigureServices(context, services);
                services.AddSingleton(A.Fake<IEnvironmentProvider>());
                services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Declared"));
                switch (invalidOptions)
                {
                    case "parser":
                        services.PostConfigure<AppSurfaceConfigKeyOptions>(options =>
                            options.LegacyDotPathBehavior = (LegacyDotPathBehavior)99);
                        break;
                    case "resources":
                        services.PostConfigure<ConfigResourceOptions>(options => options.MaxFileBytes = 0);
                        break;
                    case "environment":
                        services.PostConfigure<AppSurfaceEnvironmentConfigOptions>(options =>
                            options.MapKey(AppSurfaceConfigKey.Parse("Mapped"), "Declared"));
                        break;
                }

                services.AddSingleton<IHostedService>(new StartupProbe(() => traversed = true));
            })
            .Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        Assert.IsType<OptionsValidationException>(exception);
        Assert.False(traversed);
    }

    [Fact]
    public async Task Startup_AcceptsLateOptionsWithOneRegistryAndNoCircularDependencies()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddConfigAuditKey<string>("Manual.Value");
                var context = new StartupContext([], new TestHostModule());
                new AppSurfaceConfigModule().ConfigureServices(context, services);
                services.AddSingleton(A.Fake<IEnvironmentProvider>());
                services.PostConfigure<AppSurfaceConfigKeyOptions>(options =>
                    options.LegacyDotPathBehavior = LegacyDotPathBehavior.Strict);
                services.PostConfigure<AppSurfaceEnvironmentConfigOptions>(options =>
                    options.MapKey(AppSurfaceConfigKey.Parse("Manual.Value"), "Manual_Value"));
                services.PostConfigure<ConfigResourceOptions>(options => options.MaxFilesPerEnvironment = 3);
            })
            .Build();

        await host.StartAsync();

        Assert.Single(host.Services.GetServices<ConfigDeclarationRegistry>());
        Assert.Single(host.Services.GetServices<IConfigKeyInputParser>());
        Assert.Single(host.Services.GetServices<IValidateOptions<ConfigDeclarationStartupOptions>>());
        Assert.Equal("Manual.Value", Assert.Single(host.Services
            .GetRequiredService<ConfigDeclarationRegistry>().Keys).Value);
        Assert.Equal(3, host.Services.GetRequiredService<IOptions<ConfigResourceOptions>>().Value.MaxFilesPerEnvironment);
        await host.StopAsync();
    }

    private sealed class PreviousContractAssembly : Assembly
    {
        public bool TypesEnumerated { get; private set; }
        public override bool IsDynamic => false;
        public override AssemblyName GetName() => new("PreviousProviderFixture");
        public override AssemblyName[] GetReferencedAssemblies() =>
            [new("ForgeTrust.AppSurface.Config") { Version = new Version(0, 1, 0, 0) }];

        public override IEnumerable<TypeInfo> DefinedTypes
        {
            get
            {
                TypesEnumerated = true;
                throw new InvalidOperationException("An incompatible assembly must not be scanned.");
            }
        }
    }

    private sealed class StartupProbe(Action onStart) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void ConfigureServices_AddsRequiredServices()
    {
        var services = new ServiceCollection();
        var rootModule = A.Fake<IAppSurfaceHostModule>();
        var context = new StartupContext([], rootModule);
        var module = new AppSurfaceConfigModule();

        module.ConfigureServices(context, services);

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigManager) && d.ImplementationType == typeof(DefaultConfigManager));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigKeyInputParser)
                 && d.ImplementationType == typeof(ConfigKeyInputParser));
        Assert.Contains(services, d => d.ServiceType == typeof(ConfigDeclarationRegistry)
                                       && d.ImplementationType == typeof(ConfigDeclarationRegistry));
        Assert.Contains(services, d => d.ServiceType == typeof(IValidateOptions<ConfigDeclarationStartupOptions>)
                                       && d.ImplementationType == typeof(ConfigDeclarationStartupValidator));
        Assert.Contains(services, d => d.ServiceType == typeof(IValidateOptions<ConfigResourceOptions>));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IEnvironmentConfigProvider)
                 && d.ImplementationType == typeof(EnvironmentConfigProvider));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigFileLocationProvider)
                 && d.ImplementationType == typeof(DefaultConfigFileLocationProvider));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IConfigProvider) && d.ImplementationType == typeof(FileBasedConfigProvider));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigDiagnosticsCommandRunner)
                 && d.ImplementationType == typeof(ConfigDiagnosticsCommandRunner));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigAuditDiffCommandRunner)
                 && d.ImplementationType == typeof(ConfigAuditDiffCommandRunner));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigAuditReportDiffer)
                 && d.ImplementationType == typeof(ConfigAuditReportDiffer));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigAuditDiffTextRenderer)
                 && d.ImplementationType == typeof(ConfigAuditDiffTextRenderer));

        // One registration for the module's assembly scanning task
        Assert.Single(context.CustomRegistrations);
    }

    [Fact]
    public void CustomRegistrationTask_ScansAssembliesAndRegistersConfigs()
    {
        var services = new ServiceCollection();
        var rootModule = new TestHostModule();

        var context = new StartupContext([], rootModule)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceConfigModuleTests).Assembly
        };
        context.Dependencies.AddModule<TestHostModule>();

        var module = new AppSurfaceConfigModule();
        var moduleBuilder = new ModuleDependencyBuilder();
        module.RegisterDependentModules(moduleBuilder);

        module.ConfigureServices(context, services);
        var registrationTask = context.CustomRegistrations[0];

        // Invoke the task
        registrationTask(services);

        // Verify it registered TestConfig and TrackingTestConfig
        Assert.Contains(services, d => d.ServiceType == typeof(TestConfig));
        Assert.Contains(services, d => d.ServiceType == typeof(TrackingTestConfig));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ConfigAuditRawDeclaration)
                 && d.ImplementationInstance is ConfigAuditRawDeclaration declaration
                 && declaration.ConfigType == typeof(TrackingTestConfig)
                 && declaration.IsAttributeDeclaration);

        // Test the factory activation
        var configManager = A.Fake<IConfigManager>();
        var envProvider = A.Fake<IEnvironmentProvider>();
        services.AddSingleton(configManager);
        services.AddSingleton(envProvider);
        services.AddSingleton(A.Fake<ILogger<DefaultConfigManager>>());

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<TrackingTestConfig>();

        Assert.NotNull(resolved);
        Assert.True(resolved.InitCalled);

        // Verify Init was called via ConfigManager too
        A.CallTo(() => configManager.GetValue<TrackingTestConfig>(A<string>._, A<AppSurfaceConfigKey>._))
            .MustHaveHappened();
    }

    [Fact]
    public void CustomRegistrationTask_InvalidConfigThrowsDuringServiceActivation()
    {
        var services = new ServiceCollection();
        var rootModule = new TestHostModule();

        var context = new StartupContext([], rootModule)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceConfigModuleTests).Assembly
        };
        context.Dependencies.AddModule<TestHostModule>();

        var module = new AppSurfaceConfigModule();
        module.ConfigureServices(context, services);
        context.CustomRegistrations[0](services);

        var configManager = A.Fake<IConfigManager>();
        var envProvider = A.Fake<IEnvironmentProvider>();
        services.AddSingleton(configManager);
        services.AddSingleton(envProvider);
        services.AddSingleton(A.Fake<ILogger<DefaultConfigManager>>());

        A.CallTo(() => envProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<InvalidRegisteredOptions>(A<string>._, A<AppSurfaceConfigKey>._))
            .Returns(new InvalidRegisteredOptions());

        var sp = services.BuildServiceProvider();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            sp.GetRequiredService<InvalidRegisteredConfig>());

        Assert.Equal("InvalidRegisteredConfig", exception.Key);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Name"]));
    }

    [Fact]
    public void CustomRegistrationTask_AppliesWrapperTraversalAttributeOptions()
    {
        var reporter = CreateReporter(
            new Dictionary<string, object?>
            {
                ["Audit:Services"] = CreateEndpoints("billing", "search"),
                ["Audit:OpaqueServices"] = CreateEndpoints("opaque"),
                ["Audit:LimitedServices"] = CreateEndpoints("first", "second"),
                ["Audit:InvalidServices"] = CreateEndpoints("first", "second"),
                ["Audit:InheritedServices"] = CreateEndpoints("first", "second"),
                ["Audit:OverrideServices"] = CreateEndpoints("first", "second")
            });

        var report = reporter.GetReport("Production");

        var services = AssertEntry(report, "Audit:Services", ConfigAuditEntryState.Resolved);
        Assert.Equal(["Audit:Services[0]", "Audit:Services[1]"], services.Children.Select(child => child.Key));
        Assert.True(services.Children[0].Children.Single(child => child.Key == "Audit:Services[0].Password").IsRedacted);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("billing-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("search-secret", serialized, StringComparison.Ordinal);

        Assert.Empty(AssertEntry(report, "Audit:OpaqueServices", ConfigAuditEntryState.Resolved).Children);

        var limited = AssertEntry(report, "Audit:LimitedServices", ConfigAuditEntryState.Resolved);
        Assert.Single(limited.Children);
        Assert.Contains(limited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        var invalid = AssertEntry(report, "Audit:InvalidServices", ConfigAuditEntryState.Resolved);
        Assert.Equal(2, invalid.Children.Count);
        Assert.Equal(3, invalid.Diagnostics.Count(diagnostic => diagnostic.Code == "config-audit-options-invalid"));

        var inherited = AssertEntry(report, "Audit:InheritedServices", ConfigAuditEntryState.Resolved);
        Assert.Single(inherited.Children);
        Assert.Contains(inherited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        var overridden = AssertEntry(report, "Audit:OverrideServices", ConfigAuditEntryState.Resolved);
        Assert.Equal(["Audit:OverrideServices[0]", "Audit:OverrideServices[1]"], overridden.Children.Select(child => child.Key));
        Assert.DoesNotContain(overridden.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");
    }

    [Fact]
    public void CustomRegistrationTask_ManualDefaultValuedOptionsOverrideWrapperAttribute()
    {
        var reporter = CreateReporter(
            new Dictionary<string, object?>
            {
                ["Audit:HiddenDictionary"] = new Dictionary<string, string>
                {
                    ["public-name"] = "visible"
                }
            },
            services => services.AddConfigAuditKey<Dictionary<string, string>>(
                "Audit.HiddenDictionary",
                options => options.DisplayDictionaryKeys = true));

        var report = reporter.GetReport("Production");

        var entry = AssertEntry(report, "Audit:HiddenDictionary", ConfigAuditEntryState.Resolved);
        var child = Assert.Single(entry.Children);
        Assert.Equal("Audit:HiddenDictionary[\"public-name\"]", child.Key);
        Assert.Equal("public-name", child.Element?.KeyLabel);
        Assert.False(child.Element?.IsKeyRedacted);
    }

    [Fact]
    public void CustomRegistrationTask_ManualSensitivityPreservesWrapperTraversalMetadata()
    {
        var reporter = CreateReporter(
            new Dictionary<string, object?>
            {
                ["Audit:Services"] = CreateEndpoints("billing")
            },
            services => services.AddConfigAuditKey<List<AuditEndpoint>>(
                "Audit.Services",
                options => options.Sensitivity = ConfigAuditSensitivity.Sensitive));

        var report = reporter.GetReport("Production");

        var entry = AssertEntry(report, "Audit:Services", ConfigAuditEntryState.Resolved);
        var item = Assert.Single(entry.Children);
        Assert.Equal("Audit:Services[0]", item.Key);
        Assert.Equal("[redacted]", item.Children.Single(child => child.Key == "Audit:Services[0].Name").DisplayValue);
        Assert.True(item.Children.Single(child => child.Key == "Audit:Services[0].Name").IsRedacted);
    }

    private static IConfigAuditReporter CreateReporter(
        IReadOnlyDictionary<string, object?> values,
        Action<IServiceCollection>? afterDiscovery = null)
    {
        var discoveryServices = new ServiceCollection();
        var rootModule = new TestHostModule();
        var context = new StartupContext([], rootModule)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceConfigModuleTests).Assembly
        };
        context.Dependencies.AddModule<TestHostModule>();

        var module = new AppSurfaceConfigModule();
        module.ConfigureServices(context, discoveryServices);
        context.CustomRegistrations[0](discoveryServices);
        afterDiscovery?.Invoke(discoveryServices);

        var targetKeys = values.Keys.ToHashSet(StringComparer.Ordinal);
        using var discoveryProvider = discoveryServices.BuildServiceProvider();
        var discoveredEntries = discoveryProvider
            .GetRequiredService<ConfigDeclarationRegistry>()
            .Entries
            .Where(entry => targetKeys.Contains(entry.Key))
            .ToList();

        var services = new ServiceCollection();
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        services.AddSingleton(environment);
        services.AddSingleton<IEnvironmentConfigProvider, EnvironmentConfigProvider>();
        services.AddSingleton<IConfigProvider>(new DictionaryConfigProvider(values));
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddSingleton<ConfigAuditTextRenderer>();
        foreach (var entry in discoveredEntries)
        {
            services.AddSingleton(entry);
        }

        return services.BuildServiceProvider().GetRequiredService<IConfigAuditReporter>();
    }

    private static List<AuditEndpoint> CreateEndpoints(params string[] names) =>
        names.Select(name => new AuditEndpoint { Name = name, Password = $"{name}-secret" }).ToList();

    private static ConfigAuditEntry AssertEntry(
        ConfigAuditReport report,
        string key,
        ConfigAuditEntryState state)
    {
        var entry = Assert.Single(report.Entries, entry => entry.Key == key);
        Assert.Equal(state, entry.State);
        return entry;
    }

    private sealed class DictionaryConfigProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly IReadOnlyDictionary<string, object?> _values;

        public DictionaryConfigProvider(IReadOnlyDictionary<string, object?> values)
        {
            _values = values;
        }

        public int Priority => 20;

        public string Name => nameof(DictionaryConfigProvider);

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            _values.TryGetValue(request.Key.Value, out var value) && value is T typed
                ? ConfigProviderValueResult<T>.Found(typed)
                : ConfigProviderValueResult<T>.Missing();

        public ConfigValueResolution Resolve(
            ConfigProviderRequest request,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            var key = request.Key;
            if (!_values.TryGetValue(key.Value, out var value))
            {
                return ConfigValueResolution.Missing(key);
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                value,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = key.Value,
                        AppliedToPath = key.Value,
                        Role = role
                    }
                ],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

    }
}
