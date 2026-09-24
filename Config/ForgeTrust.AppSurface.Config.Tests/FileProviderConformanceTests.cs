using ForgeTrust.AppSurface.Config.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class FileProviderConformanceTests
{
    [Fact]
    public void PublicConformanceCases_UseRealFileAndManagerResolution()
    {
        var scenarios = new[]
        {
            ConfigProviderContractScenario.CaseIdentity, ConfigProviderContractScenario.DottedSegment,
            ConfigProviderContractScenario.HyphenDistinction, ConfigProviderContractScenario.Missing,
            ConfigProviderContractScenario.SameLayerCollision, ConfigProviderContractScenario.OrderedOverride,
            ConfigProviderContractScenario.OrderedCaseCollision, ConfigProviderContractScenario.EnvironmentPrecedence,
            ConfigProviderContractScenario.PrefixBoundary, ConfigProviderContractScenario.LegacyTranslation,
            ConfigProviderContractScenario.ConcurrentRepeatability, ConfigProviderContractScenario.MissingToLowerFallback,
            ConfigProviderContractScenario.Unrepresentable
        };
        var harness = new FileHarness();
        foreach (var contractCase in ConfigProviderContractCases.All.Where(item => scenarios.Contains(item.Scenario)))
        {
            ConfigProviderContractAssert.Case(harness, contractCase);
        }
    }

    private sealed class FileHarness : IConfigProviderContractHarness
    {
        public ConfigProviderContractSession Create(ConfigProviderContractCase contractCase)
        {
            var directory = Path.Combine(Path.GetTempPath(), "appsurface-file-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var json = contractCase.Scenario switch
                {
                    ConfigProviderContractScenario.Missing or ConfigProviderContractScenario.MissingToLowerFallback => "{}",
                    ConfigProviderContractScenario.Unrepresentable => """{"Payments":{"Invalid:Segment":"primary"}}""",
                    ConfigProviderContractScenario.SameLayerCollision => """{"Payments":{"ApiKey":"primary","apikey":"primary"}}""",
                    ConfigProviderContractScenario.DottedSegment => """{"Logging":{"LogLevel":{"Microsoft.Hosting.Lifetime":"primary"}}}""",
                    ConfigProviderContractScenario.HyphenDistinction => """{"Payments":{"Api-Key":"primary","Api":{"Key":"distinct"}}}""",
                    ConfigProviderContractScenario.OrderedOverride or ConfigProviderContractScenario.OrderedCaseCollision
                        or ConfigProviderContractScenario.EnvironmentPrecedence => """{"Payments":{"ApiKey":"lower"}}""",
                    _ => """{"Payments":{"ApiKey":"primary"}}"""
                };
                File.WriteAllText(Path.Combine(directory, "appsettings.json"), json);
                if (contractCase.Scenario == ConfigProviderContractScenario.OrderedOverride)
                {
                    File.WriteAllText(Path.Combine(directory, "config_z.json"), """{"Payments":{"ApiKey":"primary"}}""");
                }
                if (contractCase.Scenario == ConfigProviderContractScenario.OrderedCaseCollision)
                {
                    File.WriteAllText(Path.Combine(directory, "config_z.json"), """{"payments":{"apikey":"primary"}}""");
                }
                var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                if (contractCase.Scenario == ConfigProviderContractScenario.EnvironmentPrecedence)
                {
                    variables.Add("PAYMENTS__APIKEY", "primary");
                }
                var environment = new EnvironmentConfigProvider(new SnapshotEnvironment(variables));
                var file = new FileBasedConfigProvider(new FileLocation(directory), NullLogger<FileBasedConfigProvider>.Instance);
                var lower = new LowerProvider(contractCase.Scenario is ConfigProviderContractScenario.MissingToLowerFallback
                    or ConfigProviderContractScenario.Unrepresentable);
                var manager = new DefaultConfigManager(environment, [file, lower], NullLogger<DefaultConfigManager>.Instance);
                return new ConfigProviderContractSession(manager, "Production", "primary", "distinct",
                    () =>
                    {
                        Directory.Delete(directory, recursive: true);
                        if (contractCase.Scenario == ConfigProviderContractScenario.MissingToLowerFallback) Assert.Equal(2, lower.Reads);
                        if (contractCase.Scenario == ConfigProviderContractScenario.Unrepresentable) Assert.Equal(0, lower.Reads);
                    }, contractCase.Scenario == ConfigProviderContractScenario.Unrepresentable ? AppSurfaceConfigKey.Parse("Payments") : null);
            }
            catch
            {
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }
    }

    private sealed class LowerProvider(bool hasValue) : IConfigProvider
    {
        public string Name => "LowerFileContractProvider";
        public int Priority => int.MinValue;
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            return hasValue && "primary" is T value ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
        }
    }

    private sealed class FileLocation(string directory) : IConfigFileLocationProvider
    {
        public string Directory => directory;
    }

    private sealed class SnapshotEnvironment(IReadOnlyDictionary<string, string> values) : ForgeTrust.AppSurface.Core.IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            values.TryGetValue(name, out var value) ? value : defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            new Dictionary<string, string>(values, StringComparer.Ordinal);
    }
}
