using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.Testing;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerConfigProviderContractTests
{
    [Fact]
    public void ActualProvider_Should_PassGoogleApplicableSharedContractCases()
    {
        var cases = ConfigProviderContractCases.All.Where(contractCase => contractCase.Scenario is
            ConfigProviderContractScenario.CaseIdentity or
            ConfigProviderContractScenario.DottedSegment or
            ConfigProviderContractScenario.HyphenDistinction or
            ConfigProviderContractScenario.Missing or
            ConfigProviderContractScenario.MissingToLowerFallback or
            ConfigProviderContractScenario.Unrepresentable or
            ConfigProviderContractScenario.TerminalPrecedence or
            ConfigProviderContractScenario.OrderedOverride or
            ConfigProviderContractScenario.EnvironmentPrecedence or
            ConfigProviderContractScenario.PrefixBoundary or
            ConfigProviderContractScenario.LegacyTranslation or
            ConfigProviderContractScenario.ConcurrentRepeatability).ToArray();

        Assert.Equal(12, cases.Length);
        Assert.All(cases, contractCase => ConfigProviderContractAssert.Case(new Harness(), contractCase));
    }

    [Fact]
    public void SharedUnrepresentableCaseRejectsGoogleDottedDescendantBeforeNetworkOrFallback() =>
        ConfigProviderContractAssert.Case(new Harness(), ConfigProviderContractCases.All.Single(row =>
            row.Scenario == ConfigProviderContractScenario.Unrepresentable));

    [Fact]
    public void SharedMissingCaseReachesLowerProviderAfterGoogleReturnsMissing() =>
        ConfigProviderContractAssert.Case(new Harness(), ConfigProviderContractCases.All.Single(row =>
            row.Scenario == ConfigProviderContractScenario.MissingToLowerFallback));

    [Fact]
    public void ActualProvider_Should_AcceptNativeUnderscoreKeyAndRejectConventionDot()
    {
        var client = new RecordingClient(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projects/project/secrets/shared-a_--b/versions/5"] = "underscore"
        });
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.EnableConventionResolver("A_", secretIdPrefix: "shared-", version: "5");
        });

        var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("a_:b")));

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal("underscore", result.Value);
        Assert.Equal("projects/project/secrets/shared-a_--b/versions/5", Assert.Single(client.Requested));

        var invalid = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        invalid.EnableConventionResolver("Dot.Key", secretIdPrefix: "shared-", version: "5");
        var validation = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, invalid);
        Assert.True(validation.Failed);
        Assert.Contains(validation.Failures, failure => failure.Contains("cannot be represented", StringComparison.Ordinal));
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(
        IAppSurfaceGoogleSecretManagerClient client,
        Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        configure(options);
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private sealed class Harness : IConfigProviderContractHarness
    {
        public ConfigProviderContractSession Create(ConfigProviderContractCase contractCase)
        {
            var expected = $"expected-{contractCase.Id}";
            var distinct = $"distinct-{contractCase.Id}";
            var environment = new ContractEnvironmentProvider();
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);
            var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
            AppSurfaceConfigKey? counterexample = null;
            var lower = new LowerProvider();

            switch (contractCase.Scenario)
            {
                case ConfigProviderContractScenario.CaseIdentity:
                case ConfigProviderContractScenario.OrderedOverride:
                case ConfigProviderContractScenario.LegacyTranslation:
                case ConfigProviderContractScenario.ConcurrentRepeatability:
                    Map(options, resources, contractCase.Key, "identity", expected);
                    break;
                case ConfigProviderContractScenario.DottedSegment:
                    Map(options, resources, contractCase.Key, "dotted", expected);
                    break;
                case ConfigProviderContractScenario.HyphenDistinction:
                    Map(options, resources, contractCase.Key, "hyphen", expected);
                    Map(options, resources, AppSurfaceConfigKey.Parse("Payments:Api:Key"), "nested", distinct);
                    break;
                case ConfigProviderContractScenario.TerminalPrecedence:
                    options.MapSecret(contractCase.Key, "terminal", version: "5");
                    lower.Value = distinct;
                    break;
                case ConfigProviderContractScenario.Unrepresentable:
                    counterexample = AppSurfaceConfigKey.Parse("Payments:Unsupported.Dot");
                    options.EnableConventionResolver("Payments", secretIdPrefix: "shared-", version: "5");
                    lower.Value = expected;
                    break;
                case ConfigProviderContractScenario.MissingToLowerFallback:
                    // This populated Google provider does not claim the requested key. Remote failures stay terminal.
                    Map(options, resources, AppSurfaceConfigKey.Parse("Other:Key"), "other", distinct);
                    lower.Value = expected;
                    break;
                case ConfigProviderContractScenario.EnvironmentPrecedence:
                    Map(options, resources, contractCase.Key, "environment-lower", "lower");
                    environment.Set(contractCase.Key, expected);
                    break;
                case ConfigProviderContractScenario.PrefixBoundary:
                    options.EnableConventionResolver("Payments", secretIdPrefix: "shared-", version: "5");
                    resources["projects/project/secrets/shared-payments--apikey/versions/5"] = expected;
                    break;
                case ConfigProviderContractScenario.Missing:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Google contract case: {contractCase.Scenario}");
            }

            IAppSurfaceGoogleSecretManagerClient client = contractCase.Scenario == ConfigProviderContractScenario.TerminalPrecedence
                ? new ThrowingClient()
                : new RecordingClient(resources);
            var google = CreateProvider(client, configured =>
            {
                configured.ProjectId = options.ProjectId;
                configured.DefaultVersion = options.DefaultVersion;
                foreach (var mapping in options.Mappings)
                {
                    configured.MapSecret(mapping.LogicalKey, mapping.SecretIdOrResourceName, mapping.Version);
                }
                foreach (var convention in options.Conventions)
                {
                    configured.EnableConventionResolver(convention.LogicalKeyPrefix, convention.SecretIdPrefix, convention.Version);
                }
            });
            var manager = new DefaultConfigManager(
                environment,
                [google, lower],
                NullLogger<DefaultConfigManager>.Instance);
            return new ConfigProviderContractSession(manager, environment.Environment, expected, distinct, () =>
            {
                if (contractCase.Scenario is ConfigProviderContractScenario.Unrepresentable or ConfigProviderContractScenario.TerminalPrecedence)
                    Assert.Equal(0, lower.Reads);
                if (contractCase.Scenario == ConfigProviderContractScenario.Unrepresentable)
                    Assert.Empty(((RecordingClient)client).Requested);
                if (contractCase.Scenario == ConfigProviderContractScenario.MissingToLowerFallback)
                {
                    Assert.Equal(2, lower.Reads);
                    Assert.Empty(((RecordingClient)client).Requested);
                }
            }, counterexample);
        }

        private static void Map(AppSurfaceGoogleSecretManagerOptions options, Dictionary<string, string> resources,
            AppSurfaceConfigKey key, string secretId, string value)
        {
            options.MapSecret(key, secretId, version: "5");
            resources[$"projects/project/secrets/{secretId}/versions/5"] = value;
        }
    }

    private sealed class ContractEnvironmentProvider : IEnvironmentConfigProvider
    {
        private readonly ConcurrentDictionary<AppSurfaceConfigKey, string> _values = new();
        public int Priority => -1;
        public string Name => nameof(ContractEnvironmentProvider);
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public void Set(AppSurfaceConfigKey key, string value) => _values[key] = value;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            _values.TryGetValue(request.Key, out var value) && value is T typed
                ? ConfigProviderValueResult<T>.Found(typed)
                : ConfigProviderValueResult<T>.Missing();
    }

    private sealed class RecordingClient : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly IReadOnlyDictionary<string, string> _resources;
        public RecordingClient(IReadOnlyDictionary<string, string>? resources = null)
        {
            _resources = resources ?? new Dictionary<string, string>();
        }

        public ConcurrentQueue<string> Requested { get; } = new();
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Requested.Enqueue(resourceName);
            return _resources.TryGetValue(resourceName, out var value)
                ? new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes(value), resourceName)
                : throw new InvalidOperationException("missing fixture resource");
        }
    }

    private sealed class LowerProvider : IConfigProvider
    {
        public string Name => "LowerGoogleContractProvider";
        public int Priority => int.MinValue;
        public string? Value { get; set; }
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            return Value is T value ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
        }
    }

    private sealed class ThrowingClient : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) =>
            throw new InvalidOperationException("fixture failure");
    }
}
