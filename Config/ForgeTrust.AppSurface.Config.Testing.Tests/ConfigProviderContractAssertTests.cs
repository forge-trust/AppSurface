namespace ForgeTrust.AppSurface.Config.Testing.Tests;

public sealed class ConfigProviderContractAssertTests
{
    [Fact]
    public void All_ExercisesEveryScenarioAndDisposesSessions()
    {
        var harness = new Harness();
        var results = ConfigProviderContractAssert.All(harness);
        Assert.Equal(ConfigProviderContractCases.All.Select(item => item.Id), results);
        Assert.Equal(ConfigProviderContractCases.All.Count, harness.Disposed);
        Assert.Equal(Enum.GetValues<ConfigProviderContractScenario>().Length, results.Count);
    }

    [Fact]
    public void Assertions_DoNotExposeMarkersOrProviderExceptionText()
    {
        var contractCase = ConfigProviderContractCases.All[0];
        var harness = new Harness { WrongMarker = true };
        var error = Assert.Throws<ConfigProviderContractAssertionException>(() => ConfigProviderContractAssert.Case(harness, contractCase));
        Assert.Equal(contractCase.Id, error.CaseId);
        Assert.DoesNotContain("SENTINEL", error.ToString());
        Assert.Equal(1, harness.Disposed);
        harness = new Harness { ThrowRaw = true };
        error = Assert.Throws<ConfigProviderContractAssertionException>(() => ConfigProviderContractAssert.Case(harness, contractCase));
        Assert.Contains("unexpected-provider-failure", error.Message);
        Assert.DoesNotContain("SENTINEL", error.ToString());
    }

    [Fact]
    public void ExpectedTerminalMayOccurDuringSourceCreation()
    {
        var contractCase = ConfigProviderContractCases.All.Single(item => item.Scenario == ConfigProviderContractScenario.SameLayerCollision);
        ConfigProviderContractAssert.Case(new Harness { ThrowAtCreation = true }, contractCase);
    }

    [Theory]
    [InlineData(ConfigProviderContractScenario.SameLayerCollision)]
    [InlineData(ConfigProviderContractScenario.Missing)]
    [InlineData(ConfigProviderContractScenario.MissingToLowerFallback)]
    [InlineData(ConfigProviderContractScenario.PrefixBoundary)]
    [InlineData(ConfigProviderContractScenario.HyphenDistinction)]
    public void IncorrectScenarioBehaviorFailsSafely(ConfigProviderContractScenario scenario)
    {
        var contractCase = ConfigProviderContractCases.All.Single(item => item.Scenario == scenario);
        Assert.Throws<ConfigProviderContractAssertionException>(() =>
            ConfigProviderContractAssert.Case(new Harness { BreakScenario = scenario }, contractCase));
    }

    [Fact]
    public void WrongTerminalCodeCannotPassEvenForNonterminalCase()
    {
        Assert.Throws<ConfigProviderContractAssertionException>(() =>
            ConfigProviderContractAssert.Case(new Harness { WrongTerminal = true }, ConfigProviderContractCases.All[0]));
    }

    [Fact]
    public void UnrepresentableUsesProviderCounterexampleInsteadOfRepresentableDefault()
    {
        var row = ConfigProviderContractCases.All.Single(item => item.Scenario == ConfigProviderContractScenario.Unrepresentable);
        var counterexample = AppSurfaceConfigKey.Parse("Payments:Unsupported.Dot");
        var harness = new Harness { Counterexample = counterexample };
        ConfigProviderContractAssert.Case(harness, row);
        Assert.Equal(counterexample, Assert.Single(harness.Requested));
        Assert.Equal("A_:B", row.Key.Value);
        Assert.Equal(1, harness.Disposed);
    }

    [Fact]
    public void CounterexampleCannotReplaceOtherCanonicalCases()
    {
        var harness = new Harness { Counterexample = AppSurfaceConfigKey.Parse("Different:Key") };
        var failure = Assert.Throws<ConfigProviderContractAssertionException>(() =>
            ConfigProviderContractAssert.Case(harness, ConfigProviderContractCases.All[0]));
        Assert.Contains("counterexample-outside-unrepresentable-scenario", failure.Message);
        Assert.Empty(harness.Requested);
        Assert.Equal(1, harness.Disposed);
    }

    [Fact]
    public void TerminalForAnotherKeyCannotSatisfyCounterexample()
    {
        var row = ConfigProviderContractCases.All.Single(item => item.Scenario == ConfigProviderContractScenario.Unrepresentable);
        var harness = new Harness { Counterexample = AppSurfaceConfigKey.Parse("Payments:Unsupported.Dot"), WrongTerminalKey = true };
        var failure = Assert.Throws<ConfigProviderContractAssertionException>(() => ConfigProviderContractAssert.Case(harness, row));
        Assert.Contains("unexpected-terminal-key", failure.Message);
    }

    [Fact]
    public void MissingToLowerFallbackRequiresTheLowerMarkerIncludingCaseVariant()
    {
        var row = ConfigProviderContractCases.All.Single(item => item.Scenario == ConfigProviderContractScenario.MissingToLowerFallback);
        var harness = new Harness();
        ConfigProviderContractAssert.Case(harness, row);
        Assert.Equal(new[] { "Payments:ApiKey", "payments:apikey" }, harness.Requested.Select(key => key.Value));
    }

    [Fact]
    public void ConstructorArgumentsAndCleanupOwnershipAreEnforced()
    {
        var key = AppSurfaceConfigKey.Parse("Key");
        Assert.Throws<ArgumentException>(() => new ConfigProviderContractCase("", ConfigProviderContractScenario.Missing, key));
        Assert.Throws<ArgumentNullException>(() => new ConfigProviderContractCase("id", ConfigProviderContractScenario.Missing, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigProviderContractCase("id", (ConfigProviderContractScenario)99, key));
        var manager = new Manager(ConfigProviderContractCases.All[0], new Harness());
        Assert.Throws<ArgumentNullException>(() => new ConfigProviderContractSession(null!, "P", "a", "b"));
        Assert.Throws<ArgumentException>(() => new ConfigProviderContractSession(manager, "", "a", "b"));
        Assert.Throws<ArgumentNullException>(() => new ConfigProviderContractSession(manager, "P", null!, "b"));
        Assert.Throws<ArgumentNullException>(() => new ConfigProviderContractSession(manager, "P", "a", null!));
        Assert.Throws<ArgumentException>(() => new ConfigProviderContractSession(manager, "P", "same", "same"));
        Assert.Throws<ArgumentNullException>(() => ConfigProviderContractAssert.All(null!));
        Assert.Throws<ArgumentNullException>(() => ConfigProviderContractAssert.Case(null!, ConfigProviderContractCases.All[0]));
        Assert.Throws<ArgumentNullException>(() => ConfigProviderContractAssert.Case(new Harness(), null!));
        var calls = 0;
        var session = new ConfigProviderContractSession(manager, "P", "a", "b", () => calls++);
        Assert.Same(manager, session.Manager);
        Assert.Equal("P", session.Environment);
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, calls);
        new ConfigProviderContractSession(manager, "P", "a", "b").Dispose();
        Assert.Throws<NotSupportedException>(() => ((IList<ConfigProviderContractCase>)ConfigProviderContractCases.All).Clear());
    }

    private sealed class Harness : IConfigProviderContractHarness
    {
        public int Disposed;
        public bool WrongMarker;
        public bool ThrowRaw;
        public bool ThrowAtCreation;
        public bool WrongTerminal;
        public bool WrongTerminalKey;
        public AppSurfaceConfigKey? Counterexample;
        public List<AppSurfaceConfigKey> Requested { get; } = [];
        public ConfigProviderContractScenario? BreakScenario;

        public ConfigProviderContractSession Create(ConfigProviderContractCase contractCase)
        {
            if (ThrowAtCreation) { throw Terminal(contractCase, contractCase.TerminalCode!); }
            return new ConfigProviderContractSession(new Manager(contractCase, this), "P", "SENTINEL_EXPECTED",
                "SENTINEL_DISTINCT", () => Interlocked.Increment(ref Disposed), Counterexample);
        }
    }

    private sealed class Manager(ConfigProviderContractCase contractCase, Harness harness) : IConfigManager
    {
        public T? GetValue<T>(string environment, string key) =>
            GetValue<T>(environment, AppSurfaceConfigKey.Parse(key.Replace('.', ':')));

        public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
        {
            lock (harness.Requested) { harness.Requested.Add(key); }
            if (harness.ThrowRaw) { throw new IOException("SENTINEL_RAW_FAILURE"); }
            if (harness.WrongTerminal) { throw Terminal(contractCase, "wrong-code"); }
            var breakScenario = harness.BreakScenario == contractCase.Scenario;
            if (contractCase.TerminalCode is not null && !breakScenario
                && (harness.Counterexample is null || key.Equals(harness.Counterexample)))
            {
                throw new ConfigurationResolutionException("P", harness.WrongTerminalKey ? contractCase.Key : key, "Fixture",
                    Terminal(contractCase, contractCase.TerminalCode).Diagnostic);
            }
            if (contractCase.Scenario == ConfigProviderContractScenario.Missing && !breakScenario) { return default; }
            if (contractCase.Scenario == ConfigProviderContractScenario.MissingToLowerFallback && breakScenario) { return default; }
            if (contractCase.Scenario == ConfigProviderContractScenario.PrefixBoundary && key.Value.StartsWith("PaymentsArchive", StringComparison.Ordinal) && !breakScenario) { return default; }
            if (harness.WrongMarker) { return (T)(object)"SENTINEL_WRONG"; }
            if (contractCase.Scenario == ConfigProviderContractScenario.HyphenDistinction && key.Equals(AppSurfaceConfigKey.Parse("Payments:Api:Key")) && !breakScenario)
            {
                return (T)(object)"SENTINEL_DISTINCT";
            }

            return (T)(object)"SENTINEL_EXPECTED";
        }
    }

    private static ConfigurationResolutionException Terminal(ConfigProviderContractCase contractCase, string code) =>
        new("P", contractCase.Key, "Fixture", new ConfigProviderTerminalDiagnostic(code,
            "Fixture failed.", "Fixture source was terminal.", "Repair the fixture.", "https://example.test/docs", false));
}
