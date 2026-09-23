using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class AuditContractTests
{
    [Fact]
    public void DiagnosticCollectionOptionReachesReporterRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentConfigProvider, MissingEnvironmentProvider>();
        services.AddSingleton<IConfigProvider>(new ValueProvider("Services", new List<string> { "a", "b" }));
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
        services.AddConfigAuditKey<List<string>>("Services", options => options.TraverseCollectionElements = true);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();
        Assert.True(Assert.Single(registry.Entries).OptionsSnapshot.TraverseCollectionElements);
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        Assert.Equal(["Services[0]", "Services[1]"], Assert.Single(report.Entries).Children.Select(entry => entry.Key));
    }

    [Fact]
    public void AuditResolutionReceivesTheTypedKnownEntryWithoutReparsingItsOrigin()
    {
        var key = AppSurfaceConfigKey.Parse("Payments.ApiKey")
            .WithInput(ConfigKeyInputOrigin.TranslatedDot, "Payments.ApiKey");
        var provider = new CapturingEnvironmentProvider
        {
            Resolution = request =>
            {
                Assert.Equal(ConfigKeyInputOrigin.TranslatedDot, request.Key.InputOrigin);
                return new ConfigValueResolution(
                    request.Key,
                    ConfigAuditEntryState.Resolved,
                    "value",
                    [],
                    []);
            }
        };
        var reporter = CreateReporter(provider, [], new ConfigAuditKnownEntry(key, null, typeof(string)));

        var report = reporter.GetReport("Production");

        Assert.Equal(ConfigAuditEntryState.Resolved, Assert.Single(report.Entries).State);
    }

    [Fact]
    public void TerminalEnvironmentAuditResultSuppressesLowerProviderReads()
    {
        var environment = new CapturingEnvironmentProvider
        {
            Resolution = request => new ConfigValueResolution(
                request.Key,
                ConfigAuditEntryState.Invalid,
                null,
                [],
                [new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Error,
                    Code = "terminal",
                    Key = request.Key.Value,
                    ConfigPath = request.Key.Value,
                    Message = "terminal"
                }])
        };
        var lower = new CountingProvider();
        var reporter = CreateReporter(
            environment,
            [lower],
            new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Payments.ApiKey"), null, typeof(string)));

        var report = reporter.GetReport("Production");

        Assert.Equal(ConfigAuditEntryState.Invalid, Assert.Single(report.Entries).State);
        Assert.Equal(0, lower.AuditCalls);
        Assert.Equal(0, lower.GenericCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverySuccessfulProviderIsAuditedWithoutChangingWinner(bool environmentWins)
    {
        var environment = new GenericEnvironment
        {
            Result = environmentWins ? ConfigProviderValueResult<string>.Found("environment") : null
        };
        var high = new GenericProvider("High", 20) { Result = ConfigProviderValueResult<string>.Found("high") };
        var low = new GenericProvider("Low", 10) { Result = ConfigProviderValueResult<string>.Found("low") };
        var report = CreateReporter(environment, [low, high], Known<string>()).GetReport("Production");
        var entry = Assert.Single(report.Entries);
        Assert.Equal(environmentWins ? "environment" : "high", entry.DisplayValue);
        Assert.Equal(environmentWins ? new[] { "Environment", "High", "Low" } : new[] { "High", "Low" },
            entry.Sources.Select(source => source.ProviderName));
        Assert.Equal(1, high.Calls);
        Assert.Equal(1, low.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalStopsProvenanceReadsAndCannotReplaceAnEarlierWinner(bool earlierWinner)
    {
        var high = new GenericProvider("High", 30) { Result = ConfigProviderValueResult<string>.Found("winner") };
        var terminal = new GenericProvider("Terminal", 20)
        {
            Result = ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision"))
        };
        var low = new GenericProvider("Low", 10) { Result = ConfigProviderValueResult<string>.Found("unreachable") };
        var report = CreateReporter(new GenericEnvironment(), earlierWinner ? [high, terminal, low] : [terminal, low],
            Known<string>()).GetReport("Production");
        var entry = Assert.Single(report.Entries);
        Assert.Equal(earlierWinner ? ConfigAuditEntryState.Resolved : ConfigAuditEntryState.Invalid, entry.State);
        Assert.Equal(earlierWinner ? "winner" : null, entry.DisplayValue);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-key-collision");
        Assert.Equal(0, low.Calls);
    }

    [Fact]
    public void GenericMissingValueTypeRemainsMissingAndFoundZeroRemainsFound()
    {
        var missing = Assert.Single(CreateReporter(new GenericEnvironment(), [], Known<int>()).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Missing, missing.State);
        Assert.Null(missing.DisplayValue);
        var found = new GenericProvider("Zero", 1) { Result = ConfigProviderValueResult<int>.Found(0) };
        var entry = Assert.Single(CreateReporter(new GenericEnvironment(), [found], Known<int>()).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Resolved, entry.State);
        Assert.Equal("0", entry.DisplayValue);
    }

    [Fact]
    public void NullGenericResultIsTerminalAndSuppressesLowerProviders()
    {
        var malformed = new GenericProvider("Malformed", 10) { ReturnNull = true };
        var low = new GenericProvider("Low", 1);
        var entry = Assert.Single(CreateReporter(new GenericEnvironment(), [malformed, low], Known<string>()).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, entry.State);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-invalid-result");
        Assert.Equal(0, low.Calls);
    }

    [Fact]
    public void FoundNoticesReachScopeAndExplicitAuditWithoutResolvedValue()
    {
        const string value = "value-sentinel-never-in-diagnostics";
        var notice = new ConfigProviderNotice("external-notice", "Problem\nline", "Cause", "Fix", "Docs", false);
        var provider = new GenericProvider("Notices", 1) { Result = ConfigProviderValueResult<string>.Found(value, notice) };
        var entry = Assert.Single(CreateReporter(new GenericEnvironment(), [provider], Known<string>()).GetReport("Production").Entries);
        var diagnostic = Assert.Single(entry.Diagnostics, item => item.Code == "external-notice");
        Assert.Contains("Problem\\u000aline", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Same(notice, Assert.Single(provider.Request!.Scope.Notices).Notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationPropagatesThroughGenericAndRichAuditProviders(bool richAudit)
    {
        var cancellation = new OperationCanceledException("cancelled-value-sentinel");
        IConfigProvider provider = richAudit
            ? new DiagnosticFailure(cancellation)
            : new GenericProvider("Cancelled", 1) { Failure = cancellation };
        var reporter = CreateReporter(new GenericEnvironment(), [provider], Known<string>());
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() => reporter.GetReport("Production")));
    }

    [Fact]
    public void TranslatedDeclarationNoticeAppearsEvenWithRichAuditProvider()
    {
        var key = AppSurfaceConfigKey.Parse("Feature:Value").WithInput(ConfigKeyInputOrigin.TranslatedDot, "Feature.Value");
        var reporter = CreateReporter(new MissingEnvironmentProvider(), [new ValueProvider(key.Value, "value")],
            new ConfigAuditKnownEntry(key, null, typeof(string)));
        var entry = Assert.Single(reporter.GetReport("Production").Entries);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-key-legacy-dot-path"
            && diagnostic.Message.Contains("Feature:Value", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyRegistryAndNullProviderListProduceAnEmptyDefaultReport()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var reporter = new ConfigAuditReporter(new GenericEnvironment(), null, null, services, new ConfigAuditRedactor(),
            Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));
        Assert.Empty(reporter.GetReport(new ConfigAuditReportRequest("Production")).Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryUsesDeclarationElseWinnerSpellingAndStableTypedGrouping(bool declared)
    {
        var high = new DiscoveryProvider("High", 20, "zEta", "a.b");
        var low = new DiscoveryProvider("Low", 10, "A.B", "ZETA");
        using var services = new ServiceCollection().BuildServiceProvider();
        var reporter = new ConfigAuditReporter(new GenericEnvironment(), [low, high],
            declared ? [new ConfigAuditKnownEntry(AppSurfaceConfigKey.FromSegments("A.B"), null, typeof(string))] : [],
            services, new ConfigAuditRedactor(), Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));
        var discovered = reporter.GetReport("Production").DiscoveredKeys;
        Assert.Equal(declared ? new[] { "A.B", "zEta" } : new[] { "a.b", "zEta" }, discovered.Select(entry => entry.Key));
        Assert.All(discovered, entry => Assert.Equal(new[] { "High", "Low" }, entry.Sources.Select(source => source.ProviderName)));
        Assert.Equal(new[] { "a.b", "A.B" }, discovered[0].Sources.Select(source => source.ConfigPath));
        Assert.Equal(declared ? "High" : null, discovered[0].DisplayValue);
    }

    [Fact]
    public void AuditBudgetExhaustionProducesExplicitReportDiagnostic()
    {
        var provider = new GenericProvider("Budgeted", 1)
        {
            RequestAction = request => request.Scope.MarkAuditDeadline(),
            Result = ConfigProviderValueResult<string>.Found("value")
        };
        var report = CreateReporter(new GenericEnvironment(), [provider], Known<string>()).GetReport("Production");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "config-audit-deadline");
    }

    [Fact]
    public void TerminalProviderCannotBecomeAWrapperDefault()
    {
        var provider = new GenericProvider("Terminal", 1)
        {
            Result = ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision"))
        };
        var known = new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Feature:Value"), typeof(FallbackConfig), typeof(string));
        var entry = Assert.Single(CreateReporter(new GenericEnvironment(), [provider], known).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, entry.State);
        Assert.Null(entry.DisplayValue);
        Assert.DoesNotContain(entry.Sources, source => source.Kind == ConfigAuditSourceKind.Default);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("exception")]
    [InlineData("null")]
    [InlineData("null-value")]
    public void InvalidPatchNeverPublishesBaseOrPartialValue(string outcome)
    {
        var environment = new ScriptedPatchEnvironment
        {
            Patch = () => outcome switch
            {
                "exception" => throw new InvalidOperationException("value-sentinel"),
                "null" => null!,
                "null-value" => new ConfigPatchDiagnosticResult(true, null, [], []),
                _ => new ConfigPatchDiagnosticResult(false, null, [],
                    [new ConfigAuditDiagnostic { Code = "config-patch-failed", Severity = ConfigAuditDiagnosticSeverity.Error, Message = "Patch failed" }])
            }
        };
        var provider = new GenericProvider("Base", 1) { Result = ConfigProviderValueResult<string>.Found("value-sentinel") };
        var entry = Assert.Single(CreateReporter(environment, [provider], Known<string>()).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, entry.State);
        Assert.Null(entry.DisplayValue);
        Assert.Contains(entry.Sources, source => source.ProviderName == "Base");
        Assert.All(entry.Diagnostics, diagnostic => Assert.DoesNotContain("value-sentinel", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void PatchCancellationPropagatesWithoutReadingLowerProvidersAfterTerminal()
    {
        var cancellation = new OperationCanceledException();
        var environment = new ScriptedPatchEnvironment
        {
            Result = ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision")),
            Patch = () => throw cancellation
        };
        var low = new GenericProvider("Low", 1);
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() =>
            CreateReporter(environment, [low], Known<string>()).GetReport("Production")));
        Assert.Equal(0, low.Calls);
    }

    [Fact]
    public void KnownEntriesAndEnvironmentInventoryShareOneSnapshotPerReport()
    {
        var captures = 0;
        var source = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => source.Environment).Returns("Production");
        A.CallTo(() => source.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["MAPPED"] = ++captures == 1 ? "first" : "second"
            });
        var environment = new EnvironmentConfigProvider(source,
            Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey("Feature:Value", "MAPPED")));
        var reporter = CreateReporter(environment, [], Known<string>());
        foreach (var expected in new[] { "first", "second" })
        {
            var report = reporter.GetReport("Production");
            Assert.Equal(expected, Assert.Single(report.Entries).DisplayValue);
            Assert.Equal(expected, Assert.Single(report.DiscoveredKeys).DisplayValue);
            Assert.Equal("MAPPED", Assert.Single(Assert.Single(report.DiscoveredKeys).Sources).EnvironmentVariableName);
        }
        Assert.Equal(2, captures);
        A.CallTo(() => source.GetEnvironmentVariable(A<string>._, A<string?>._)).MustNotHaveHappened();
    }

    [Fact]
    public void ProviderInvocationWithoutInnerExceptionStillFailsClosed()
    {
        var provider = new GenericProvider("Broken", 10) { Failure = new System.Reflection.TargetInvocationException(null) };
        var lower = new GenericProvider("Lower", 1);
        var entry = Assert.Single(CreateReporter(new GenericEnvironment(), [provider, lower], Known<string>()).GetReport("Production").Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, entry.State);
        Assert.Null(entry.DisplayValue);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-get-value-threw");
        Assert.Equal(0, lower.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SuccessfulAliasesBeyondNoticeCapacityMarkPublicReportIncomplete(int aliasCount)
    {
        var source = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => source.Environment).Returns("Production");
        A.CallTo(() => source.CaptureEnvironmentVariables()).Returns(new Dictionary<string, string>
        {
            ["FEATURE:ONE"] = "first",
            ["FEATURE:TWO"] = "second"
        });
        var limits = Options.Create(new ConfigResourceOptions { MaxNoticeIdentities = 1 });
        var registrations = new ServiceCollection();
        foreach (var key in new[] { "Feature:One", "Feature:Two" }.Take(aliasCount)) registrations.AddConfigAuditKey<string>(key);
        using var services = registrations.BuildServiceProvider();
        var registry = services.GetRequiredService<ConfigDeclarationRegistry>();
        var entries = registry.Entries;
        var environment = new EnvironmentConfigProvider(source, resourceOptions: limits, declarationRegistry: registry);
        var reporter = new ConfigAuditReporter(environment, [], entries, services, new ConfigAuditRedactor(),
            Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()), limits);
        var report = reporter.GetReport("Production");
        Assert.Equal(new[] { "first", "second" }.Take(aliasCount), report.Entries.Select(entry => entry.DisplayValue));
        Assert.All(report.Entries, entry => Assert.Equal(ConfigAuditEntryState.Resolved, entry.State));
        Assert.Single(report.Entries.SelectMany(entry => entry.Diagnostics), diagnostic => diagnostic.Code == "config-key-legacy-provider-alias");
        if (aliasCount == 1)
        {
            Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "config-audit-notice-limit");
            return;
        }
        var incomplete = Assert.Single(report.Diagnostics, diagnostic => diagnostic.Code == "config-audit-notice-limit");
        Assert.Equal(ConfigAuditDiagnosticSeverity.Error, incomplete.Severity);
        Assert.DoesNotContain("first", incomplete.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", incomplete.Message, StringComparison.Ordinal);
    }

    public sealed class FallbackConfig : Config<string>
    {
        public override string DefaultValue => "must-not-be-used";
    }

    private sealed class ScriptedPatchEnvironment : GenericEnvironment, IConfigDiagnosticPatcher
    {
        public required Func<ConfigPatchDiagnosticResult> Patch { get; init; }
        public ConfigPatchDiagnosticResult TracePatch(ConfigProviderRequest request, object? value, Type type) => Patch();
    }

    private sealed class DiscoveryProvider(string name, int priority, params string[] keys) : IConfigProvider, IConfigProviderAuditKeyEnumerator
    {
        public int Priority => priority;
        public string Name => name;
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => ConfigProviderValueResult<T>.Missing();
        public IReadOnlyList<ConfigProviderAuditDiscoveredKey> EnumerateKeys(string environment) => keys.Select(key =>
            new ConfigProviderAuditDiscoveredKey(AppSurfaceConfigKey.Parse(key), name, ConfigAuditDiscoveredValueKind.Scalar,
                [new ConfigAuditSourceRecord { Kind = ConfigAuditSourceKind.Provider, ProviderName = name, ConfigPath = key, Role = ConfigAuditSourceRole.Base }], [])).ToArray();
    }

    private static ConfigAuditKnownEntry Known<T>() => new(AppSurfaceConfigKey.Parse("Feature:Value"), null, typeof(T));

    private class GenericProvider(string name, int priority) : IConfigProvider
    {
        public object? Result { get; init; }
        public bool ReturnNull { get; init; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public ConfigProviderRequest? Request { get; private set; }
        public Action<ConfigProviderRequest>? RequestAction { get; init; }
        public int Priority => priority;
        public string Name => name;
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Calls++;
            Request = request;
            RequestAction?.Invoke(request);
            if (Failure != null) throw Failure;
            return ReturnNull ? null! : Result is null ? ConfigProviderValueResult<T>.Missing() : (ConfigProviderValueResult<T>)Result;
        }
    }

    private class GenericEnvironment() : GenericProvider("Environment", 100), IEnvironmentConfigProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }

    private sealed class DiagnosticFailure(Exception exception) : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 10;
        public string Name => "DiagnosticFailure";
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => throw exception;
        public ConfigValueResolution Resolve(ConfigProviderRequest request, Type type, ConfigAuditSourceRole role) => throw exception;
        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private static IConfigAuditReporter CreateReporter(
        IEnvironmentConfigProvider environment,
        IReadOnlyList<IConfigProvider> providers,
        ConfigAuditKnownEntry knownEntry)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ConfigAuditReporter(
            environment,
            providers,
            [knownEntry],
            services,
            new ConfigAuditRedactor(),
            Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));
    }

    private sealed class CapturingEnvironmentProvider : IEnvironmentConfigProvider, IConfigDiagnosticProvider
    {
        public Func<ConfigProviderRequest, ConfigValueResolution>? Resolution { get; init; }

        public int Priority => 100;

        public string Name => nameof(CapturingEnvironmentProvider);

        public string Environment => "Production";

        public bool IsDevelopment => false;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            ConfigProviderValueResult<T>.Missing();

        ConfigValueResolution IConfigDiagnosticProvider.Resolve(
            ConfigProviderRequest request,
            Type valueType,
            ConfigAuditSourceRole role) =>
            Resolution?.Invoke(request) ?? ConfigValueResolution.Missing(request.Key);

        IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) => [];
    }

    private sealed class CountingProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        public int AuditCalls { get; private set; }

        public int GenericCalls { get; private set; }

        public int Priority => 10;

        public string Name => nameof(CountingProvider);

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            GenericCalls++;
            return ConfigProviderValueResult<T>.Found(default!);
        }

        ConfigValueResolution IConfigDiagnosticProvider.Resolve(
            ConfigProviderRequest request,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            AuditCalls++;
            return ConfigValueResolution.Missing(request.Key);
        }

        IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) => [];
    }

    private sealed class MissingEnvironmentProvider : IEnvironmentConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 100;
        public string Name => nameof(MissingEnvironmentProvider);
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => ConfigProviderValueResult<T>.Missing();
        ConfigValueResolution IConfigDiagnosticProvider.Resolve(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role) => ConfigValueResolution.Missing(request.Key);
        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class ValueProvider(string key, object value) : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 1;
        public string Name => nameof(ValueProvider);
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => ConfigProviderValueResult<T>.Missing();
        ConfigValueResolution IConfigDiagnosticProvider.Resolve(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role) =>
            string.Equals(request.Key.Value, key, StringComparison.OrdinalIgnoreCase)
                ? new ConfigValueResolution(request.Key, ConfigAuditEntryState.Resolved, value, [], [])
                : ConfigValueResolution.Missing(request.Key);
        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }
}
