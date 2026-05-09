using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Config.Tests;

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

            var port = settings.Children
                .Single(child => child.Key == "MyApp.Settings.Database")
                .Children
                .Single(child => child.Key == "MyApp.Settings.Database.Port");
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
                    ["Object.String"] = "plain",
                    ["Odd.Shape"] = new OddShape()
                }));
        services.AddSingleton(new ConfigAuditKnownEntry("Short.Name", typeof(ShortNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Throwing.Name", typeof(ThrowingNameConfig), typeof(string)));
        services.AddSingleton(new ConfigAuditKnownEntry("Throwing.Count", typeof(ThrowingCountConfig), typeof(int)));
        services.AddSingleton(new ConfigAuditKnownEntry("Default.Port", typeof(DefaultPortConfig), typeof(int)));
        services.AddSingleton(new ConfigAuditKnownEntry("Broken.Wrapper", typeof(UnconstructableConfig), typeof(string)));
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
        Assert.Contains(
            AssertEntry(report, "Default.Port", ConfigAuditEntryState.Defaulted, "8080").Sources,
            source => source.Kind == ConfigAuditSourceKind.Default);
        Assert.Contains(
            AssertEntry(report, "Broken.Wrapper", ConfigAuditEntryState.Invalid, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-wrapper-create-failed");
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
    public void Redactor_FallsBackWhenEnumerableCannotSerialize()
    {
        var redactor = new ConfigAuditRedactor();

        var formatted = redactor.FormatValue("Values", new ThrowingEnumerable(), []);

        Assert.Equal(nameof(ThrowingEnumerable), formatted.DisplayValue);
        Assert.False(formatted.IsRedacted);
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

    private sealed class ThrowingEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override string ToString() => nameof(ThrowingEnumerable);
    }
}
