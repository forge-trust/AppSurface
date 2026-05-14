using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigAuditReporterTests
{
    [ConfigKey("Region", root: true)]
    private sealed class RegionConfig : Config<string>
    {
        public override string DefaultValue => "us-east-1";
    }

    [ConfigKey("Retry.Count", root: true)]
    [ConfigValueRange(1, 5)]
    private sealed class RetryCountConfig : ConfigStruct<int>
    {
    }

    private sealed class AppSettings
    {
        public string? Mode { get; set; }

        public DatabaseOptions Database { get; set; } = new();
    }

    private sealed class DatabaseOptions
    {
        public string? Host { get; set; }

        public int Port { get; set; }

        public int TimeoutSeconds { get; set; }
    }

    [ConfigKey("Short.Name", root: true)]
    [ConfigValueMinLength(3)]
    private sealed class ShortNameConfig : Config<string>
    {
    }

    [ConfigKey("Throwing.Name", root: true)]
    private sealed class ThrowingNameConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            string value,
            ValidationContext validationContext) =>
            throw new InvalidOperationException("string validator failed");
    }

    [ConfigKey("Leaky.Name", root: true)]
    private sealed class LeakyNameConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            string value,
            ValidationContext validationContext) =>
            [new ValidationResult($"do not leak {value}")];
    }

    [ConfigKey("Leaky.Throwing", root: true)]
    private sealed class LeakyThrowingConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            string value,
            ValidationContext validationContext) =>
            throw new InvalidOperationException($"do not leak {value}");
    }

    [ConfigKey("Throwing.Count", root: true)]
    private sealed class ThrowingCountConfig : ConfigStruct<int>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            int value,
            ValidationContext validationContext) =>
            throw new InvalidOperationException("int validator failed");
    }

    [ConfigKey("Default.Port", root: true)]
    private sealed class DefaultPortConfig : ConfigStruct<int>
    {
        public override int? DefaultValue => 8080;
    }

    private sealed class UnconstructableConfig : Config<string>
    {
        public UnconstructableConfig(IMissingDependency dependency)
        {
            ArgumentNullException.ThrowIfNull(dependency);
        }
    }

    private sealed class ThrowingConstructorConfig : Config<string>
    {
        public ThrowingConstructorConfig()
        {
            throw new InvalidOperationException("constructor failed with super-secret");
        }
    }

    private interface IMissingDependency
    {
    }

    private sealed class OddShape
    {
        public readonly string ReadOnlyField = "ignored";

        public string MutableField = "field-value";

        public string this[int index] => index.ToString();

        public string BadProperty => throw new JsonException("bad getter");

        public string GoodProperty => "property-value";
    }

    private sealed class CyclicOptions
    {
        public string Name { get; set; } = "root";

        public CyclicOptions? Self { get; set; }
    }

    [Fact]
    public void GetReport_WithContractFixture_ReportsStatesSourcesPatchesAndRedaction()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "appsettings.Staging.json"),
                """
                {
                  "Feature": {
                    "Enabled": true
                  },
                  "MyApp": {
                    "Settings": {
                      "Mode": "file",
                      "Database": {
                        "Host": "db.staging.internal",
                        "Port": 5432,
                        "TimeoutSeconds": 30
                      }
                    }
                  },
                  "Retry": {
                    "Count": 10
                  },
                  "Shape": "legacy",
                  "Unused": {
                    "NullValue": null
                  }
                }
                """);
            File.WriteAllText(Path.Combine(tempDir, "appsettings.Broken.json"), "{");
            File.WriteAllText(
                Path.Combine(tempDir, "config_Override.Staging.json"),
                """
                {
                  "Shape": {
                    "Nested": "from-override"
                  }
                }
                """);

            var environment = A.Fake<IEnvironmentProvider>();
            A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
            A.CallTo(() => environment.GetEnvironmentVariable("STAGING__BILLING__ENDPOINT", A<string?>._))
                .Returns("https://staging-billing.example");
            A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
                .Returns("6543");
            A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__TIMEOUTSECONDS", A<string?>._))
                .Returns("soon");
            A.CallTo(() => environment.GetEnvironmentVariable("PAYMENT__APIKEY", A<string?>._))
                .Returns("super-secret");

            var services = CreateServices(tempDir, environment);
            services.AddConfigAuditKey<bool>("Feature.Enabled");
            services.AddConfigAuditKey<string>("Billing.Endpoint");
            services.AddConfigAuditKey<AppSettings>("MyApp.Settings");
            services.AddSingleton(new ConfigAuditKnownEntry("Region", typeof(RegionConfig), typeof(string)));
            services.AddConfigAuditKey<string>("Region");
            services.AddConfigAuditKey<string>("Missing.RequiredApiUrl");
            services.AddConfigAuditKey<string>("Payment.ApiKey");
            services.AddSingleton(new ConfigAuditKnownEntry("Retry.Count", typeof(RetryCountConfig), typeof(int)));
            services.AddConfigAuditKey<string>("Shape.Nested");

            var provider = services.BuildServiceProvider();
            var reporter = provider.GetRequiredService<IConfigAuditReporter>();

            var report = reporter.GetReport("Staging");

            Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "config-file-malformed");
            AssertEntry(report, "Feature.Enabled", ConfigAuditEntryState.Resolved, "True");
            Assert.DoesNotContain(
                report.Entries.SelectMany(entry => entry.Diagnostics),
                diagnostic => diagnostic.Code == "config-file-null-skipped");

            var billing = AssertEntry(report, "Billing.Endpoint", ConfigAuditEntryState.Resolved, "https://staging-billing.example");
            Assert.Contains(billing.Sources, source => source.EnvironmentVariableName == "STAGING__BILLING__ENDPOINT");

            var settings = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null);
            Assert.Contains(settings.Sources, source => source.FilePath?.EndsWith("appsettings.Staging.json", StringComparison.Ordinal) == true);
            Assert.Contains(settings.Sources, source => source.EnvironmentVariableName == "MYAPP__SETTINGS__DATABASE__PORT");
            Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Message.Contains("MYAPP__SETTINGS__DATABASE__TIMEOUTSECONDS", StringComparison.Ordinal));

            var database = settings.Children
                .Single(child => child.Key == "MyApp.Settings.Database");
            Assert.Equal(ConfigAuditEntryState.PartiallyResolved, database.State);

            var port = database.Children.Single(child => child.Key == "MyApp.Settings.Database.Port");
            Assert.Equal("6543", port.DisplayValue);
            Assert.Contains(port.Sources, source => source.EnvironmentVariableName == "MYAPP__SETTINGS__DATABASE__PORT");

            var region = AssertEntry(report, "Region", ConfigAuditEntryState.Defaulted, "us-east-1");
            Assert.Contains(region.Sources, source => source.Kind == ConfigAuditSourceKind.Default);

            AssertEntry(report, "Missing.RequiredApiUrl", ConfigAuditEntryState.Missing, null);

            var apiKey = AssertEntry(report, "Payment.ApiKey", ConfigAuditEntryState.Resolved, "[redacted]");
            Assert.True(apiKey.IsRedacted);

            var retry = AssertEntry(report, "Retry.Count", ConfigAuditEntryState.Invalid, "10");
            Assert.Contains(retry.Diagnostics, diagnostic => diagnostic.Code == "config-validation-failed");

            var shape = AssertEntry(report, "Shape.Nested", ConfigAuditEntryState.Resolved, "from-override");
            Assert.Contains(shape.Sources, source => source.FilePath?.EndsWith("config_Override.Staging.json", StringComparison.Ordinal) == true);

            var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
            Assert.Contains("Environment: Staging", rendered, StringComparison.Ordinal);
            Assert.Contains("Payment.ApiKey = [redacted]", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("soon", rendered, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetReport_DoesNotMutateProviderObjectWhenTracingEnvironmentPatch()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");
        var providerValue = new AppSettings
        {
            Mode = "file",
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432
            }
        };

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("MyApp.Settings", providerValue));
        services.AddConfigAuditKey<AppSettings>("MyApp.Settings");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null);
        Assert.Equal(5432, providerValue.Database.Port);
        var database = entry.Children.Single(child => child.Key == "MyApp.Settings.Database");
        Assert.Equal("6543", database.Children.Single(child => child.Key == "MyApp.Settings.Database.Port").DisplayValue);
    }

    [Fact]
    public void GetReport_UsesPatchSourceForDescendantsWhenEnvironmentReplacesNestedObject()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE", A<string?>._))
            .Returns("""{"Host":"db.from.env","Port":6543,"TimeoutSeconds":15}""");
        var providerValue = new AppSettings
        {
            Mode = "file",
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432,
                TimeoutSeconds = 30
            }
        };

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("MyApp.Settings", providerValue));
        services.AddConfigAuditKey<AppSettings>("MyApp.Settings");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null);
        var database = entry.Children.Single(child => child.Key == "MyApp.Settings.Database");
        var host = database.Children.Single(child => child.Key == "MyApp.Settings.Database.Host");
        Assert.Equal("db.from.env", host.DisplayValue);
        Assert.Contains(host.Sources, source => source.EnvironmentVariableName == "MYAPP__SETTINGS__DATABASE");
        Assert.DoesNotContain(host.Sources, source => source.ProviderName == nameof(StaticConfigProvider));
    }

    [Fact]
    public void Redactor_RedactsSensitiveCollectionsWithoutLeakingCount()
    {
        var redactor = new ConfigAuditRedactor();

        var redacted = redactor.FormatValue(
            "ConnectionStrings",
            new[] { "one", "two", "three" },
            []);

        Assert.True(redacted.IsRedacted);
        Assert.Equal("[redacted]", redacted.DisplayValue);
    }

    [Fact]
    public void GetReport_ReportsWrapperInspectionFailuresAndProviderFallbackShapes()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Short.Name"] = "no",
                    ["Throwing.Name"] = "valid-name",
                    ["Throwing.Count"] = 7,
                    ["Leaky.Name"] = "super-secret",
                    ["Leaky.Throwing"] = "super-secret",
                    ["Mismatched.Name"] = 5,
                    ["Mismatched.Count"] = "seven",
                    ["Object.String"] = "plain",
                    ["Odd.Shape"] = new OddShape()
                }));
        services.AddSingleton(new ConfigAuditKnownEntry("Short.Name", typeof(ShortNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Throwing.Name", typeof(ThrowingNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Throwing.Count", typeof(ThrowingCountConfig), typeof(int)));
        services.AddSingleton(new ConfigAuditKnownEntry("Leaky.Name", typeof(LeakyNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Leaky.Throwing", typeof(LeakyThrowingConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Mismatched.Name", typeof(ShortNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Mismatched.Count", typeof(RetryCountConfig), typeof(int)));
        services.AddSingleton(new ConfigAuditKnownEntry("Default.Port", typeof(DefaultPortConfig), typeof(int)));
        services.AddSingleton(new ConfigAuditKnownEntry("Broken.Wrapper", typeof(UnconstructableConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Throwing.Wrapper", typeof(ThrowingConstructorConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Plain.Wrapper", typeof(object), typeof(string)));
        services.AddConfigAuditKey<object>("Object.String");
        services.AddConfigAuditKey<OddShape>("Odd.Shape");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.Contains(
            AssertEntry(report, "Short.Name", ConfigAuditEntryState.Invalid, "no").Diagnostics,
            diagnostic => diagnostic.Code == "config-validation-failed");
        Assert.Contains(
            AssertEntry(report, "Throwing.Name", ConfigAuditEntryState.Invalid, "valid-name").Diagnostics,
            diagnostic => diagnostic.Code == "config-validation-threw");
        Assert.Contains(
            AssertEntry(report, "Throwing.Count", ConfigAuditEntryState.Invalid, "7").Diagnostics,
            diagnostic => diagnostic.Code == "config-validation-threw");
        Assert.All(
            AssertEntry(report, "Leaky.Name", ConfigAuditEntryState.Invalid, "super-secret").Diagnostics,
            diagnostic => Assert.DoesNotContain("super-secret", diagnostic.Message, StringComparison.Ordinal));
        Assert.All(
            AssertEntry(report, "Leaky.Throwing", ConfigAuditEntryState.Invalid, "super-secret").Diagnostics,
            diagnostic => Assert.DoesNotContain("super-secret", diagnostic.Message, StringComparison.Ordinal));
        Assert.Contains(
            AssertEntry(report, "Mismatched.Name", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-value-type-mismatch");
        Assert.Contains(
            AssertEntry(report, "Mismatched.Count", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-value-type-mismatch");
        Assert.Contains(
            AssertEntry(report, "Default.Port", ConfigAuditEntryState.Defaulted, "8080").Sources,
            source => source.Kind == ConfigAuditSourceKind.Default);
        Assert.Contains(
            AssertEntry(report, "Broken.Wrapper", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-wrapper-create-failed");
        Assert.All(
            AssertEntry(report, "Throwing.Wrapper", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => Assert.DoesNotContain("super-secret", diagnostic.Message, StringComparison.Ordinal));
        AssertEntry(report, "Plain.Wrapper", ConfigAuditEntryState.Missing, null);

        var stringObject = AssertEntry(report, "Object.String", ConfigAuditEntryState.Resolved, "plain");
        Assert.Empty(stringObject.Children);

        var oddShape = AssertEntry(report, "Odd.Shape", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(oddShape.Children, child => child.Key == "Odd.Shape.GoodProperty");
        Assert.Contains(oddShape.Children, child => child.Key == "Odd.Shape.MutableField");
        Assert.DoesNotContain(oddShape.Children, child => child.Key == "Odd.Shape.BadProperty");
        Assert.DoesNotContain(oddShape.Children, child => child.Key == "Odd.Shape.ReadOnlyField");

        var rendered = new ConfigAuditTextRenderer().Render(
            new ConfigAuditReport
            {
                Environment = "Production",
                GeneratedAt = DateTimeOffset.UtcNow,
                Providers = [],
                Entries =
                [
                    new ConfigAuditEntry
                    {
                        Key = "Provider.Source",
                        State = ConfigAuditEntryState.Resolved,
                        Sources =
                        [
                            new ConfigAuditSourceRecord
                            {
                                Kind = ConfigAuditSourceKind.Provider,
                                ProviderName = "CustomProvider",
                                ConfigPath = "Provider.Source",
                                AppliedToPath = "Provider.Source",
                                Role = ConfigAuditSourceRole.Base
                            }
                        ]
                    }
                ],
                Redaction = new ConfigAuditRedaction
                {
                    Enabled = true,
                    MatchedFragments = [],
                    Placeholder = "[redacted]"
                }
            });
        Assert.Contains("CustomProvider", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_TreatsNullFromGenericProviderAsMissing()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new MissingStringProvider());
        services.AddConfigAuditKey<string>("Provider.Missing");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        AssertEntry(report, "Provider.Missing", ConfigAuditEntryState.Missing, null);
    }

    [Fact]
    public void GetReport_ContinuesAfterInvalidProviderWhenLowerPriorityProviderResolves()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new InvalidDiagnosticProvider("Fallback.Port", priority: 30));
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Fallback.Port", 443));
        services.AddConfigAuditKey<int>("Fallback.Port");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Fallback.Port", ConfigAuditEntryState.Resolved, "443");
        Assert.Contains(entry.Sources, source => source.ProviderName == nameof(StaticConfigProvider));
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-invalid");
    }

    [Fact]
    public void GetReport_ConvertsProviderExceptionsToDiagnostics()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new ThrowingConfigProvider("Provider.Throws"));
        services.AddSingleton<IConfigProvider>(new ThrowingDiagnosticProvider("Diagnostic.Throws"));
        services.AddConfigAuditKey<string>("Provider.Throws");
        services.AddConfigAuditKey<string>("Diagnostic.Throws");
        services.AddSingleton(new ConfigAuditKnownEntry("Provider.BadType", null, typeof(void)));

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "config-provider-diagnostics-threw");
        Assert.DoesNotContain(
            report.Diagnostics.Concat(report.Entries.SelectMany(entry => entry.Diagnostics)),
            diagnostic => diagnostic.Message.Contains("super-secret", StringComparison.Ordinal));
        Assert.Contains(
            AssertEntry(report, "Provider.Throws", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-provider-get-value-threw");
        Assert.Contains(
            AssertEntry(report, "Diagnostic.Throws", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-provider-resolve-threw");
        Assert.Contains(
            AssertEntry(report, "Provider.BadType", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-provider-get-value-threw");
    }

    [Fact]
    public void GetReport_ConvertsPatchExceptionsToDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentConfigProvider>(new ThrowingPatchEnvironmentProvider());
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider(
            "MyApp.Settings",
            new AppSettings
            {
                Mode = "file"
            }));
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddConfigAuditKey<AppSettings>("MyApp.Settings");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-provider-patch-threw");
        Assert.DoesNotContain(
            entry.Diagnostics,
            diagnostic => diagnostic.Message.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void GetReport_DoesNotExpandCyclesIndefinitely()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var options = new CyclicOptions { Name = "root" };
        options.Self = options;

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Cycle.Options", options));
        services.AddConfigAuditKey<CyclicOptions>("Cycle.Options");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Cycle.Options", ConfigAuditEntryState.Resolved, null);
        Assert.Equal("root", entry.Children.Single(child => child.Key == "Cycle.Options.Name").DisplayValue);
        Assert.Empty(entry.Children.Single(child => child.Key == "Cycle.Options.Self").Children);
    }

    [Fact]
    public void GetReport_MarksResolvedEntryPartialWhenChildSourcesContainPatch()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new PatchSourceProvider());
        services.AddConfigAuditKey<AppSettings>("Patch.Source");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        AssertEntry(report, "Patch.Source", ConfigAuditEntryState.PartiallyResolved, null);
    }

    [Fact]
    public void Redactor_FallsBackWhenEnumerableCannotSerialize()
    {
        var redactor = new ConfigAuditRedactor();

        var formatted = redactor.FormatValue("Values", new ThrowingEnumerable(), []);
        var json = redactor.FormatValue("Values", new JsonExceptionEnumerable(), []);
        var invalidOperation = redactor.FormatValue("Values", new InvalidOperationEnumerable(), []);
        var throwingToString = redactor.FormatValue("Values", new ThrowingToStringEnumerable(), []);

        Assert.Equal(nameof(ThrowingEnumerable), formatted.DisplayValue);
        Assert.False(formatted.IsRedacted);
        Assert.Equal(nameof(JsonExceptionEnumerable), json.DisplayValue);
        Assert.False(json.IsRedacted);
        Assert.Equal(nameof(InvalidOperationEnumerable), invalidOperation.DisplayValue);
        Assert.False(invalidOperation.IsRedacted);
        Assert.Equal(nameof(ThrowingToStringEnumerable), throwingToString.DisplayValue);
        Assert.False(throwingToString.IsRedacted);
    }

    [Fact]
    public void Redactor_CreatePolicyReturnsFragmentSnapshot()
    {
        var redactor = new ConfigAuditRedactor();

        var first = redactor.CreatePolicy();
        var fragments = Assert.IsType<string[]>(first.MatchedFragments);
        fragments[0] = "not-password";

        var second = redactor.CreatePolicy();

        Assert.Contains("password", second.MatchedFragments);
        Assert.DoesNotContain("not-password", second.MatchedFragments);
    }

    private static ServiceCollection CreateServices(string configDirectory, IEnvironmentProvider environment)
    {
        var services = new ServiceCollection();
        var locationProvider = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => locationProvider.Directory).Returns(configDirectory);

        services.AddSingleton(environment);
        services.AddSingleton(locationProvider);
        services.AddSingleton(A.Fake<ILogger<FileBasedConfigProvider>>());
        services.AddSingleton<IEnvironmentConfigProvider, EnvironmentConfigProvider>();
        services.AddSingleton<IConfigProvider, FileBasedConfigProvider>();
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddSingleton<ConfigAuditTextRenderer>();
        return services;
    }

    private static ConfigAuditEntry AssertEntry(
        ConfigAuditReport report,
        string key,
        ConfigAuditEntryState state,
        string? displayValue)
    {
        var entry = Assert.Single(report.Entries, entry => entry.Key == key);
        Assert.Equal(state, entry.State);
        Assert.Equal(displayValue, entry.DisplayValue);
        return entry;
    }

    private sealed class StaticConfigProvider : IConfigProvider
    {
        private readonly string _key;
        private readonly object _value;

        public StaticConfigProvider(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public int Priority => 20;

        public string Name => nameof(StaticConfigProvider);

        public T? GetValue<T>(string environment, string key)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
            {
                return default;
            }

            return (T)_value;
        }
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

        public T? GetValue<T>(string environment, string key) =>
            _values.TryGetValue(key, out var value) ? (T?)value : default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (!_values.TryGetValue(key, out var value))
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
                        ConfigPath = key,
                        AppliedToPath = key,
                        Role = role
                    }
                ],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class MissingStringProvider : IConfigProvider
    {
        public int Priority => 10;

        public string Name => nameof(MissingStringProvider);

        public T? GetValue<T>(string environment, string key) => default;
    }

    private sealed class InvalidDiagnosticProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;

        public InvalidDiagnosticProvider(string key, int priority)
        {
            _key = key;
            Priority = priority;
        }

        public int Priority { get; }

        public string Name => nameof(InvalidDiagnosticProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
            {
                return ConfigValueResolution.Missing(key);
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Invalid,
                null,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = key,
                        AppliedToPath = key,
                        Role = role
                    }
                ],
                [
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = "config-provider-invalid",
                        Key = key,
                        ConfigPath = key,
                        Message = "The provider value was invalid."
                    }
                ]);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class ThrowingConfigProvider : IConfigProvider
    {
        private readonly string _key;

        public ThrowingConfigProvider(string key)
        {
            _key = key;
        }

        public int Priority => 30;

        public string Name => nameof(ThrowingConfigProvider);

        public T? GetValue<T>(string environment, string key)
        {
            if (string.Equals(_key, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("provider value failed with super-secret");
            }

            return default;
        }
    }

    private sealed class ThrowingDiagnosticProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;

        public ThrowingDiagnosticProvider(string key)
        {
            _key = key;
        }

        public int Priority => 25;

        public string Name => nameof(ThrowingDiagnosticProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (string.Equals(_key, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("provider resolve failed with super-secret");
            }

            return ConfigValueResolution.Missing(key);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) =>
            throw new InvalidOperationException("provider diagnostics failed with super-secret");
    }

    private sealed class PatchSourceProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 35;

        public string Name => nameof(PatchSourceProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (!string.Equals(key, "Patch.Source", StringComparison.Ordinal))
            {
                return ConfigValueResolution.Missing(key);
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                new AppSettings
                {
                    Mode = "patched"
                },
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = "Patch.Source.Mode",
                        AppliedToPath = "Patch.Source.Mode",
                        Role = ConfigAuditSourceRole.Patch
                    }
                ],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class ThrowingPatchEnvironmentProvider : IEnvironmentConfigProvider, IConfigDiagnosticProvider, IConfigDiagnosticPatcher
    {
        public int Priority => -1;

        public string Name => nameof(ThrowingPatchEnvironmentProvider);

        public string Environment => "Production";

        public bool IsDevelopment => false;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role) =>
            ConfigValueResolution.Missing(key);

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

        public ConfigPatchDiagnosticResult TracePatch(
            string environment,
            string key,
            object? currentValue,
            Type valueType) =>
            throw new InvalidOperationException("patch failed with super-secret");
    }

    private sealed class ThrowingEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override string ToString() => nameof(ThrowingEnumerable);
    }

    private sealed class JsonExceptionEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new JsonException();

        public override string ToString() => nameof(JsonExceptionEnumerable);
    }

    private sealed class InvalidOperationEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new InvalidOperationException();

        public override string ToString() => nameof(InvalidOperationEnumerable);
    }

    private sealed class ThrowingToStringEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new JsonException();

        public override string ToString() => throw new InvalidOperationException();
    }
}
