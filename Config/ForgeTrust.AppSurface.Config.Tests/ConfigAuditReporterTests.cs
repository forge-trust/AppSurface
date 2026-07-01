using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigAuditReporterTests
{
    private const string CorrelationSecretA = "0123456789abcdef0123456789abcdef";
    private const string CorrelationSecretB = "abcdef0123456789abcdef0123456789";

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

    private sealed class EndpointSettings
    {
        public List<string> Endpoints { get; set; } = [];
    }

    private sealed class FieldEndpointSettings
    {
        public List<string> Endpoints = [];
    }

    private sealed class ScalarEndpointSettings
    {
        public string Endpoint { get; set; } = string.Empty;
    }

    private sealed class ReadOnlyFieldEndpointSettings
    {
        public ReadOnlyFieldEndpointSettings(params string[] endpoints)
        {
            Endpoints = [.. endpoints];
        }

        public readonly List<string> Endpoints;
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

    private sealed class ServiceSecretOptions
    {
        public string Name { get; set; } = "billing";

        public string Password { get; set; } = "password-from-collection";

        public string Token { get; set; } = "token-from-collection";

        public string Secret { get; set; } = "secret-from-collection";

        public string ApiKey { get; set; } = "api-key-from-collection";

        public NestedSecretOptions Nested { get; set; } = new();
    }

    private sealed class NestedSecretOptions
    {
        public string Secret { get; set; } = "nested-secret-from-collection";
    }

    private sealed class ReadOnlyValues : IReadOnlyList<string>
    {
        private readonly string[] _values;

        public ReadOnlyValues(params string[] values)
        {
            _values = values.Length == 0 ? ["first", "second"] : values;
        }

        public string this[int index] => _values[index];

        public int Count => _values.Length;

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PropertyBudgetShape
    {
        public string First { get; set; } = "first";

        public string Second { get; set; } = "second";
    }

    private sealed class FieldBudgetShape
    {
        public string First = string.Empty;

        public string Second = string.Empty;
    }

    private readonly struct StructShape
    {
        public string Name { get; init; }
    }

    private sealed class NamedEndpoint
    {
        public string? Name { get; set; }

        public string? Url { get; set; }

        public string? Password { get; set; }
    }

    [ConfigKey("Default.Services", root: true)]
    private sealed class DefaultServicesConfig : Config<List<NamedEndpoint>>
    {
        public override List<NamedEndpoint>? DefaultValue =>
        [
            new()
            {
                Name = "fallback",
                Url = "https://fallback.example"
            }
        ];
    }

    [Fact]
    public void GetReport_WithContractFixture_ReportsStatesSourcesPatchesAndRedaction()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Staging.json"),
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
            File.WriteAllText(Path.Join(tempDir, "appsettings.Staging.Broken.json"), "{");
            File.WriteAllText(Path.Join(tempDir, "appsettings.Development.Broken.json"), "{");
            File.WriteAllText(Path.Join(tempDir, "appsettings.Development.Array.json"), "[1, 2, 3]");
            File.WriteAllText(
                Path.Join(tempDir, "config_Override.Staging.json"),
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
            Assert.DoesNotContain(
                report.Diagnostics,
                diagnostic => diagnostic.Message.Contains("appsettings.Development.Broken.json", StringComparison.Ordinal)
                              || diagnostic.Message.Contains("appsettings.Development.Array.json", StringComparison.Ordinal));
            var feature = AssertEntry(report, "Feature.Enabled", ConfigAuditEntryState.Resolved, "True");
            var featureSource = Assert.Single(feature.Sources, source => source.Kind == ConfigAuditSourceKind.File);
            AssertLocation(featureSource, lineNumber: 3, byteColumnNumber: 5);
            Assert.DoesNotContain(
                report.Entries.SelectMany(entry => entry.Diagnostics),
                diagnostic => diagnostic.Code == "config-file-null-skipped");

            var billing = AssertEntry(report, "Billing.Endpoint", ConfigAuditEntryState.Resolved, "https://staging-billing.example");
            Assert.Contains(billing.Sources, source => source.EnvironmentVariableName == "STAGING__BILLING__ENDPOINT");

            var settings = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null);
            Assert.Contains(settings.Sources, source => source.FilePath?.EndsWith("appsettings.Staging.json", StringComparison.Ordinal) == true);
            Assert.Contains(settings.Sources, source => source.EnvironmentVariableName == "MYAPP__SETTINGS__DATABASE__PORT");
            Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Message.Contains("MYAPP__SETTINGS__DATABASE__TIMEOUTSECONDS", StringComparison.Ordinal));
            var settingsFileSource = Assert.Single(
                settings.Sources,
                source => source.FilePath?.EndsWith("appsettings.Staging.json", StringComparison.Ordinal) == true);
            AssertLocation(settingsFileSource, lineNumber: 6, byteColumnNumber: 5);

            var database = settings.Children
                .Single(child => child.Key == "MyApp.Settings.Database");
            Assert.Equal(ConfigAuditEntryState.PartiallyResolved, database.State);
            var host = database.Children.Single(child => child.Key == "MyApp.Settings.Database.Host");
            var hostFileSource = Assert.Single(host.Sources, source => source.Kind == ConfigAuditSourceKind.File);
            AssertLocation(hostFileSource, lineNumber: 9, byteColumnNumber: 9);

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
            Assert.Contains("appsettings.Staging.json:6:5 :: MyApp.Settings", rendered, StringComparison.Ordinal);
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
    public void Redactor_OmitsNonSensitiveCollections()
    {
        var redactor = new ConfigAuditRedactor();

        var formatted = redactor.FormatValue("Values", new[] { "one", "two", "three" }, []);

        Assert.Null(formatted.DisplayValue);
        Assert.False(formatted.IsRedacted);
    }

    [Fact]
    public void Redactor_RedactsCollectionsWhenSourceMetadataIsSensitive()
    {
        var redactor = new ConfigAuditRedactor();

        var redacted = redactor.FormatValue(
            "Values",
            new[] { "one", "two", "three" },
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.Provider,
                    ProviderName = "SecretProvider",
                    ConfigPath = "Values",
                    AppliedToPath = "Values",
                    Role = ConfigAuditSourceRole.Base,
                    Sensitivity = ConfigAuditSensitivity.Sensitive
                }
            ]);

        Assert.True(redacted.IsRedacted);
        Assert.Equal("[redacted]", redacted.DisplayValue);
    }

    [Fact]
    public void Redactor_RedactsExplicitSensitiveEntriesAndNonSensitiveDoesNotDowngradeFragments()
    {
        var redactor = new ConfigAuditRedactor();

        var explicitSensitive = redactor.FormatValue(
            "Partner.Payload",
            "partner-assertion",
            [],
            ConfigAuditSensitivity.Sensitive);
        var nonSensitiveFragment = redactor.FormatValue(
            "Payment.ApiKey",
            "not-safe",
            [],
            ConfigAuditSensitivity.NonSensitive);

        Assert.True(explicitSensitive.IsRedacted);
        Assert.Equal("[redacted]", explicitSensitive.DisplayValue);
        Assert.True(nonSensitiveFragment.IsRedacted);
        Assert.Equal("[redacted]", nonSensitiveFragment.DisplayValue);
    }

    [Theory]
    [InlineData("Partner.Passphrase")]
    [InlineData("Partner.Dsn")]
    [InlineData("Partner.Assertion")]
    [InlineData("Partner.Certificate")]
    [InlineData("Partner.ClientSecret")]
    [InlineData("Partner.SharedAccessSignature")]
    [InlineData("Partner.Cookie")]
    public void Redactor_RedactsExpandedSensitiveFragments(string key)
    {
        var redactor = new ConfigAuditRedactor();

        var redacted = redactor.FormatValue(key, "secret-value", []);

        Assert.True(redacted.IsRedacted);
        Assert.Equal("[redacted]", redacted.DisplayValue);
    }

    [Fact]
    public void GetReport_RedactsExplicitSensitiveProviderOnlyKeyBeforeStructuredAndTextOutput()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Partner.Payload"] = "partner-assertion-value"
                }));
        services.AddConfigAuditKey<string>(
            "Partner.Payload",
            options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
        var serialized = JsonSerializer.Serialize(report);

        var entry = AssertEntry(report, "Partner.Payload", ConfigAuditEntryState.Resolved, "[redacted]");
        Assert.True(entry.IsRedacted);
        Assert.DoesNotContain("partner-assertion-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("partner-assertion-value", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_OmitsCollectionDisplayValuesWithoutLeakingNestedSecrets()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Services"] = new List<ServiceSecretOptions>
                    {
                        new()
                    }
                }));
        services.AddConfigAuditKey<List<ServiceSecretOptions>>("Services");

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

        var entry = AssertEntry(report, "Services", ConfigAuditEntryState.Resolved, null);
        Assert.False(entry.IsRedacted);
        Assert.Empty(entry.Children);
        var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
        Assert.DoesNotContain("password-from-collection", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token-from-collection", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-from-collection", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-from-collection", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret-from-collection", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_TraversesOptInListElementsWithExactFileSources()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Production.json"),
                """
                {
                  "Services": [
                    {
                      "Name": "billing",
                      "Url": "https://billing.example",
                      "Password": "first-secret"
                    },
                    {
                      "Name": "search",
                      "Url": "https://search.example",
                      "Password": "second-secret"
                    },
                    null
                  ]
                }
                """);
            var environment = A.Fake<IEnvironmentProvider>();
            A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
            var services = CreateServices(tempDir, environment);
            services.AddConfigAuditKey<List<NamedEndpoint>>(
                "Services",
                options => options.TraverseCollectionElements = true);

            var provider = services.BuildServiceProvider();
            var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

            var entry = AssertEntry(report, "Services", ConfigAuditEntryState.Resolved, null);
            Assert.Equal(["Services[0]", "Services[1]", "Services[2]"], entry.Children.Select(child => child.Key));
            Assert.All(entry.Children, child => Assert.Equal(ConfigAuditElementKind.ListItem, child.Element?.Kind));
            Assert.Equal(0, entry.Children[0].Element?.Index);
            Assert.Equal(2, entry.Children[2].Element?.Index);
            Assert.Null(entry.Children[2].DisplayValue);
            Assert.Empty(entry.Children[2].Children);
            Assert.Contains(entry.Children[2].Sources, source => source.ConfigPath == "Services.2");
            Assert.Contains(entry.Children[0].Sources, source => source.ConfigPath == "Services.0");
            Assert.Equal("billing", entry.Children[0].Children.Single(child => child.Key == "Services[0].Name").DisplayValue);
            Assert.Contains(
                entry.Children[0].Children.Single(child => child.Key == "Services[0].Name").Sources,
                source => source.ConfigPath == "Services.0.Name");
            Assert.True(entry.Children[0].Children.Single(child => child.Key == "Services[0].Password").IsRedacted);

            var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
            Assert.DoesNotContain("first-secret", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("second-secret", rendered, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetReport_RedactsSensitiveDictionaryKeysBeforePublicFields()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Tenants"] = new Dictionary<string, string?>
                    {
                        ["tenant-secret-token"] = "alpha",
                        ["tenant-password"] = "beta",
                        ["safe.name"] = "visible",
                        ["quoted\"name"] = "quoted",
                        ["bracket[name]"] = "bracket",
                        ["0"] = "numeric-looking"
                    }
                }));
        services.AddConfigAuditKey<Dictionary<string, string?>>(
            "Tenants",
            options => options.TraverseCollectionElements = true);

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(entry.Children, child => child.Key == "Tenants[[redacted-key-1]]");
        Assert.Contains(entry.Children, child => child.Key == "Tenants[[redacted-key-2]]");
        Assert.Contains(entry.Children, child => child.Key == "Tenants[\"safe.name\"]");
        Assert.Contains(entry.Children, child => child.Key == "Tenants[\"quoted\\\"name\"]");
        Assert.Contains(entry.Children, child => child.Key == "Tenants[\"bracket[name]\"]");
        Assert.Contains(entry.Children, child => child.Key == "Tenants[\"0\"]");
        Assert.All(
            entry.Children.Where(child => child.Element?.IsKeyRedacted == true),
            child =>
            {
                Assert.True(child.IsRedacted);
                Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-source-inherited");
            });

        var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("tenant-secret-token", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-password", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-password", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_LeavesDictionaryKeyCorrelationAbsentByDefault()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            enableEntryCorrelation: false);

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);

        Assert.Null(child.Element?.KeyCorrelationId);
        Assert.Null(child.Element?.ComparisonKeyCorrelationId);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, report.Redaction.DictionaryKeyCorrelationMode);
        Assert.Null(report.Redaction.DictionaryKeyCorrelationKeyId);
        Assert.Null(report.Redaction.DictionaryKeyCorrelationApplicationScope);
    }

    [Fact]
    public void GetReport_UsesEffectiveDictionaryKeyCorrelationPolicyForRedactionMetadata()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.Configure<ConfigAuditDictionaryKeyCorrelationOptions>(options =>
        {
            options.SecretKey = CorrelationSecretA;
            options.KeyId = "kid-a";
            options.ApplicationScope = "app-a";
        });
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Tenants"] = new Dictionary<string, string>
                    {
                        ["tenant-secret-token"] = "alpha-secret-value"
                    }
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Tenants",
            options =>
            {
                options.TraverseCollectionElements = false;
                options.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);

        Assert.Empty(entry.Children);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.None, report.Redaction.DictionaryKeyCorrelationMode);
        Assert.Null(report.Redaction.DictionaryKeyCorrelationKeyId);
        Assert.Null(report.Redaction.DictionaryKeyCorrelationApplicationScope);
        Assert.Contains(
            entry.Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-options-invalid"
                          && diagnostic.Message.Contains(
                              nameof(ConfigAuditEntryOptions.DictionaryKeyCorrelationMode),
                              StringComparison.Ordinal));
        Assert.DoesNotContain(
            entry.Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-key-correlation-unavailable");
    }

    [Fact]
    public void GetReport_CreatesStableScopedDictionaryKeyCorrelationIds()
    {
        var firstReport = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token");
        var first = GetSingleCorrelationId(firstReport);
        var firstComparison = GetSingleComparisonCorrelationId(firstReport);
        var duplicate = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var duplicateComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var differentKey = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-password"));
        var differentKeyComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-password"));

        Assert.Equal(first, duplicate);
        Assert.Equal(firstComparison, duplicateComparison);
        Assert.NotEqual(first, differentKey);
        Assert.NotEqual(firstComparison, differentKeyComparison);
        Assert.StartsWith("v1:kid-a:", first, StringComparison.Ordinal);
        Assert.Equal("v1:kid-a:".Length + 24, first.Length);
        Assert.StartsWith("v1c:kid-a:", firstComparison, StringComparison.Ordinal);
        Assert.Equal("v1c:kid-a:".Length + 24, firstComparison.Length);
        Assert.Equal(ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac, firstReport.Redaction.DictionaryKeyCorrelationMode);
        Assert.Equal("kid-a", firstReport.Redaction.DictionaryKeyCorrelationKeyId);
        Assert.Equal("app-a", firstReport.Redaction.DictionaryKeyCorrelationApplicationScope);
    }

    [Fact]
    public void GetReport_ScopesDictionaryKeyCorrelationIds()
    {
        var baseline = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var baselineComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var changedEnvironment = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Staging",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var changedEnvironmentComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Staging",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token"));
        var changedRootKey = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Accounts",
            rawDictionaryKey: "tenant-secret-token"));
        var changedRootKeyComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Accounts",
            rawDictionaryKey: "tenant-secret-token"));
        var changedScope = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            applicationScope: "other-app"));
        var changedScopeComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            applicationScope: "other-app"));
        var changedKeyId = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            keyId: "kid-b"));
        var changedKeyIdComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            keyId: "kid-b"));
        var changedSecret = GetSingleCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            secretKey: CorrelationSecretB));
        var changedSecretComparison = GetSingleComparisonCorrelationId(CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            secretKey: CorrelationSecretB));

        Assert.NotEqual(baseline, changedEnvironment);
        Assert.Equal(baselineComparison, changedEnvironmentComparison);
        Assert.NotEqual(baseline, changedRootKey);
        Assert.NotEqual(baselineComparison, changedRootKeyComparison);
        Assert.NotEqual(baseline, changedScope);
        Assert.NotEqual(baselineComparison, changedScopeComparison);
        Assert.NotEqual(baseline, changedKeyId);
        Assert.NotEqual(baselineComparison, changedKeyIdComparison);
        Assert.NotEqual(baseline, changedSecret);
        Assert.NotEqual(baselineComparison, changedSecretComparison);
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationReportsMissingGlobalKeyMaterial()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            configureGlobalOptions: false,
            enableEntryCorrelation: true);

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);

        Assert.Null(child.Element?.KeyCorrelationId);
        Assert.Null(child.Element?.ComparisonKeyCorrelationId);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-audit-key-correlation-unavailable");
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationReportsInvalidGlobalKeyMaterial()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            secretKey: "too-short");

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);

        Assert.Null(child.Element?.KeyCorrelationId);
        Assert.Null(child.Element?.ComparisonKeyCorrelationId);
        Assert.Contains(
            entry.Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-key-correlation-unavailable"
                          && diagnostic.Message.Contains("at least 32 UTF-8 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationRejectsUnsafeKeyIds()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            keyId: "kid-a\nforged-line");

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);
        var rendered = new ConfigAuditTextRenderer().Render(report);

        Assert.Null(child.Element?.KeyCorrelationId);
        Assert.Null(child.Element?.ComparisonKeyCorrelationId);
        Assert.Null(report.Redaction.DictionaryKeyCorrelationKeyId);
        Assert.Contains(
            entry.Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-key-correlation-unavailable"
                          && diagnostic.Message.Contains("ASCII letters", StringComparison.Ordinal));
        Assert.DoesNotContain("forged-line", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationUsesTrimmedMetadata()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token",
            applicationScope: " app-a ",
            keyId: " kid-a ");
        var correlationId = GetSingleCorrelationId(report);
        var comparisonCorrelationId = GetSingleComparisonCorrelationId(report);

        Assert.StartsWith("v1:kid-a:", correlationId, StringComparison.Ordinal);
        Assert.StartsWith("v1c:kid-a:", comparisonCorrelationId, StringComparison.Ordinal);
        Assert.Equal("kid-a", report.Redaction.DictionaryKeyCorrelationKeyId);
        Assert.Equal("app-a", report.Redaction.DictionaryKeyCorrelationApplicationScope);
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationDoesNotExposeRawKeys()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-secret-token");

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);
        var correlationId = child.Element?.KeyCorrelationId;
        var comparisonCorrelationId = child.Element?.ComparisonKeyCorrelationId;
        Assert.NotNull(correlationId);
        Assert.NotNull(comparisonCorrelationId);
        var rendered = new ConfigAuditTextRenderer().Render(report);
        var serialized = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(correlationId!, child.Key, StringComparison.Ordinal);
        Assert.DoesNotContain(comparisonCorrelationId!, child.Key, StringComparison.Ordinal);
        Assert.Contains($"Key correlation: {correlationId}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-token", child.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-token", child.Element?.KeyLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-token", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-secret-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-secret-value", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationWorksWhenDictionaryKeyDisplayIsDisabled()
    {
        var report = CreateCorrelationReport(
            environmentName: "Production",
            rootKey: "Tenants",
            rawDictionaryKey: "tenant-a",
            displayDictionaryKeys: false);

        var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);
        var correlationId = child.Element?.KeyCorrelationId;
        var comparisonCorrelationId = child.Element?.ComparisonKeyCorrelationId;
        Assert.NotNull(correlationId);
        Assert.NotNull(comparisonCorrelationId);
        var rendered = new ConfigAuditTextRenderer().Render(report);
        var serialized = JsonSerializer.Serialize(report);

        Assert.Equal("Tenants[[key]]", child.Key);
        Assert.Equal("[key]", child.Element?.KeyLabel);
        Assert.True(child.Element?.IsKeyRedacted);
        Assert.Contains($"Key correlation: {correlationId}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", child.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", child.Element?.KeyLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_DictionaryKeyCorrelationOmitsIdsForUnprintableDictionaryKeys()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.Configure<ConfigAuditDictionaryKeyCorrelationOptions>(options =>
        {
            options.SecretKey = CorrelationSecretA;
            options.KeyId = "kid-a";
            options.ApplicationScope = "app-a";
        });
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Labels.Items"] = new Hashtable
                    {
                        [new ThrowingDictionaryKey()] = "throwing",
                        [new FormatFailingDictionaryKey()] = "format",
                        [new NullReturningDictionaryKey()] = "null",
                        ["safe"] = "safe"
                    }
                }));
        services.AddConfigAuditKey<Hashtable>(
            "Labels.Items",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var children = AssertEntry(report, "Labels.Items", ConfigAuditEntryState.Resolved, null).Children;
        var unprintableChildren = children
            .Where(child => child.Element?.KeyLabel == "[key]")
            .ToList();
        var safeChild = Assert.Single(children, child => child.Element?.KeyLabel == "safe");

        Assert.Equal(3, unprintableChildren.Count);
        Assert.All(unprintableChildren, child => Assert.Null(child.Element?.KeyCorrelationId));
        Assert.All(unprintableChildren, child => Assert.Null(child.Element?.ComparisonKeyCorrelationId));
        Assert.NotNull(safeChild.Element?.KeyCorrelationId);
        Assert.NotNull(safeChild.Element?.ComparisonKeyCorrelationId);
    }

    [Fact]
    public void GetReport_ManualOptionOverridesWrapperDictionaryCorrelationPolicy()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.Configure<ConfigAuditDictionaryKeyCorrelationOptions>(options =>
        {
            options.SecretKey = CorrelationSecretA;
            options.KeyId = "kid-a";
            options.ApplicationScope = "app-a";
        });
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Tenants"] = new Dictionary<string, string>
                    {
                        ["tenant-secret-token"] = "alpha-secret-value"
                    }
                }));
        services.AddSingleton(new ConfigAuditKnownEntry(
            "Tenants",
            typeof(object),
            typeof(Dictionary<string, string>),
            new ConfigAuditEntryOptions
            {
                TraverseCollectionElements = true,
                DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.None
            }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "tenants",
            options => options.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.NotNull(GetSingleCorrelationId(report));
    }

    [Fact]
    public void GetReport_RedactsSensitiveFileDiagnosticPathsBeforePublicFields()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Production.json"),
                """
                {
                  "Tenants": {
                    "password": null
                  }
                }
                """);

            var environment = A.Fake<IEnvironmentProvider>();
            A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

            var services = CreateServices(tempDir, environment);
            services.AddConfigAuditKey<Dictionary<string, string>>(
                "Tenants",
                options => options.TraverseCollectionElements = true);

            var report = services.BuildServiceProvider()
                .GetRequiredService<IConfigAuditReporter>()
                .GetReport("Production");

            var entry = AssertEntry(report, "Tenants", ConfigAuditEntryState.Resolved, null);
            var diagnostic = Assert.Single(entry.Diagnostics, item => item.Code == "config-file-null-skipped");
            var rendered = new ConfigAuditTextRenderer().Render(report);

            Assert.Equal("Tenants.[redacted-key]", diagnostic.ConfigPath);
            Assert.DoesNotContain("password", diagnostic.ConfigPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetReport_TraversesNonStringDictionaryKeysAndCanHideLabels()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Codes"] = new Hashtable
                    {
                        [7] = "seven",
                        [true] = "enabled"
                    },
                    ["Hidden"] = new Dictionary<string, string>
                    {
                        ["public-name"] = "visible"
                    },
                    ["HiddenNested"] = new Dictionary<string, object?>
                    {
                        ["public-name"] = new Dictionary<string, string>
                        {
                            ["child-name"] = "visible"
                        }
                    }
                }));
        services.AddConfigAuditKey<Hashtable>(
            "Codes",
            options => options.TraverseCollectionElements = true);
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Hidden",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.DisplayDictionaryKeys = false;
            });
        services.AddConfigAuditKey<Dictionary<string, object?>>(
            "HiddenNested",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.DisplayDictionaryKeys = false;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var codes = AssertEntry(report, "Codes", ConfigAuditEntryState.Resolved, null).Children;
        Assert.Equal(2, codes.Count);
        Assert.Contains(codes, code => code.Key == "Codes[\"7\"]"
                                       && code.Element is { KeyLabel: "7", IsKeyRedacted: false });
        Assert.Contains(codes, code => code.Key == "Codes[\"True\"]"
                                       && code.Element is { KeyLabel: "True", IsKeyRedacted: false });

        var hidden = Assert.Single(AssertEntry(report, "Hidden", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("Hidden[[key]]", hidden.Key);
        Assert.Equal("[key]", hidden.Element?.KeyLabel);
        Assert.True(hidden.Element?.IsKeyRedacted);

        var hiddenParent = Assert.Single(AssertEntry(report, "HiddenNested", ConfigAuditEntryState.Resolved, null).Children);
        var hiddenChild = Assert.Single(hiddenParent.Children);
        Assert.Equal("HiddenNested[[key]][[key]]", hiddenChild.Key);
        Assert.Equal("[key]", hiddenChild.Element?.KeyLabel);
        Assert.True(hiddenChild.Element?.IsKeyRedacted);
        Assert.DoesNotContain("[redacted-key", hiddenChild.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_RedactsDictionaryLabelsForSensitiveParentSignals()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["CookieJar"] = new Dictionary<string, string>
                    {
                        ["tenant-a"] = "session-cookie"
                    },
                    ["Partner.Payloads"] = new Dictionary<string, string>
                    {
                        ["tenant-b"] = "payload"
                    }
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "CookieJar",
            options => options.TraverseCollectionElements = true);
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Partner.Payloads",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.Sensitivity = ConfigAuditSensitivity.Sensitive;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var serialized = JsonSerializer.Serialize(report);

        var fragmentSensitiveChild = Assert.Single(AssertEntry(report, "CookieJar", ConfigAuditEntryState.Resolved, "[redacted]").Children);
        Assert.Equal("CookieJar[[redacted-key-1]]", fragmentSensitiveChild.Key);
        Assert.Equal("[redacted-key-1]", fragmentSensitiveChild.Element?.KeyLabel);
        Assert.True(fragmentSensitiveChild.Element?.IsKeyRedacted);

        var entrySensitiveChild = Assert.Single(AssertEntry(report, "Partner.Payloads", ConfigAuditEntryState.Resolved, "[redacted]").Children);
        Assert.Equal("Partner.Payloads[[redacted-key-1]]", entrySensitiveChild.Key);
        Assert.Equal("[redacted-key-1]", entrySensitiveChild.Element?.KeyLabel);
        Assert.True(entrySensitiveChild.IsRedacted);

        Assert.DoesNotContain("tenant-a", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-b", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_RedactsSourceSensitiveDictionaryChildWhenLabelsAreHidden()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new SourceSensitiveDictionaryProvider(
                "Hidden.Payloads",
                new Dictionary<string, string>
                {
                    ["tenant-a"] = "patched-secret"
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Hidden.Payloads",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.DisplayDictionaryKeys = false;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var serialized = JsonSerializer.Serialize(report);

        var child = Assert.Single(AssertEntry(report, "Hidden.Payloads", ConfigAuditEntryState.PartiallyResolved, "[redacted]").Children);
        Assert.Equal("Hidden.Payloads[[key]]", child.Key);
        Assert.Equal("[redacted]", child.DisplayValue);
        Assert.True(child.IsRedacted);
        Assert.DoesNotContain("patched-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_HandlesUnsafeDictionaryKeyLabelsWithoutCrashingOrLeaking()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var longKey = new string('a', 200);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Labels.Items"] = new Hashtable
                    {
                        [new ThrowingDictionaryKey()] = "throwing",
                        [new FormatFailingDictionaryKey()] = "format",
                        [new NullReturningDictionaryKey()] = "null",
                        [longKey] = "long"
                    }
                }));
        services.AddConfigAuditKey<Hashtable>(
            "Labels.Items",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var serialized = JsonSerializer.Serialize(report);

        var entry = AssertEntry(report, "Labels.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(4, entry.Children.Count);
        Assert.Contains(entry.Children, child => child.Element?.KeyLabel == "[key]" && child.Element.IsKeyRedacted);
        Assert.Contains(entry.Children, child => child.Element?.KeyLabel?.Length == 131);
        Assert.DoesNotContain(new string('a', 200), serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("dictionary-key-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("dictionary-key-format-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_ReportsCollectionTraversalLimitsAndUnsupportedShapes()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Array.Default"] = new[] { "opaque" },
                    ["Array.Items"] = new[] { "one", "two" },
                    ["Array.Deep"] = new[] { new[] { "too-deep" } },
                    ["Array.Limited"] = new[] { "one", "two" },
                    ["ReadOnly.Default"] = new ReadOnlyValues(),
                    ["ReadOnly.Items"] = new ReadOnlyValues(),
                    ["ReadOnly.Deep"] = new List<object> { new ReadOnlyValues() },
                    ["ReadOnly.Limited"] = new ReadOnlyValues(),
                    ["Limited.Items"] = new List<int> { 1, 2, 3 },
                    ["Budget.Items"] = new List<int> { 1, 2, 3 },
                    ["Deep.Items"] = new List<object> { new List<string> { "too-deep" } },
                    ["Dictionary.Default"] = new Dictionary<string, string>
                    {
                        ["one"] = "1"
                    },
                    ["Dictionary.Deep"] = new Dictionary<string, object>
                    {
                        ["inner"] = new Dictionary<string, string>
                        {
                            ["child"] = "too-deep"
                        }
                    },
                    ["Dictionary.Limited"] = new Dictionary<string, string>
                    {
                        ["one"] = "1",
                        ["two"] = "2"
                    },
                    ["PropertyBudget.Shape"] = new PropertyBudgetShape(),
                    ["FieldBudget.Shape"] = new FieldBudgetShape
                    {
                        First = "first",
                        Second = "second"
                    },
                    ["Struct.Shape"] = new StructShape { Name = "value-type" },
                    ["Unsupported.Default"] = new ThrowingEnumerable(),
                    ["Unsupported.Items"] = new ThrowingEnumerable(),
                    ["Matrix.Items"] = new int[1, 1]
                }));
        services.AddConfigAuditKey<string[]>("Array.Default");
        services.AddConfigAuditKey<string[]>(
            "Array.Items",
            options => options.TraverseCollectionElements = true);
        services.AddConfigAuditKey<string[][]>(
            "Array.Deep",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionDepth = 1;
            });
        services.AddConfigAuditKey<string[]>(
            "Array.Limited",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionElements = 1;
            });
        services.AddConfigAuditKey<ReadOnlyValues>("ReadOnly.Default");
        services.AddConfigAuditKey<ReadOnlyValues>(
            "ReadOnly.Items",
            options => options.TraverseCollectionElements = true);
        services.AddConfigAuditKey<List<object>>(
            "ReadOnly.Deep",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionDepth = 1;
            });
        services.AddConfigAuditKey<ReadOnlyValues>(
            "ReadOnly.Limited",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionElements = 1;
            });
        services.AddConfigAuditKey<List<int>>(
            "Limited.Items",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionElements = 2;
            });
        services.AddConfigAuditKey<List<int>>(
            "Budget.Items",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxReportNodes = 1;
            });
        services.AddConfigAuditKey<List<object>>(
            "Deep.Items",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionDepth = 1;
            });
        services.AddConfigAuditKey<Dictionary<string, string>>("Dictionary.Default");
        services.AddConfigAuditKey<Dictionary<string, object>>(
            "Dictionary.Deep",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionDepth = 1;
            });
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Dictionary.Limited",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionElements = 1;
            });
        services.AddConfigAuditKey<PropertyBudgetShape>(
            "PropertyBudget.Shape",
            options => options.MaxReportNodes = 1);
        services.AddConfigAuditKey<FieldBudgetShape>(
            "FieldBudget.Shape",
            options => options.MaxReportNodes = 1);
        services.AddConfigAuditKey<StructShape>("Struct.Shape");
        services.AddConfigAuditKey<ThrowingEnumerable>("Unsupported.Default");
        services.AddConfigAuditKey<ThrowingEnumerable>(
            "Unsupported.Items",
            options => options.TraverseCollectionElements = true);
        services.AddConfigAuditKey<int[,]>(
            "Matrix.Items",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.Empty(AssertEntry(report, "Array.Default", ConfigAuditEntryState.Resolved, null).Children);
        var array = AssertEntry(report, "Array.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(["Array.Items[0]", "Array.Items[1]"], array.Children.Select(child => child.Key));
        Assert.All(array.Children, child => Assert.Equal(ConfigAuditElementKind.ArrayItem, child.Element?.Kind));

        var arrayDeep = AssertEntry(report, "Array.Deep", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(arrayDeep.Children[0].Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-depth-limit");

        var arrayLimited = AssertEntry(report, "Array.Limited", ConfigAuditEntryState.Resolved, null);
        Assert.Single(arrayLimited.Children);
        Assert.Contains(arrayLimited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        Assert.Empty(AssertEntry(report, "ReadOnly.Default", ConfigAuditEntryState.Resolved, null).Children);
        var readOnly = AssertEntry(report, "ReadOnly.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(["first", "second"], readOnly.Children.Select(child => child.DisplayValue));
        Assert.All(readOnly.Children, child => Assert.Equal(ConfigAuditElementKind.ListItem, child.Element?.Kind));

        var readOnlyDeep = AssertEntry(report, "ReadOnly.Deep", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(readOnlyDeep.Children[0].Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-depth-limit");

        var readOnlyLimited = AssertEntry(report, "ReadOnly.Limited", ConfigAuditEntryState.Resolved, null);
        Assert.Single(readOnlyLimited.Children);
        Assert.Contains(readOnlyLimited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        var limited = AssertEntry(report, "Limited.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(2, limited.Children.Count);
        Assert.Contains(limited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        var budget = AssertEntry(report, "Budget.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Single(budget.Children);
        Assert.Contains(budget.Diagnostics, diagnostic => diagnostic.Code == "config-audit-report-node-limit");

        var deep = AssertEntry(report, "Deep.Items", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(deep.Children[0].Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-depth-limit");

        Assert.Empty(AssertEntry(report, "Dictionary.Default", ConfigAuditEntryState.Resolved, null).Children);

        var dictionaryDeep = AssertEntry(report, "Dictionary.Deep", ConfigAuditEntryState.Resolved, null);
        Assert.Contains(dictionaryDeep.Children[0].Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-depth-limit");

        var dictionaryLimited = AssertEntry(report, "Dictionary.Limited", ConfigAuditEntryState.Resolved, null);
        Assert.Single(dictionaryLimited.Children);
        Assert.Contains(dictionaryLimited.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");

        var propertyBudget = AssertEntry(report, "PropertyBudget.Shape", ConfigAuditEntryState.Resolved, null);
        Assert.Single(propertyBudget.Children);
        Assert.Contains(propertyBudget.Diagnostics, diagnostic => diagnostic.Code == "config-audit-report-node-limit");

        var fieldBudget = AssertEntry(report, "FieldBudget.Shape", ConfigAuditEntryState.Resolved, null);
        Assert.Single(fieldBudget.Children);
        Assert.Contains(fieldBudget.Diagnostics, diagnostic => diagnostic.Code == "config-audit-report-node-limit");

        var structShape = AssertEntry(report, "Struct.Shape", ConfigAuditEntryState.Resolved, null);
        Assert.Equal("value-type", structShape.Children.Single(child => child.Key == "Struct.Shape.Name").DisplayValue);

        Assert.Empty(AssertEntry(report, "Unsupported.Default", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Contains(
            AssertEntry(report, "Unsupported.Items", ConfigAuditEntryState.Resolved, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-collection-kind-unsupported");
        Assert.Contains(
            AssertEntry(report, "Matrix.Items", ConfigAuditEntryState.Resolved, null).Diagnostics,
            diagnostic => diagnostic.Code == "config-audit-collection-kind-unsupported");
    }

    [Fact]
    public void GetReport_ReportsSourceUnavailableForRedactedDictionaryKeysWithoutProviderSources()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new NoSourceProvider(
                "Map.Values",
                new Dictionary<string, string>
                {
                    ["password"] = "secret"
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Map.Values",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Map.Values", ConfigAuditEntryState.Resolved, null);
        var child = Assert.Single(entry.Children);

        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-source-unavailable");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionElementsCreatedWhenBaseIsMissing()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var services = CreateServices("/missing", environment);
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

        var entry = AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(["Endpoints[0]", "Endpoints[1]"], entry.Children.Select(child => child.Key));
        Assert.All(
            entry.Children,
            child =>
            {
                Assert.Contains(child.Sources, source => source.Kind == ConfigAuditSourceKind.EnvironmentVariable);
                Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
            });

        var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
        Assert.Contains("[Info] config-audit-environment-created-element:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_DoesNotMarkDirectEnvironmentCollectionElementCreatedWhenBaseIndexExists()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Endpoints", new List<string> { "https://file.example" }));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", child.DisplayValue);
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionTailElementCreatedWhenBaseIsShorter()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Endpoints", new List<string> { "https://file.example" }));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null);
        var first = entry.Children.Single(child => child.Key == "Endpoints[0]");
        var second = entry.Children.Single(child => child.Key == "Endpoints[1]");

        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.Contains(second.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentArrayTailElementCreatedWhenBaseIsShorter()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Endpoints", new[] { "https://file.example" }));
        services.AddConfigAuditKey<string[]>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null);
        var first = entry.Children.Single(child => child.Key == "Endpoints[0]");
        var second = entry.Children.Single(child => child.Key == "Endpoints[1]");

        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.Contains(second.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_DoesNotMarkDirectEnvironmentReadOnlyListElementCreatedWhenBaseIndexExists()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Endpoints", new ReadOnlyValues()));
        services.AddConfigAuditKey<IReadOnlyList<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", child.DisplayValue);
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentReadOnlyListTailElementCreatedWhenBaseIsShorter()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "Endpoints",
            new ReadOnlyValues("first", "second", "third"),
            "Endpoints.2");
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Endpoints", new ReadOnlyValues()));
        services.AddConfigAuditKey<IReadOnlyList<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children, child => child.Key == "Endpoints[2]");
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionElementCreatedWhenBaseValueIsNull()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Endpoints"] = null
                }));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_DoesNotCreateFactsForEnvironmentSourcesWithoutCollectionElementPath()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new EndpointSettings
            {
                Endpoints = ["https://env.example"]
            },
            "MyApp.Settings");
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints")
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints[0]");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_DoesNotCreateFactsForEnvironmentSourcesWithoutPathSeparator()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "Endpoints",
            new List<string> { "https://env.example" },
            "Endpoints");
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", endpoint.DisplayValue);
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_DoesNotCreateFactsForEnvironmentSourcesWithTrailingPathSeparator()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "Endpoints",
            new List<string> { "https://env.example" },
            "Endpoints.");
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", endpoint.DisplayValue);
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_DoesNotMarkDirectEnvironmentObjectElementCreatedWhenBasePropertyIndexExists()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new EndpointSettings
            {
                Endpoints = ["https://env.example"]
            },
            "MyApp.Settings.Endpoints.0");
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new EndpointSettings
                {
                    Endpoints = ["https://file.example"]
                }));
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints")
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints[0]");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentObjectElementCreatedWhenBasePropertyIsNull()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new EndpointSettings
            {
                Endpoints = ["https://env.example"]
            },
            "MyApp.Settings.Endpoints.0");
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new EndpointSettings
                {
                    Endpoints = null!
                }));
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints")
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints[0]");
        Assert.Contains(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentObjectElementCreatedWhenBaseFieldCollectionIsEmpty()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new FieldEndpointSettings
            {
                Endpoints = ["https://env.example"]
            },
            "MyApp.Settings.Endpoints.0");
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new FieldEndpointSettings()));
        services.AddConfigAuditKey<FieldEndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoint = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints")
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints[0]");
        Assert.Contains(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(endpoint.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentObjectElementCreatedWhenBaseFieldIsReadOnly()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new ReadOnlyFieldEndpointSettings("https://env.example"),
            "MyApp.Settings.Endpoints.0");
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new ReadOnlyFieldEndpointSettings()));
        services.AddConfigAuditKey<ReadOnlyFieldEndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.DoesNotContain(
            AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null).Children,
            child => child.Key == "MyApp.Settings.Endpoints");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentObjectElementCreatedWhenBaseMemberCannotBeIndexed()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "MyApp.Settings",
            new ScalarEndpointSettings
            {
                Endpoint = "https://env.example"
            },
            "MyApp.Settings.Endpoint.0");
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new ScalarEndpointSettings
                {
                    Endpoint = "https://file.example"
                }));
        services.AddConfigAuditKey<ScalarEndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.Resolved, null);
        var endpoint = entry.Children.Single(child => child.Key == "MyApp.Settings.Endpoint");
        Assert.Equal("https://env.example", endpoint.DisplayValue);
        Assert.Empty(endpoint.Diagnostics);
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionBasePresenceUnknownForPathlessProvider()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new PathlessSourceProvider("Endpoints", new List<string> { "https://file.example" }));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_IgnoresMismatchedEnvironmentElementFactWhenBaseSourceUsesDifferentRoot()
    {
        var services = CreateServicesWithDiagnosticEnvironment(
            "Endpoints",
            new List<string> { "https://env.example" },
            "Other.0");
        services.AddSingleton<IConfigProvider>(
            new SourcePathProvider(
                "Endpoints",
                new List<string> { "https://file.example" },
                "Other"));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", child.DisplayValue);
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionBasePresenceUnknownForInvalidProvider()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new InvalidDiagnosticProvider("Endpoints", priority: 30));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_MarksDirectEnvironmentCollectionBasePresenceUnknownWhenLowerProviderResolvesAfterInvalidProvider()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new InvalidDiagnosticProvider("Endpoints", priority: 30));
        services.AddSingleton<IConfigProvider>(new SourcePathProvider("Endpoints", new List<string>(), "Endpoints"));
        services.AddConfigAuditKey<List<string>>(
            "Endpoints",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var child = Assert.Single(AssertEntry(report, "Endpoints", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("https://env.example", child.DisplayValue);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_MarksNestedEnvironmentCollectionTailCreatedDuringPatch()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new EndpointSettings
                {
                    Endpoints = ["https://file.example"]
                }));
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoints = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints");
        var first = endpoints.Children.Single(child => child.Key == "MyApp.Settings.Endpoints[0]");
        var second = endpoints.Children.Single(child => child.Key == "MyApp.Settings.Endpoints[1]");

        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
        Assert.Contains(second.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_MarksNestedEnvironmentCollectionBasePresenceUnknownForInvalidProviderDuringPatch()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new InvalidDiagnosticProvider("MyApp.Settings", priority: 30));
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoints = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints");
        var child = Assert.Single(endpoints.Children);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_MarksNestedEnvironmentCollectionBasePresenceUnknownWhenLowerProviderResolvesAfterInvalidProvider()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => environment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://env.example");

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new InvalidDiagnosticProvider("MyApp.Settings", priority: 30));
        services.AddSingleton<IConfigProvider>(
            new StaticConfigProvider(
                "MyApp.Settings",
                new EndpointSettings
                {
                    Endpoints = []
                }));
        services.AddConfigAuditKey<EndpointSettings>(
            "MyApp.Settings",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var endpoints = AssertEntry(report, "MyApp.Settings", ConfigAuditEntryState.PartiallyResolved, null)
            .Children.Single(child => child.Key == "MyApp.Settings.Endpoints");
        var child = Assert.Single(endpoints.Children);
        Assert.Equal("https://env.example", child.DisplayValue);
        Assert.Contains(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-element-base-unknown");
        Assert.DoesNotContain(child.Diagnostics, diagnostic => diagnostic.Code == "config-audit-environment-created-element");
    }

    [Fact]
    public void GetReport_FallsBackWhenSourcePathsAreUnavailable()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new PathlessSourceProvider(
                "Pathless.Endpoint",
                new NamedEndpoint
                {
                    Name = "billing",
                    Url = "https://example.test"
                }));
        services.AddConfigAuditKey<NamedEndpoint>("Pathless.Endpoint");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Pathless.Endpoint", ConfigAuditEntryState.Resolved, null);
        var name = entry.Children.Single(child => child.Key == "Pathless.Endpoint.Name");

        Assert.Contains(name.Sources, source => source.ProviderName == nameof(PathlessSourceProvider));
    }

    [Fact]
    public void GetReport_HandlesCyclesAndUnknownElementSources()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        var cyclic = new List<object>();
        cyclic.Add(cyclic);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new NoSourceProvider("Cycle.List", cyclic));
        services.AddConfigAuditKey<List<object>>(
            "Cycle.List",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Cycle.List", ConfigAuditEntryState.Resolved, null);
        var element = Assert.Single(entry.Children);
        Assert.Empty(element.Children);
        Assert.Empty(element.Sources);
        Assert.Contains(element.Diagnostics, diagnostic => diagnostic.Code == "config-audit-source-unavailable");
    }

    [Fact]
    public void GetReport_ReportsInvalidOptionsAndUsesSafeTraversalDefaults()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Invalid.Options"] = new List<int> { 1, 2 }
                }));
        services.AddConfigAuditKey<List<int>>(
            "Invalid.Options",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionDepth = -1;
                options.MaxCollectionElements = -1;
                options.MaxReportNodes = 0;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Invalid.Options", ConfigAuditEntryState.Resolved, null);
        Assert.Equal(2, entry.Children.Count);
        Assert.Equal(3, entry.Diagnostics.Count(diagnostic => diagnostic.Code == "config-audit-options-invalid"));
    }

    [Fact]
    public void GetReport_ReportsInvalidSensitivityAndFailsClosed()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Partner.Payload"] = "invalid-sensitivity-secret"
                }));
        services.AddConfigAuditKey<string>(
            "Partner.Payload",
            options => options.Sensitivity = (ConfigAuditSensitivity)999);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var entry = AssertEntry(report, "Partner.Payload", ConfigAuditEntryState.Resolved, "[redacted]");
        var diagnostic = Assert.Single(entry.Diagnostics, diagnostic => diagnostic.Code == "config-audit-options-invalid");

        Assert.True(entry.IsRedacted);
        Assert.Contains("Sensitivity", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("999", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Sensitive", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-sensitivity-secret", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_ReportsInvalidSensitivityWithoutRelaxingValidTraversalLimits()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Partner.Payloads"] = new List<string> { "one", "two", "three" }
                }));
        services.AddConfigAuditKey<List<string>>(
            "Partner.Payloads",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.MaxCollectionElements = 1;
                options.MaxReportNodes = 2;
                options.Sensitivity = (ConfigAuditSensitivity)999;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var entry = AssertEntry(report, "Partner.Payloads", ConfigAuditEntryState.Resolved, "[redacted]");
        var child = Assert.Single(entry.Children);
        var serialized = JsonSerializer.Serialize(report);

        Assert.Equal("Partner.Payloads[0]", child.Key);
        Assert.Equal("[redacted]", child.DisplayValue);
        Assert.True(child.IsRedacted);
        Assert.Single(entry.Diagnostics, diagnostic => diagnostic.Code == "config-audit-options-invalid");
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-audit-collection-element-limit");
        Assert.DoesNotContain("two", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("three", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_RedactsDictionaryLabelsWhenInvalidSensitivityFailsClosed()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    ["Partner.Payloads"] = new Dictionary<string, string>
                    {
                        ["tenant-invalid"] = "invalid-sensitivity-secret"
                    }
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            "Partner.Payloads",
            options =>
            {
                options.TraverseCollectionElements = true;
                options.Sensitivity = (ConfigAuditSensitivity)999;
            });

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");
        var child = Assert.Single(AssertEntry(report, "Partner.Payloads", ConfigAuditEntryState.Resolved, "[redacted]").Children);
        var serialized = JsonSerializer.Serialize(report);

        Assert.Equal("Partner.Payloads[[redacted-key-1]]", child.Key);
        Assert.Equal("[redacted-key-1]", child.Element?.KeyLabel);
        Assert.True(child.Element?.IsKeyRedacted);
        Assert.True(child.IsRedacted);
        Assert.DoesNotContain("tenant-invalid", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-sensitivity-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigAuditTextRenderer_OrdersCollectionElementsByNumericIndex()
    {
        var report = new ConfigAuditReport
        {
            Environment = "Production",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Redaction = new ConfigAuditRedaction
            {
                Enabled = true,
                Placeholder = "[redacted]"
            },
            Entries =
            [
                new ConfigAuditEntry
                {
                    Key = "Items",
                    State = ConfigAuditEntryState.Resolved,
                    Children =
                    [
                        new ConfigAuditEntry
                        {
                            Key = "Items[10]",
                            State = ConfigAuditEntryState.Resolved,
                            DisplayValue = "ten",
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.ListItem,
                                Index = 10
                            }
                        },
                        new ConfigAuditEntry
                        {
                            Key = "Items[2]",
                            State = ConfigAuditEntryState.Resolved,
                            DisplayValue = "two",
                            Element = new ConfigAuditElementIdentity
                            {
                                Kind = ConfigAuditElementKind.ListItem,
                                Index = 2
                            }
                        }
                    ]
                }
            ]
        };

        var rendered = new ConfigAuditTextRenderer().Render(report);

        Assert.True(rendered.IndexOf("Items[2]", StringComparison.Ordinal) < rendered.IndexOf("Items[10]", StringComparison.Ordinal));
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
    public void GetReport_UsesDefaultSourceForDefaultedCollectionChildren()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton(new ConfigAuditKnownEntry(
            "Default.Services",
            typeof(DefaultServicesConfig),
            typeof(List<NamedEndpoint>)));
        services.AddConfigAuditKey<List<NamedEndpoint>>(
            "Default.Services",
            options => options.TraverseCollectionElements = true);

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Default.Services", ConfigAuditEntryState.Defaulted, null);
        var service = Assert.Single(entry.Children);
        var name = Assert.Single(service.Children, child => child.Key == "Default.Services[0].Name");

        Assert.All(entry.Sources, source => Assert.Equal(ConfigAuditSourceKind.Default, source.Kind));
        Assert.All(service.Sources, source => Assert.Equal(ConfigAuditSourceKind.Default, source.Kind));
        Assert.All(name.Sources, source => Assert.Equal(ConfigAuditSourceKind.Default, source.Kind));
        Assert.DoesNotContain(service.Sources, source => source.Kind == ConfigAuditSourceKind.Missing);
        Assert.DoesNotContain(name.Sources, source => source.Kind == ConfigAuditSourceKind.Missing);
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
    public void GetReport_ConvertsPublicProviderDiagnosticExceptionsToDiagnostics()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new ThrowingPublicAuditDiagnosticsProvider(
            new InvalidOperationException("public diagnostics failed with super-secret")));

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "config-provider-diagnostics-threw");
        Assert.DoesNotContain(
            report.Diagnostics.Concat(report.Entries.SelectMany(entry => entry.Diagnostics)),
            diagnostic => diagnostic.Message.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void GetReport_UsesPublicProviderAuditResolution()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new PublicAuditDiagnosticsProvider("Public.Resolved", "from-public-provider"));
        services.AddConfigAuditKey<string>("Public.Resolved");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Public.Resolved", ConfigAuditEntryState.Resolved, "from-public-provider");
        Assert.Contains(entry.Sources, source => source.ProviderName == nameof(PublicAuditDiagnosticsProvider));
    }

    [Fact]
    public void GetReport_ConvertsPublicProviderAuditResolutionExceptionsToDiagnostics()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new ThrowingPublicAuditResolutionProvider("Public.Throws"));
        services.AddConfigAuditKey<string>("Public.Throws");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var diagnostic = Assert.Single(AssertEntry(report, "Public.Throws", ConfigAuditEntryState.Invalid, null).Diagnostics);
        Assert.Equal("config-provider-resolve-threw", diagnostic.Code);
        Assert.DoesNotContain("super-secret", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_DoesNotConvertCriticalProviderDiagnosticExceptions()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new CriticalDiagnosticProvider(
            new AccessViolationException("critical diagnostics failed")));

        var reporter = services.BuildServiceProvider().GetRequiredService<IConfigAuditReporter>();

        Assert.Throws<AccessViolationException>(() => reporter.GetReport("Production"));
    }

    [Fact]
    public void GetReport_DoesNotConvertCriticalPublicProviderDiagnosticExceptions()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new ThrowingPublicAuditDiagnosticsProvider(
            new AccessViolationException("critical public diagnostics failed")));

        var reporter = services.BuildServiceProvider().GetRequiredService<IConfigAuditReporter>();

        Assert.Throws<AccessViolationException>(() => reporter.GetReport("Production"));
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
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
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
    public void TextRenderer_UsesFileLocationsWhenPresentAndFallsBackWhenNull()
    {
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
                        Key = "Located.Value",
                        State = ConfigAuditEntryState.Resolved,
                        Sources =
                        [
                            new ConfigAuditSourceRecord
                            {
                                Kind = ConfigAuditSourceKind.File,
                                ProviderName = "Files",
                                FilePath = "/tmp/appsettings.json",
                                ConfigPath = "Located.Value",
                                AppliedToPath = "Located.Value",
                                Location = new ConfigAuditSourceLocation(4, 12),
                                Role = ConfigAuditSourceRole.Base
                            }
                        ]
                    },
                    new ConfigAuditEntry
                    {
                        Key = "Unlocated.Value",
                        State = ConfigAuditEntryState.Resolved,
                        Sources =
                        [
                            new ConfigAuditSourceRecord
                            {
                                Kind = ConfigAuditSourceKind.File,
                                ProviderName = "Files",
                                FilePath = "/tmp/appsettings.json",
                                ConfigPath = "Unlocated.Value",
                                AppliedToPath = "Unlocated.Value",
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

        Assert.Contains("Files appsettings.json:4:12 :: Located.Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Files appsettings.json :: Unlocated.Value", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReport_RemovesInheritedParentLocationFromChildSource()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IConfigProvider>(new ParentLocatedSourceProvider());
        services.AddConfigAuditKey<AppSettings>("Located.Parent");

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var entry = AssertEntry(report, "Located.Parent", ConfigAuditEntryState.Resolved, null);
        var parentSource = Assert.Single(entry.Sources, source => source.ProviderName == nameof(ParentLocatedSourceProvider));
        AssertLocation(parentSource, lineNumber: 10, byteColumnNumber: 4);

        var mode = Assert.Single(entry.Children, child => child.Key == "Located.Parent.Mode");
        var childSource = Assert.Single(mode.Sources, source => source.ProviderName == nameof(ParentLocatedSourceProvider));
        Assert.Equal("Located.Parent", childSource.ConfigPath);
        Assert.Equal("Located.Parent", childSource.AppliedToPath);
        Assert.Null(childSource.Location);
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
    public void Redactor_OmitsEnumerablesThatWouldHaveFailedSerialization()
    {
        var redactor = new ConfigAuditRedactor();

        var formatted = redactor.FormatValue("Values", new ThrowingEnumerable(), []);
        var json = redactor.FormatValue("Values", new JsonExceptionEnumerable(), []);
        var invalidOperation = redactor.FormatValue("Values", new InvalidOperationEnumerable(), []);
        var throwingToString = redactor.FormatValue("Values", new ThrowingToStringEnumerable(), []);

        Assert.Null(formatted.DisplayValue);
        Assert.False(formatted.IsRedacted);
        Assert.Null(json.DisplayValue);
        Assert.False(json.IsRedacted);
        Assert.Null(invalidOperation.DisplayValue);
        Assert.False(invalidOperation.IsRedacted);
        Assert.Null(throwingToString.DisplayValue);
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

    [Fact]
    public void GetReport_IncludesProviderDiscoveredFileKeysWithClassificationsAndRedaction()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Staging.json"),
                """
                {
                  "KnownExact": "registered",
                  "SensitiveExact": "provider-only-secret",
                  "SensitiveTree": {
                    "Child": "descendant-secret"
                  },
                  "NestedTree": {
                    "Branch": {
                      "Leaf": "nearest-visible"
                    }
                  },
                  "SensitiveUnknownTree": {
                    "Branch": {
                      "Leaf": "inherited-secret"
                    }
                  },
                  "App": {
                    "Mode": "file",
                    "Enabled": true,
                    "RetryCount": 3,
                    "Ratio": 42.5,
                    "LongValue": 9223372036854775807,
                    "HugeValue": 1e100,
                    "Password": "super-secret",
                    "Items": [1, 2],
                    "TokenList": ["secret-one", "secret-two"],
                    "NullValue": null
                  },
                  "Application": {
                    "Name": "lookalike",
                    "Reviewed": "display-me"
                  },
                  "UnknownNull": null
                }
                """);
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Development.json"),
                """
                {
                  "OtherEnvironmentNull": null
                }
                """);

            var environment = A.Fake<IEnvironmentProvider>();
            A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

            var services = CreateServices(tempDir, environment);
            services.AddConfigAuditKey<string>("KnownExact");
            services.AddConfigAuditKey<string>(
                "SensitiveExact",
                options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
            services.AddConfigAuditKey<Dictionary<string, string>>(
                "SensitiveTree",
                options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
            services.AddConfigAuditKey<Dictionary<string, object?>>(
                "NestedTree",
                options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
            services.AddConfigAuditKey<Dictionary<string, string>>(
                "NestedTree.Branch",
                options => options.Sensitivity = ConfigAuditSensitivity.NonSensitive);
            services.AddConfigAuditKey<Dictionary<string, object?>>(
                "SensitiveUnknownTree",
                options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
            services.AddConfigAuditKey<Dictionary<string, string>>("SensitiveUnknownTree.Branch");
            services.AddConfigAuditKey<AppSettings>("App");
            services.AddConfigAuditKey<string>("App.Mode");
            services.AddConfigAuditKey<string>("Application.Reviewed");
            services.AddSingleton<IConfigProvider>(
                new SourceSensitiveKeyEnumeratorProvider("SourceSensitive.Leaf", "source-sensitive-value"));

            var provider = services.BuildServiceProvider();
            var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Staging");

            AssertDiscovered(
                report,
                "KnownExact",
                ConfigAuditDiscoveredKeyClassification.Known,
                "registered",
                ConfigAuditDiscoveredValueDisplayState.Shown);
            var sensitiveExact = AssertDiscovered(
                report,
                "SensitiveExact",
                ConfigAuditDiscoveredKeyClassification.Known,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(sensitiveExact.IsRedacted);
            var sensitiveChild = AssertDiscovered(
                report,
                "SensitiveTree.Child",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(sensitiveChild.IsRedacted);
            var nearestParentChild = AssertDiscovered(
                report,
                "NestedTree.Branch.Leaf",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(nearestParentChild.IsRedacted);
            var unknownChildInherited = AssertDiscovered(
                report,
                "SensitiveUnknownTree.Branch.Leaf",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(unknownChildInherited.IsRedacted);
            AssertDiscovered(
                report,
                "App",
                ConfigAuditDiscoveredKeyClassification.Known,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedComplex);
            AssertDiscovered(
                report,
                "App.Mode",
                ConfigAuditDiscoveredKeyClassification.Known,
                "file",
                ConfigAuditDiscoveredValueDisplayState.Shown);
            AssertDiscovered(
                report,
                "App.Enabled",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            AssertDiscovered(
                report,
                "App.RetryCount",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            AssertDiscovered(
                report,
                "App.Ratio",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            AssertDiscovered(
                report,
                "App.LongValue",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            AssertDiscovered(
                report,
                "App.HugeValue",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            var password = AssertDiscovered(
                report,
                "App.Password",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(password.IsRedacted);
            var items = AssertDiscovered(
                report,
                "App.Items",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedComplex);
            Assert.False(items.IsRedacted);
            var tokenList = AssertDiscovered(
                report,
                "App.TokenList",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(tokenList.IsRedacted);
            AssertDiscovered(
                report,
                "Application.Name",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            AssertDiscovered(
                report,
                "Application.Reviewed",
                ConfigAuditDiscoveredKeyClassification.Known,
                "display-me",
                ConfigAuditDiscoveredValueDisplayState.Shown);
            var sourceSensitive = AssertDiscovered(
                report,
                "SourceSensitive.Leaf",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                "[redacted]",
                ConfigAuditDiscoveredValueDisplayState.Redacted);
            Assert.True(sourceSensitive.IsRedacted);
            Assert.DoesNotContain(report.DiscoveredKeys, key => key.Key == "App.NullValue");
            Assert.DoesNotContain(report.DiscoveredKeys, key => key.Key == "UnknownNull");
            Assert.DoesNotContain(report.DiscoveredKeys, key => key.Key == "OtherEnvironmentNull");
            var app = report.Entries.Single(entry => entry.Key == "App");
            Assert.Contains(
                app.Diagnostics,
                diagnostic => diagnostic.Code == "config-file-null-skipped"
                              && diagnostic.ConfigPath == "App.NullValue");
            Assert.DoesNotContain(
                report.Diagnostics,
                diagnostic => diagnostic.Code == "config-file-null-skipped"
                              && diagnostic.ConfigPath == "App.NullValue");
            Assert.Contains(
                report.Diagnostics,
                diagnostic => diagnostic.Code == "config-file-null-skipped"
                              && diagnostic.ConfigPath == "UnknownNull");
            Assert.DoesNotContain(
                report.Diagnostics.Concat(report.Entries.SelectMany(entry => entry.Diagnostics)),
                diagnostic => diagnostic.Code == "config-file-null-skipped"
                              && diagnostic.ConfigPath == "OtherEnvironmentNull");

            var rendered = provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report);
            Assert.Contains("Discovered file keys:", rendered, StringComparison.Ordinal);
            Assert.Contains("KnownExact [Known] = registered", rendered, StringComparison.Ordinal);
            Assert.Contains("App.Mode [Known] = file", rendered, StringComparison.Ordinal);
            Assert.Contains(
                "Application.Name [Unknown to AppSurface audit registry] (value omitted: inventory key is not an exact audit entry; register this exact key with AddConfigAuditKey<T>() after reviewing sensitivity)",
                rendered,
                StringComparison.Ordinal);
            Assert.Contains(
                "App.Enabled [Under known entry] (value omitted: descendant is not an exact audit entry; register this exact key with AddConfigAuditKey<T>() after reviewing sensitivity)",
                rendered,
                StringComparison.Ordinal);
            Assert.Contains("Application.Reviewed [Known] = display-me", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("Unused", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("lookalike", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("nearest-visible", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("9223372036854775807", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("1E+100", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("source-sensitive-value", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-one", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-only-secret", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("descendant-secret", rendered, StringComparison.Ordinal);
            var serialized = JsonSerializer.Serialize(report);
            Assert.Contains("\"ValueDisplayState\":1", serialized, StringComparison.Ordinal);
            Assert.Contains("\"ValueDisplayState\":2", serialized, StringComparison.Ordinal);
            Assert.Contains("\"ValueDisplayState\":3", serialized, StringComparison.Ordinal);
            Assert.Contains("\"ValueDisplayState\":4", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("lookalike", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("nearest-visible", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("9223372036854775807", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("source-sensitive-value", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-only-secret", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("descendant-secret", serialized, StringComparison.Ordinal);
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
    public void GetReport_UsesEffectiveObjectOriginsForDiscoveredFileKeys()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Join(tempDir, "appsettings.Staging.json"),
                """
                {
                  "Composite": {
                    "Base": "from-base"
                  }
                }
                """);
            File.WriteAllText(
                Path.Join(tempDir, "config_Override.Staging.json"),
                """
                {
                  "Composite": {
                    "Override": "from-override"
                  }
                }
                """);

            var environment = A.Fake<IEnvironmentProvider>();
            A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

            var report = CreateServices(tempDir, environment)
                .BuildServiceProvider()
                .GetRequiredService<IConfigAuditReporter>()
                .GetReport("Staging");

            var parent = AssertDiscovered(report, "Composite", ConfigAuditDiscoveredKeyClassification.Unknown, null);
            Assert.Contains(
                parent.Sources,
                source => source.FilePath?.EndsWith("config_Override.Staging.json", StringComparison.Ordinal) == true);
            var baseChild = AssertDiscovered(
                report,
                "Composite.Base",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            Assert.Contains(
                baseChild.Sources,
                source => source.FilePath?.EndsWith("appsettings.Staging.json", StringComparison.Ordinal) == true);
            var overrideChild = AssertDiscovered(
                report,
                "Composite.Override",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                null,
                ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
            Assert.Contains(
                overrideChild.Sources,
                source => source.FilePath?.EndsWith("config_Override.Staging.json", StringComparison.Ordinal) == true);
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
    public void GetReport_UsesPublicProviderDiscoveredKeys()
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddConfigAuditKey<string>("Public.Known");
        services.AddSingleton<IConfigProvider>(
            new PublicKeyEnumeratorProvider("Public.Known", "provider-visible"));

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var discovered = AssertDiscovered(
            report,
            "Public.Known",
            ConfigAuditDiscoveredKeyClassification.Known,
            "provider-visible",
            ConfigAuditDiscoveredValueDisplayState.Shown);
        Assert.Contains(discovered.Sources, source => source.ProviderName == nameof(PublicKeyEnumeratorProvider));
    }

    [Fact]
    public void FileProviderEnumeration_ReportsOriginFallbackForMissingOriginMetadata()
    {
        var environmentConfig = new JsonObject
        {
            ["MissingOrigin"] = "value"
        };
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                ["Staging"] = environmentConfig
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase),
            []);
        var provider = new FileBasedConfigProvider(snapshot);

        var discoveredKey = Assert.Single(((IConfigAuditKeyEnumerator)provider).EnumerateKeys("Staging"));

        Assert.Equal("MissingOrigin", discoveredKey.Key);
        Assert.Equal("value", discoveredKey.RawValue);
        var source = Assert.Single(discoveredKey.Sources);
        Assert.Equal(ConfigAuditSourceKind.File, source.Kind);
        Assert.Equal(nameof(FileBasedConfigProvider), source.ProviderName);
        Assert.Equal("MissingOrigin", source.ConfigPath);
        Assert.Contains(
            discoveredKey.Diagnostics,
            diagnostic => diagnostic.Code == "config-provider-discovered-key-origin-missing"
                          && diagnostic.Source == source);
    }

    [Fact]
    public void FileProviderEnumeration_ReturnsNullRawValueForUnsupportedJsonScalar()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            ProviderName = nameof(FileBasedConfigProvider),
            ProviderPriority = 100,
            ConfigPath = "Timestamp",
            AppliedToPath = "Timestamp",
            Role = ConfigAuditSourceRole.Base
        };
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                ["Staging"] = new JsonObject
                {
                    ["Timestamp"] = JsonValue.Create(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero))
                }
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Staging"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Timestamp"] = source
                }
            },
            []);
        var provider = new FileBasedConfigProvider(snapshot);

        var discoveredKey = Assert.Single(((IConfigAuditKeyEnumerator)provider).EnumerateKeys("Staging"));

        Assert.Equal("Timestamp", discoveredKey.Key);
        Assert.Equal(ConfigAuditDiscoveredValueKind.Scalar, discoveredKey.ValueKind);
        Assert.Null(discoveredKey.RawValue);
        Assert.Empty(discoveredKey.Diagnostics);
    }

    [Fact]
    public void FileProviderDiagnostics_SkipPathlessDiagnosticsForUnrelatedKeys()
    {
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                ["Staging"] = new JsonObject
                {
                    ["Known"] = "value"
                }
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase),
            [
                new ConfigFileProviderDiagnostic(
                    "Staging",
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Info,
                        Code = "unrelated",
                        Key = "Other",
                        Message = "Unrelated diagnostic."
                    }),
                new ConfigFileProviderDiagnostic(
                    "Staging",
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Info,
                        Code = "unrelated-path",
                        ConfigPath = "Other.Path",
                        Message = "Unrelated path diagnostic."
                    })
            ]);
        var provider = new FileBasedConfigProvider(snapshot);

        var resolution = ((IConfigDiagnosticProvider)provider).Resolve(
            "Staging",
            "Known",
            typeof(string),
            ConfigAuditSourceRole.Base);

        Assert.Equal("value", resolution.Value);
        Assert.Empty(resolution.Diagnostics);
    }

    [Fact]
    public void FileProviderDiagnostics_AttachDescendantPathDiagnostics()
    {
        var snapshot = new ConfigFileProviderSnapshot(
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
            {
                ["Staging"] = new JsonObject
                {
                    ["Known"] = new JsonObject
                    {
                        ["Child"] = "value"
                    }
                }
            },
            new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase),
            [
                new ConfigFileProviderDiagnostic(
                    "Staging",
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Info,
                        Code = "exact-path",
                        ConfigPath = "Known",
                        Message = "Exact path diagnostic."
                    }),
                new ConfigFileProviderDiagnostic(
                    "Staging",
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Info,
                        Code = "descendant-path",
                        ConfigPath = "Known.Child",
                        Message = "Descendant diagnostic."
                    })
            ]);
        var provider = new FileBasedConfigProvider(snapshot);

        var resolution = ((IConfigDiagnosticProvider)provider).Resolve(
            "Staging",
            "Known",
            typeof(Dictionary<string, string>),
            ConfigAuditSourceRole.Base);

        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.Code == "exact-path");
        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.Code == "descendant-path");
    }

    [Fact]
    public void GetReport_ConvertsDiscoveredEnumerationExceptionsToDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentConfigProvider>(new EmptyEnvironmentConfigProvider());
        services.AddSingleton<IConfigProvider>(new ThrowingKeyEnumeratorProvider());
        services.AddSingleton<IConfigProvider>(new StaticConfigProvider("Ignored.Key", "ignored"));
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddSingleton<ConfigAuditTextRenderer>();

        var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");

        Assert.Empty(report.DiscoveredKeys);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "config-provider-enumerate-keys-threw");
        Assert.DoesNotContain(
            report.Diagnostics,
            diagnostic => diagnostic.Message.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "Discovered file keys:",
            provider.GetRequiredService<ConfigAuditTextRenderer>().Render(report),
            StringComparison.Ordinal);
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
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddSingleton<ConfigAuditTextRenderer>();
        return services;
    }

    private static ServiceCollection CreateServicesWithDiagnosticEnvironment(
        string key,
        object value,
        string sourcePath)
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        services.AddSingleton<IEnvironmentConfigProvider>(
            new DiagnosticEnvironmentProvider(
                key,
                value,
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.EnvironmentVariable,
                    EnvironmentVariableName = sourcePath.Replace('.', '_').ToUpperInvariant(),
                    ConfigPath = sourcePath,
                    AppliedToPath = sourcePath,
                    Role = ConfigAuditSourceRole.Override
                }));
        return services;
    }

    private static ConfigAuditReport CreateCorrelationReport(
        string environmentName,
        string rootKey,
        string rawDictionaryKey,
        string applicationScope = "app-a",
        string keyId = "kid-a",
        string secretKey = CorrelationSecretA,
        bool configureGlobalOptions = true,
        bool enableEntryCorrelation = true,
        bool displayDictionaryKeys = true)
    {
        var environment = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => environment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var services = CreateServices("/missing", environment);
        if (configureGlobalOptions)
        {
            services.Configure<ConfigAuditDictionaryKeyCorrelationOptions>(options =>
            {
                options.SecretKey = secretKey;
                options.KeyId = keyId;
                options.ApplicationScope = applicationScope;
            });
        }

        services.AddSingleton<IConfigProvider>(
            new DictionaryConfigProvider(
                new Dictionary<string, object?>
                {
                    [rootKey] = new Dictionary<string, string>
                    {
                        [rawDictionaryKey] = "alpha-secret-value"
                    }
                }));
        services.AddConfigAuditKey<Dictionary<string, string>>(
            rootKey,
            options =>
            {
                options.TraverseCollectionElements = true;
                options.DisplayDictionaryKeys = displayDictionaryKeys;
                if (enableEntryCorrelation)
                {
                    options.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac;
                }
            });

        return services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport(environmentName);
    }

    private static string GetSingleCorrelationId(ConfigAuditReport report)
    {
        var entry = Assert.Single(report.Entries);
        var child = Assert.Single(entry.Children);
        Assert.NotNull(child.Element?.KeyCorrelationId);
        return child.Element!.KeyCorrelationId!;
    }

    private static string GetSingleComparisonCorrelationId(ConfigAuditReport report)
    {
        var entry = Assert.Single(report.Entries);
        var child = Assert.Single(entry.Children);
        Assert.NotNull(child.Element?.ComparisonKeyCorrelationId);
        return child.Element!.ComparisonKeyCorrelationId!;
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

    private static void AssertLocation(ConfigAuditSourceRecord source, int lineNumber, int byteColumnNumber)
    {
        Assert.NotNull(source.Location);
        Assert.Equal(lineNumber, source.Location.LineNumber);
        Assert.Equal(byteColumnNumber, source.Location.ByteColumnNumber);
    }

    private static ConfigAuditDiscoveredKey AssertDiscovered(
        ConfigAuditReport report,
        string key,
        ConfigAuditDiscoveredKeyClassification classification,
        string? displayValue,
        ConfigAuditDiscoveredValueDisplayState? displayState = null)
    {
        var discoveredKey = Assert.Single(report.DiscoveredKeys, discoveredKey => discoveredKey.Key == key);
        Assert.Equal(classification, discoveredKey.Classification);
        Assert.Equal(displayValue, discoveredKey.DisplayValue);
        if (displayState != null)
        {
            Assert.Equal(displayState, discoveredKey.ValueDisplayState);
        }

        return discoveredKey;
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

    private sealed class EmptyEnvironmentConfigProvider : IEnvironmentConfigProvider
    {
        public string Environment => "Production";

        public bool IsDevelopment => false;

        public int Priority => int.MaxValue;

        public string Name => nameof(EmptyEnvironmentConfigProvider);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public T? GetValue<T>(string environment, string key) => default;
    }

    private sealed class DiagnosticEnvironmentProvider : IEnvironmentConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;
        private readonly object _value;
        private readonly ConfigAuditSourceRecord _source;

        public DiagnosticEnvironmentProvider(string key, object value, ConfigAuditSourceRecord source)
        {
            _key = key;
            _value = value;
            _source = source;
        }

        public string Environment => "Production";

        public bool IsDevelopment => false;

        public int Priority => -1;

        public string Name => nameof(DiagnosticEnvironmentProvider);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public T? GetValue<T>(string environment, string key) =>
            string.Equals(_key, key, StringComparison.Ordinal) ? (T)_value : default;

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
                ConfigAuditEntryState.Resolved,
                _value,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = _source.Kind,
                        ProviderName = _source.ProviderName,
                        ProviderPriority = _source.ProviderPriority,
                        FilePath = _source.FilePath,
                        EnvironmentVariableName = _source.EnvironmentVariableName,
                        ConfigPath = _source.ConfigPath,
                        AppliedToPath = _source.AppliedToPath,
                        Location = _source.Location,
                        Role = role,
                        Sensitivity = _source.Sensitivity
                    }
                ],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class ThrowingKeyEnumeratorProvider : IConfigProvider, IConfigAuditKeyEnumerator
    {
        public int Priority => 10;

        public string Name => nameof(ThrowingKeyEnumeratorProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public IReadOnlyList<ConfigAuditProviderDiscoveredKey> EnumerateKeys(string environment) =>
            throw new InvalidOperationException("super-secret enumeration failure");
    }

    private sealed class SourceSensitiveKeyEnumeratorProvider : IConfigProvider, IConfigAuditKeyEnumerator
    {
        private readonly string _key;
        private readonly object _value;

        public SourceSensitiveKeyEnumeratorProvider(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public int Priority => 10;

        public string Name => nameof(SourceSensitiveKeyEnumeratorProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public IReadOnlyList<ConfigAuditProviderDiscoveredKey> EnumerateKeys(string environment) =>
        [
            new ConfigAuditProviderDiscoveredKey(
                _key,
                _value,
                ConfigAuditDiscoveredValueKind.Scalar,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        FilePath = "/app/appsettings.Staging.json",
                        ConfigPath = _key,
                        AppliedToPath = _key,
                        Role = ConfigAuditSourceRole.Base,
                        Sensitivity = ConfigAuditSensitivity.Sensitive
                    }
                ],
                [])
        ];
    }

    private sealed class PublicKeyEnumeratorProvider(string key, object value) : IConfigProvider, IConfigProviderAuditKeyEnumerator
    {
        public int Priority => 10;

        public string Name => nameof(PublicKeyEnumeratorProvider);

        public T? GetValue<T>(string environment, string requestedKey) => default;

        public IReadOnlyList<ConfigProviderAuditDiscoveredKey> EnumerateKeys(string environment) =>
        [
            new ConfigProviderAuditDiscoveredKey(
                key,
                value,
                ConfigAuditDiscoveredValueKind.Scalar,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = key,
                        AppliedToPath = key,
                        Role = ConfigAuditSourceRole.Base
                    }
                ],
                [])
        ];
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

    private sealed class NoSourceProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;
        private readonly object _value;

        public NoSourceProvider(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public int Priority => 20;

        public string Name => nameof(NoSourceProvider);

        public T? GetValue<T>(string environment, string key) =>
            string.Equals(_key, key, StringComparison.Ordinal) ? (T)_value : default;

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

            return new ConfigValueResolution(key, ConfigAuditEntryState.Resolved, _value, [], []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class SourceSensitiveDictionaryProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;
        private readonly object _value;

        public SourceSensitiveDictionaryProvider(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public int Priority => 20;

        public string Name => nameof(SourceSensitiveDictionaryProvider);

        public T? GetValue<T>(string environment, string key) =>
            string.Equals(_key, key, StringComparison.Ordinal) ? (T)_value : default;

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
                ConfigAuditEntryState.Resolved,
                _value,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = key,
                        AppliedToPath = key,
                        Role = ConfigAuditSourceRole.Base
                    },
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.EnvironmentVariable,
                        EnvironmentVariableName = "HIDDEN__PAYLOADS",
                        ConfigPath = key,
                        AppliedToPath = key,
                        Role = ConfigAuditSourceRole.Patch,
                        Sensitivity = ConfigAuditSensitivity.Sensitive
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

    private sealed class PathlessSourceProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;
        private readonly object _value;

        public PathlessSourceProvider(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public int Priority => 20;

        public string Name => nameof(PathlessSourceProvider);

        public T? GetValue<T>(string environment, string key) =>
            string.Equals(_key, key, StringComparison.Ordinal) ? (T)_value : default;

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

            var source = new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.Provider,
                ProviderName = Name,
                ProviderPriority = Priority,
                Role = role
            };

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                _value,
                [source],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class SourcePathProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        private readonly string _key;
        private readonly object _value;
        private readonly string _sourcePath;

        public SourcePathProvider(string key, object value, string sourcePath)
        {
            _key = key;
            _value = value;
            _sourcePath = sourcePath;
        }

        public int Priority => 20;

        public string Name => nameof(SourcePathProvider);

        public T? GetValue<T>(string environment, string key) =>
            string.Equals(_key, key, StringComparison.Ordinal) ? (T)_value : default;

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

            var source = new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.Provider,
                ProviderName = Name,
                ProviderPriority = Priority,
                ConfigPath = _sourcePath,
                AppliedToPath = _sourcePath,
                Role = role
            };

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                _value,
                [source],
                []);
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

    private sealed class CriticalDiagnosticProvider(Exception exception) : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 26;

        public string Name => nameof(CriticalDiagnosticProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role) =>
            ConfigValueResolution.Missing(key);

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => throw exception;
    }

    private sealed class ThrowingPublicAuditDiagnosticsProvider(Exception exception) : IConfigProvider, IConfigProviderAuditDiagnostics
    {
        public int Priority => 24;

        public string Name => nameof(ThrowingPublicAuditDiagnosticsProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigProviderAuditResolution ResolveForAudit(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role) =>
            ConfigProviderAuditResolution.Missing(key);

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => throw exception;
    }

    private sealed class PublicAuditDiagnosticsProvider(string key, object value) : IConfigProvider, IConfigProviderAuditDiagnostics
    {
        public int Priority => 24;

        public string Name => nameof(PublicAuditDiagnosticsProvider);

        public T? GetValue<T>(string environment, string requestedKey) => default;

        public ConfigProviderAuditResolution ResolveForAudit(
            string environment,
            string requestedKey,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (!string.Equals(key, requestedKey, StringComparison.Ordinal))
            {
                return ConfigProviderAuditResolution.Missing(requestedKey);
            }

            return new ConfigProviderAuditResolution(
                requestedKey,
                ConfigAuditEntryState.Resolved,
                value,
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        ConfigPath = requestedKey,
                        AppliedToPath = requestedKey,
                        Role = role
                    }
                ],
                []);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
    }

    private sealed class ThrowingPublicAuditResolutionProvider(string key) : IConfigProvider, IConfigProviderAuditDiagnostics
    {
        public int Priority => 24;

        public string Name => nameof(ThrowingPublicAuditResolutionProvider);

        public T? GetValue<T>(string environment, string requestedKey) => default;

        public ConfigProviderAuditResolution ResolveForAudit(
            string environment,
            string requestedKey,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (string.Equals(key, requestedKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("public provider resolve failed with super-secret");
            }

            return ConfigProviderAuditResolution.Missing(requestedKey);
        }

        public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
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

    private sealed class ParentLocatedSourceProvider : IConfigProvider, IConfigDiagnosticProvider
    {
        public int Priority => 40;

        public string Name => nameof(ParentLocatedSourceProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public ConfigValueResolution Resolve(
            string environment,
            string key,
            Type valueType,
            ConfigAuditSourceRole role)
        {
            if (!string.Equals(key, "Located.Parent", StringComparison.Ordinal))
            {
                return ConfigValueResolution.Missing(key);
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                new AppSettings
                {
                    Mode = "located"
                },
                [
                    new ConfigAuditSourceRecord
                    {
                        Kind = ConfigAuditSourceKind.File,
                        ProviderName = Name,
                        ProviderPriority = Priority,
                        FilePath = "/tmp/appsettings.json",
                        ConfigPath = key,
                        AppliedToPath = key,
                        Location = new ConfigAuditSourceLocation(10, 4),
                        Role = role
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

    private sealed class ThrowingDictionaryKey
    {
        public override string ToString() => throw new InvalidOperationException("dictionary-key-secret");
    }

    private sealed class FormatFailingDictionaryKey : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) =>
            throw new FormatException("dictionary-key-format-secret");
    }

    private sealed class NullReturningDictionaryKey : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => null!;
    }
}
