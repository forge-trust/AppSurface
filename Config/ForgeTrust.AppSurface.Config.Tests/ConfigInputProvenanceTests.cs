using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigInputProvenanceTests
{
    [Theory]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, false)]
    [InlineData(LegacyDotPathBehavior.Strict, false)]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, true)]
    public void NestedAttributeKeyRetainsIdentityThroughWrapperProviderAndAudit(
        LegacyDotPathBehavior policy,
        bool typed)
    {
        var origin = typed ? ConfigKeyInputOrigin.Typed
            : policy == LegacyDotPathBehavior.Strict ? ConfigKeyInputOrigin.StrictString
            : ConfigKeyInputOrigin.TranslatedDot;
        var probe = new ProvenanceProbe();
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule())
        {
            OverrideEntryPointAssembly = typeof(ConfigInputProvenanceTests).Assembly
        };
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        context.CustomRegistrations[0](services);
        // Isolate this test's discovered wrappers from the other assembly-scanning fixtures.
        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)).ToArray())
        {
            if (descriptor.ImplementationInstance is ConfigAuditRawDeclaration declaration
                && (declaration.ConfigType == typeof(Parent.StringValue)
                    || declaration.ConfigType == typeof(Parent.StructValue)))
            {
                if (!typed)
                {
                    continue;
                }

                services.AddSingleton(declaration with
                {
                    TypedKey = ConfigKeyAttribute.GetLogicalKey(declaration.ConfigType)
                });
            }

            services.Remove(descriptor);
        }

        // This configuration is intentionally later than discovery and its captured descriptors.
        services.PostConfigure<AppSurfaceConfigKeyOptions>(options => options.LegacyDotPathBehavior = policy);
        services.AddSingleton(probe);
        var environment = new MissingEnvironment();
        services.AddSingleton<IEnvironmentProvider>(environment);
        services.Replace(ServiceDescriptor.Singleton<IEnvironmentConfigProvider>(environment));
        services.RemoveAll<IConfigProvider>();
        services.AddSingleton<IConfigProvider>(new RecordingProvider(probe));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DefaultConfigManager>>(
            NullLogger<DefaultConfigManager>.Instance);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();

        Assert.Equal(0, probe.WrapperActivations);
        Assert.Empty(probe.ProviderKeys);
        var stringKey = registry.GetForConfigType(typeof(Parent.StringValue)).LogicalKey;
        var structKey = registry.GetForConfigType(typeof(Parent.StructValue)).LogicalKey;
        Assert.Equal(origin, stringKey.InputOrigin);
        Assert.Equal(origin, structKey.InputOrigin);
        Assert.Equal(origin == ConfigKeyInputOrigin.TranslatedDot
            ? "Provenance:Parent:Service:Endpoint"
            : "Provenance.Parent:Service.Endpoint", stringKey.Value);
        Assert.Equal(typed ? null : "Provenance.Parent.Service.Endpoint", stringKey.OriginalInput);
        var strictIdentity = AppSurfaceConfigKey.Parse(stringKey.Value);
        Assert.Equal(strictIdentity, stringKey);
        Assert.Equal(strictIdentity.GetHashCode(), stringKey.GetHashCode());
        Assert.Single(new HashSet<AppSurfaceConfigKey> { strictIdentity, stringKey });
        Assert.Equal(typed ? null : "Provenance.Parent.Retry.Count", structKey.OriginalInput);

        var stringWrapper = provider.GetRequiredService<Parent.StringValue>();
        var structWrapper = provider.GetRequiredService<Parent.StructValue>();
        Assert.Equal("configured", stringWrapper.Value);
        Assert.Equal(7, structWrapper.Value);
        Assert.Same(stringKey, probe.InitializedKey);
        Assert.Equal(2, probe.WrapperActivations);
        Assert.Collection(probe.ProviderKeys,
            key => Assert.Same(stringKey, key), key => Assert.Same(structKey, key));

        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal(ConfigAuditEntryState.Resolved, entry.State));
        Assert.Equal(4, probe.WrapperActivations);
        Assert.Equal(4, probe.ProviderKeys.Count);
        Assert.All(probe.ProviderKeys, key => Assert.Same(
            key.Equals(stringKey) ? stringKey : structKey, key));
        Assert.Collection(probe.InspectedKeys,
            key => Assert.Same(structKey, key), key => Assert.Same(stringKey, key));
        Assert.All(probe.ValidationNames, name => Assert.Contains(name, new[] { stringKey.Value, structKey.Value }));
        Assert.Equal(4, probe.ValidationNames.Count);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("InputOrigin", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalInput", serialized, StringComparison.Ordinal);
    }

    [ConfigKey("Provenance.Parent", root: true)]
    private sealed class Parent
    {
        [ConfigKey("Service.Endpoint")]
        public sealed class StringValue : Config<string>, IConfigInspectable
        {
            private readonly ProvenanceProbe _probe;

            public StringValue(ProvenanceProbe probe)
            {
                _probe = probe;
                _probe.WrapperActivations++;
            }

            internal override void Init(IConfigManager manager, IEnvironmentProvider environment, AppSurfaceConfigKey key)
            {
                _probe.InitializedKey = key;
                base.Init(manager, environment, key);
            }

            ConfigWrapperInspection IConfigInspectable.Inspect(
                AppSurfaceConfigKey key, object? value, ConfigAuditEntryState state)
            {
                _probe.InspectedKeys.Add(key);
                return base.Inspect(key, value, state);
            }

            protected override IEnumerable<ValidationResult>? ValidateValue(string value, ValidationContext context)
            {
                _probe.ValidationNames.Add(context.DisplayName);
                return [];
            }
        }

        [ConfigKey("Retry.Count")]
        public sealed class StructValue : ConfigStruct<int>, IConfigInspectable
        {
            private readonly ProvenanceProbe _probe;

            public StructValue(ProvenanceProbe probe)
            {
                _probe = probe;
                _probe.WrapperActivations++;
            }

            ConfigWrapperInspection IConfigInspectable.Inspect(
                AppSurfaceConfigKey key, object? value, ConfigAuditEntryState state)
            {
                _probe.InspectedKeys.Add(key);
                return base.Inspect(key, value, state);
            }

            protected override IEnumerable<ValidationResult>? ValidateValue(int value, ValidationContext context)
            {
                _probe.ValidationNames.Add(context.DisplayName);
                return [];
            }
        }
    }

    private sealed class ProvenanceProbe
    {
        public int WrapperActivations { get; set; }
        public AppSurfaceConfigKey? InitializedKey { get; set; }
        public List<AppSurfaceConfigKey> ProviderKeys { get; } = [];
        public List<AppSurfaceConfigKey> InspectedKeys { get; } = [];
        public List<string> ValidationNames { get; } = [];
    }

    private sealed class RecordingProvider(ProvenanceProbe probe) : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 1;
        public string Name => nameof(RecordingProvider);

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            probe.ProviderKeys.Add(request.Key);
            return ConfigProviderValueResult<T>.Found((T)GetValue(typeof(T)));
        }

        public ConfigValueResolution Resolve(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role)
        {
            probe.ProviderKeys.Add(request.Key);
            return new ConfigValueResolution(request.Key, ConfigAuditEntryState.Resolved, GetValue(valueType), [], []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

        private static object GetValue(Type type) => type == typeof(string) ? "configured" : 7;
    }

    private sealed class MissingEnvironment : IEnvironmentConfigProvider, IConfigDiagnosticProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
        public int Priority => 0;
        public string Name => nameof(MissingEnvironment);
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => ConfigProviderValueResult<T>.Missing();
        public ConfigValueResolution Resolve(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role) =>
            ConfigValueResolution.Missing(request.Key);
        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }
}
