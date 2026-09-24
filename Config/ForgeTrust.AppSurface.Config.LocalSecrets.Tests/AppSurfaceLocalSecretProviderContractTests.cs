using ForgeTrust.AppSurface.Config.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

/// <summary>Runs shared conformance cases against the public provider with isolated durable file records.</summary>
/// <remarks>
/// LocalSecrets owns one source layer, so ordered-layer override and ordered-layer case collision belong to file/config
/// projection. It preserves literal underscores, so the lossy environment-codec unrepresentable row does not apply.
/// Terminal rows retain canonical arrangements and use the package's documented diagnostic codes.
/// </remarks>
public sealed class AppSurfaceLocalSecretProviderContractTests
{
    public static IEnumerable<object[]> Cases => ConfigProviderContractCases.All
        .Where(row => row.Scenario is not (ConfigProviderContractScenario.OrderedOverride or
            ConfigProviderContractScenario.OrderedCaseCollision or ConfigProviderContractScenario.Unrepresentable))
        .Select(row => new object[] { row.Id });

    [Theory]
    [MemberData(nameof(Cases))]
    public void PublicProvider_ShouldPassSharedContract(string id)
    {
        var canonical = ConfigProviderContractCases.All.Single(row => row.Id == id);
        var row = new ConfigProviderContractCase(canonical.Id, canonical.Scenario, canonical.Key,
            canonical.Scenario switch
            {
                ConfigProviderContractScenario.SameLayerCollision => "config-key-collision",
                ConfigProviderContractScenario.TerminalPrecedence => "local-secret-provider-threw",
                _ => canonical.TerminalCode
            });
        ConfigProviderContractAssert.Case(new Harness(), row);
    }

    private sealed class Harness : IConfigProviderContractHarness
    {
        public ConfigProviderContractSession Create(ConfigProviderContractCase row)
        {
            var directory = Path.Combine(Path.GetTempPath(), "local-contract-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "records.json");
            var store = new FileAppSurfaceLocalSecretStore(path);
            var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
            var options = new AppSurfaceLocalSecretsOptions { ApplicationName = "ContractApp" };
            if (row.Scenario == ConfigProviderContractScenario.PrefixBoundary) options.KeyPrefix = "shared";
            var expected = "winning-marker";
            var distinct = "distinct-marker";
            var environment = new ContractEnvironmentProvider();
            var lower = new LowerProvider();
            IAppSurfaceLocalSecretStore selected = store;
            switch (row.Scenario)
            {
                case ConfigProviderContractScenario.Missing: break;
                case ConfigProviderContractScenario.MissingToLowerFallback:
                    lower.Value = expected;
                    break;
                case ConfigProviderContractScenario.TerminalPrecedence:
                    selected = new ThrowingStore();
                    lower.Value = "lower-marker";
                    break;
                case ConfigProviderContractScenario.SameLayerCollision:
                    // Seed exact native records; public Set intentionally preserves an existing case spelling.
                    var records = new Dictionary<string, object>();
                    foreach (var spelling in new[] { row.Key.Value, row.Key.Value.ToLowerInvariant() })
                    {
                        var identity = normalizer.Normalize(options.ApplicationName, "Development", null, spelling).Identity!;
                        records[identity.StorageName] = new { identity.ApplicationName, identity.Environment, identity.KeyPrefix, Key = spelling, Value = expected };
                    }
                    DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(path,
                        System.Text.Json.JsonSerializer.Serialize(records));
                    lower.Value = "lower-marker";
                    break;
                default:
                    Seed(row.Key.Value, row.Scenario == ConfigProviderContractScenario.EnvironmentPrecedence ? "lower-marker" : expected);
                    if (row.Scenario == ConfigProviderContractScenario.HyphenDistinction) Seed("Payments:Api:Key", distinct);
                    if (row.Scenario == ConfigProviderContractScenario.DottedSegment) Seed("Logging:LogLevel:Microsoft:Hosting:Lifetime", distinct);
                    if (row.Scenario == ConfigProviderContractScenario.EnvironmentPrecedence) environment.Value = expected;
                    break;
            }
            var provider = new AppSurfaceLocalSecretProvider(Options.Create(options), selected, normalizer);
            var manager = new DefaultConfigManager(environment, [provider, lower], NullLogger<DefaultConfigManager>.Instance);
            return new ConfigProviderContractSession(manager, "Development", expected, distinct, () =>
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                if (row.TerminalCode is not null) Assert.Equal(0, lower.Reads);
                if (row.Scenario == ConfigProviderContractScenario.MissingToLowerFallback) Assert.Equal(2, lower.Reads);
            });

            void Seed(string key, string value) => Assert.Equal(LocalSecretResultStatus.Found,
                store.Set(normalizer.Normalize(options.ApplicationName, "Development", options.KeyPrefix, key).Identity!, value).Status);
        }
    }

    private sealed class LowerProvider : IConfigProvider
    {
        public int Priority => 1;
        public string Name => "LowerContractProvider";
        public string? Value { get; set; }
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            return Value is T value ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
        }
    }

    private sealed class ThrowingStore : IAppSurfaceLocalSecretStore
    {
        public string Name => "IsolatedUnavailableStore";
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => throw new IOException("isolated failure");
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => throw new NotSupportedException();
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => throw new NotSupportedException();
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => throw new NotSupportedException();
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => throw new NotSupportedException();
    }

    private sealed class ContractEnvironmentProvider : IEnvironmentConfigProvider
    {
        public string? Value { get; set; }
        public int Priority => 100;
        public string Name => "ContractEnvironment";
        public string Environment => "Development";
        public bool IsDevelopment => true;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            request.Key.Equals(AppSurfaceConfigKey.Parse("Payments:ApiKey")) && Value is T value
                ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
    }
}
