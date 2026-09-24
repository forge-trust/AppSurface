using ForgeTrust.AppSurface.Config.Testing;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class EnvironmentConfigContractTests
{
    [Theory]
    [InlineData("none", "", "", ConfigProviderValueStatus.Missing)]
    [InlineData("scoped-canonical", "PRODUCTION__A__B", "canonical", ConfigProviderValueStatus.Found)]
    [InlineData("unscoped-canonical", "A__B", "canonical", ConfigProviderValueStatus.Found)]
    [InlineData("scoped-legacy", "PRODUCTION_A:B", "legacy", ConfigProviderValueStatus.Found)]
    [InlineData("unscoped-legacy", "A:B", "legacy", ConfigProviderValueStatus.Found)]
    [InlineData("scoped-and-unscoped", "PRODUCTION__A__B|A:B", "scoped", ConfigProviderValueStatus.Found)]
    public void StrictAliasTableAppliesIndependentlyPerScope(
        string _, string nativeName, string value, ConfigProviderValueStatus expectedStatus)
    {
        var values = string.IsNullOrEmpty(nativeName)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : nativeName.Split('|').ToDictionary(name => name, _ => value, StringComparer.Ordinal);
        var environment = new SnapshotEnvironment("Production", values);
        var request = StrictRequest(environment, "A:B");

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(request);

        Assert.Equal(expectedStatus, result.Status);
        if (expectedStatus == ConfigProviderValueStatus.Found)
        {
            Assert.Equal(value, result.Value);
            Assert.Equal(value == "legacy", result.Notices.Count != 0);
        }
    }

    [Theory]
    [InlineData("none", "", "", ConfigProviderValueStatus.Missing)]
    [InlineData("scoped-canonical", "PRODUCTION__A__B", "canonical", ConfigProviderValueStatus.Found)]
    [InlineData("unscoped-canonical", "A__B", "canonical", ConfigProviderValueStatus.Found)]
    [InlineData("scoped-legacy", "PRODUCTION_A_B", "legacy", ConfigProviderValueStatus.Found)]
    [InlineData("unscoped-legacy", "A_B", "legacy", ConfigProviderValueStatus.Found)]
    [InlineData("both-scopes", "PRODUCTION_A_B|A_B", "legacy", ConfigProviderValueStatus.Found)]
    public void TranslatedDotAliasTableAppliesIndependentlyPerScope(
        string _, string nativeName, string value, ConfigProviderValueStatus expectedStatus)
    {
        var values = string.IsNullOrEmpty(nativeName)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : nativeName.Split('|').ToDictionary(name => name, _ => value, StringComparer.Ordinal);
        var environment = new SnapshotEnvironment("Production", values);
        var request = new ConfigProviderRequest(
            environment.Environment,
            AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B"));

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(request);

        Assert.Equal(expectedStatus, result.Status);
        if (expectedStatus == ConfigProviderValueStatus.Found)
        {
            Assert.Equal(value, result.Value);
            Assert.Equal(value == "legacy", result.Notices.Count != 0);
        }
    }

    [Fact]
    public void TranslatedDotCanonicalAndAliasInOneScopeAreTerminal()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION__A__B"] = "canonical",
            ["PRODUCTION_A_B"] = "legacy"
        });
        var request = new ConfigProviderRequest(
            environment.Environment,
            AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B"));

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void TranslatedDotAliasCaseOnlyCollisionIsTerminal()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION_A_B"] = "one",
            ["production_a_b"] = "two"
        });
        var request = new ConfigProviderRequest(
            environment.Environment,
            AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B"));

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void StrictCanonicalAndLegacyInOneScopeAreTerminalEvenWhenValuesMatch()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION__A__B"] = "same",
            ["PRODUCTION_A:B"] = "same"
        });

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(StrictRequest(environment, "A:B"));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void UnscopedCanonicalAndLegacyInOneScopeAreTerminal()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["A__B"] = "same",
            ["A:B"] = "same"
        });

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(StrictRequest(environment, "A:B"));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void TwoLegacySpellingsInOneScopeAreTerminal()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION_A:B"] = "one",
            ["PRODUCTION__A:B"] = "two"
        });

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(StrictRequest(environment, "A:B"));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void ExactDuplicateCandidateIsEmittedOnceForSingleSegmentStrictInput()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION__A"] = "value",
            ["A"] = "fallback"
        });

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(StrictRequest(environment, "A"));

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void TypedRequestDoesNotProbeStrictAliases()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION_A:B"] = "legacy"
        });

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(Request(environment, "A:B"));

        Assert.Equal(ConfigProviderValueStatus.Missing, result.Status);
    }

    [Fact]
    public void ObsoleteGetValueReturnsDefaultForMissingAndUsesStrictCanonicalInput()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION_A:B"] = "legacy",
            ["COUNT"] = "invalid",
            ["LABEL"] = "present"
        });
        var provider = new EnvironmentConfigProvider(environment);
        // Deliberately exercise only the retained compatibility boundary here.
#pragma warning disable CS0618
        Assert.Null(provider.GetValue<string>("Production", "A:B"));
        Assert.Equal("present", provider.GetValue<string>("Production", "Label"));
        Assert.Throws<ConfigurationResolutionException>(() => provider.GetValue<int>("Production", "Count"));
#pragma warning restore CS0618
    }

    [Fact]
    public void ExplicitMappingSuffixCollisionIsAtomic()
    {
        var options = new AppSurfaceEnvironmentConfigOptions()
            .MapKey("A:B", "SHARED")
            .MapKey("C:D", "shared");

        Assert.Throws<OptionsValidationException>(() => new EnvironmentConfigProvider(
            new SnapshotEnvironment("Production", new Dictionary<string, string>()), Options.Create(options)));
    }

    [Fact]
    public void ExplicitMappingCannotClaimKnownCanonicalSuffix()
    {
        var key = AppSurfaceConfigKey.Parse("A:B");
        var knownCanonicalKey = AppSurfaceConfigKey.Parse("C_D");
        var registry = new ConfigDeclarationRegistry(
            [
                new ConfigAuditKnownEntry(key, null, typeof(string)),
                new ConfigAuditKnownEntry(knownCanonicalKey, null, typeof(string))
            ],
            [],
            new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions
            {
                LegacyDotPathBehavior = LegacyDotPathBehavior.Strict
            })));

        var options = new AppSurfaceEnvironmentConfigOptions().MapKey(key, "C_D");

        Assert.Throws<OptionsValidationException>(() => new EnvironmentConfigProvider(
            new SnapshotEnvironment("Production", new Dictionary<string, string>()),
            Options.Create(options),
            declarationRegistry: registry));
    }

    [Fact]
    public void ResolveUsesOneSnapshotAndScopedValueWins()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PAYMENTS__APIKEY"] = "unscoped",
            ["PRODUCTION__PAYMENTS__APIKEY"] = "scoped"
        });
        var provider = new EnvironmentConfigProvider(environment);

        var result = provider.Resolve<string>(Request(environment, "Payments:ApiKey"));

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal("scoped", result.Value);
        Assert.Equal(1, environment.CaptureCount);
    }

    [Fact]
    public void ResolvePreservesLiteralSeparatorsAndReportsUnrepresentableSegments()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION__PAYMENTS__API-KEY"] = "literal"
        });
        var provider = new EnvironmentConfigProvider(environment);

        var found = provider.Resolve<string>(Request(environment, "Payments:Api-Key"));
        var unrepresentable = provider.Resolve<string>(Request(environment, "Payments:A__B"));

        Assert.Equal("literal", found.Value);
        Assert.Equal(ConfigProviderValueStatus.Terminal, unrepresentable.Status);
        Assert.Equal("config-key-unrepresentable", unrepresentable.Diagnostic!.Code);
    }

    [Fact]
    public void ResolveRejectsCaseOnlyNativeCollision()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION__PAYMENTS__APIKEY"] = "one",
            ["production__payments__apikey"] = "two"
        });
        var result = new EnvironmentConfigProvider(environment)
            .Resolve<string>(Request(environment, "Payments:ApiKey"));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
    }

    [Fact]
    public void ExplicitMappingReplacesCanonicalCandidates()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION__PAYMENTS_KEY"] = "mapped",
            ["PRODUCTION__PAYMENTS__KEY"] = "canonical"
        });
        var options = new AppSurfaceEnvironmentConfigOptions().MapKey("Payments:Key", "PAYMENTS_KEY");
        var provider = new EnvironmentConfigProvider(environment, Options.Create(options));

        var result = provider.Resolve<string>(Request(environment, "Payments:Key"));

        Assert.Equal("mapped", result.Value);
    }

    [Fact]
    public void PatchDoesNotPublishPartialMutationWhenPresentChildIsInvalid()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION__APP__NAME"] = "new",
            ["PRODUCTION__APP__PORT"] = "invalid"
        });
        var provider = new EnvironmentConfigProvider(environment);
        var current = new Settings { Name = "old", Port = 80 };
        var request = Request(environment, "App");

        var result = ((IConfigValuePatcher)provider).Patch(request, current);

        Assert.Equal(ConfigPatchStatus.Terminal, result.Status);
        Assert.Equal("old", current.Name);
        Assert.Equal(80, current.Port);
    }

    [Fact]
    public void ResolveDiscoversAllPresentCollectionEntriesWithoutSpeculativeReads()
    {
        var values = Enumerable.Range(0, 1025)
            .ToDictionary(index => $"PRODUCTION__APP__ITEMS__{index}", index => index.ToString(), StringComparer.Ordinal);
        var environment = new SnapshotEnvironment("Production", values);
        var provider = new EnvironmentConfigProvider(environment);

        var result = provider.Resolve<int[]>(Request(environment, "App:Items"));

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal(Enumerable.Range(0, 1025), result.Value);
        Assert.Equal(1, environment.CaptureCount);
    }

    [Fact]
    public void ResolveUsesTheStrictTrainOneAliasWhenItIsTheOnlyPresentSource()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION_PAYMENTS:APIKEY"] = "legacy"
        });
        var request = new ConfigProviderRequest(
            environment.Environment,
            AppSurfaceConfigKey.Parse("Payments:ApiKey").WithInput(
                ConfigKeyInputOrigin.StrictString, "Payments:ApiKey"));

        var result = new EnvironmentConfigProvider(environment).Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal("legacy", result.Value);
        Assert.Contains(result.Notices, notice => notice.Code == "config-key-legacy-provider-alias");
    }

    [Fact]
    public void ResourceLimitFailsClosedBeforePublishingAResolution()
    {
        var environment = new SnapshotEnvironment("Production", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCTION__APP__NAME"] = "value"
        });
        var options = Options.Create(new ConfigResourceOptions { MaxEnvironmentEntries = 1, MaxEnvironmentBytes = 1 });

        var result = new EnvironmentConfigProvider(environment, resourceOptions: options)
            .Resolve<string>(Request(environment, "App:Name"));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-environment-snapshot-limit", result.Diagnostic!.Code);
    }

    [Fact]
    public void ActualEnvironmentProviderPassesApplicableConfigTestingCases()
    {
        var applicable = ConfigProviderContractCases.All.Where(contractCase =>
            contractCase.Scenario is
                ConfigProviderContractScenario.CaseIdentity or
                ConfigProviderContractScenario.DottedSegment or
                ConfigProviderContractScenario.HyphenDistinction or
                ConfigProviderContractScenario.Missing or
                ConfigProviderContractScenario.MissingToLowerFallback or
                ConfigProviderContractScenario.SameLayerCollision or
                ConfigProviderContractScenario.OrderedOverride or
                ConfigProviderContractScenario.OrderedCaseCollision or
                ConfigProviderContractScenario.PrefixBoundary or
                ConfigProviderContractScenario.Unrepresentable or
                ConfigProviderContractScenario.LegacyTranslation or
                ConfigProviderContractScenario.ConcurrentRepeatability);

        var harness = new EnvironmentContractHarness();
        foreach (var contractCase in applicable)
        {
            ConfigProviderContractAssert.Case(harness, contractCase);
        }
    }

    private sealed class EnvironmentContractHarness : IConfigProviderContractHarness
    {
        public ConfigProviderContractSession Create(ConfigProviderContractCase contractCase)
        {
            ArgumentNullException.ThrowIfNull(contractCase);
            const string environmentName = "Production";
            const string expected = "environment-contract-marker";
            const string distinct = "environment-distinct-marker";

            var values = contractCase.Scenario switch
            {
                ConfigProviderContractScenario.CaseIdentity => Values(
                    ("PRODUCTION__PAYMENTS__APIKEY", expected)),
                ConfigProviderContractScenario.DottedSegment => Values(
                    ("PRODUCTION__LOGGING__LOGLEVEL__MICROSOFT.HOSTING.LIFETIME", expected)),
                ConfigProviderContractScenario.HyphenDistinction => Values(
                    ("PRODUCTION__PAYMENTS__API-KEY", expected),
                    ("PRODUCTION__PAYMENTS__API__KEY", distinct)),
                ConfigProviderContractScenario.Missing or ConfigProviderContractScenario.MissingToLowerFallback => Values(),
                ConfigProviderContractScenario.SameLayerCollision => Values(
                    ("PRODUCTION__PAYMENTS__APIKEY", expected),
                    ("production__payments__apikey", distinct)),
                ConfigProviderContractScenario.OrderedOverride => Values(
                    ("PAYMENTS__APIKEY", distinct),
                    ("PRODUCTION__PAYMENTS__APIKEY", expected)),
                ConfigProviderContractScenario.OrderedCaseCollision => Values(
                    ("PAYMENTS__APIKEY", distinct),
                    ("production__payments__apikey", expected)),
                ConfigProviderContractScenario.PrefixBoundary => Values(
                    ("PRODUCTION__PAYMENTS__APIKEY", expected)),
                ConfigProviderContractScenario.Unrepresentable => Values(),
                ConfigProviderContractScenario.LegacyTranslation => Values(
                    ("PAYMENTS__APIKEY", expected),
                    ("PRODUCTION_PAYMENTS_APIKEY", expected)),
                ConfigProviderContractScenario.ConcurrentRepeatability => Values(
                    ("PRODUCTION__PAYMENTS__APIKEY", expected)),
                _ => throw new ArgumentOutOfRangeException(nameof(contractCase))
            };

            var environment = new SnapshotEnvironment(environmentName, values);
            var provider = new EnvironmentConfigProvider(environment);
            var lower = new ContractLowerProvider(contractCase.Scenario == ConfigProviderContractScenario.MissingToLowerFallback ? expected : null);
            var manager = new DefaultConfigManager(
                provider,
                otherProviders: [lower],
                NullLogger<DefaultConfigManager>.Instance);
            return new ConfigProviderContractSession(manager, environmentName, expected, distinct, () =>
            {
                if (contractCase.Scenario == ConfigProviderContractScenario.MissingToLowerFallback) Assert.Equal(2, lower.Reads);
            });
        }

        private static IReadOnlyDictionary<string, string> Values(
            params (string Name, string Value)[] entries) =>
            entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
    }

    private sealed class ContractLowerProvider(string? marker) : IConfigProvider
    {
        public string Name => "LowerEnvironmentContractProvider";
        public int Priority => int.MinValue;
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            return marker is T value ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
        }
    }

    private static ConfigProviderRequest Request(IEnvironmentProvider environment, string key) =>
        new(environment.Environment, AppSurfaceConfigKey.Parse(key));

    private static ConfigProviderRequest StrictRequest(IEnvironmentProvider environment, string key) =>
        new(environment.Environment, AppSurfaceConfigKey.Parse(key).WithInput(ConfigKeyInputOrigin.StrictString, key));

    private sealed class Settings
    {
        public string Name { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    private sealed class SnapshotEnvironment(string environment, IReadOnlyDictionary<string, string> values) : IEnvironmentProvider
    {
        public string Environment { get; } = environment;
        public bool IsDevelopment => Environment.Equals("Development", StringComparison.OrdinalIgnoreCase);
        public int CaptureCount { get; private set; }
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            values.TryGetValue(name, out var value) ? value : defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables()
        {
            CaptureCount++;
            return new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
    }
}
