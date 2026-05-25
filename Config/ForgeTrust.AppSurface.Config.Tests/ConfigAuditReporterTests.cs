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
        private readonly string[] _values = ["first", "second"];

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
            File.WriteAllText(Path.Join(tempDir, "appsettings.Broken.json"), "{");
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
                        [7] = "seven"
                    },
                    ["Hidden"] = new Dictionary<string, string>
                    {
                        ["public-name"] = "visible"
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

        var report = services.BuildServiceProvider()
            .GetRequiredService<IConfigAuditReporter>()
            .GetReport("Production");

        var code = Assert.Single(AssertEntry(report, "Codes", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("Codes[\"7\"]", code.Key);
        Assert.Equal("7", code.Element?.KeyLabel);
        Assert.False(code.Element?.IsKeyRedacted);

        var hidden = Assert.Single(AssertEntry(report, "Hidden", ConfigAuditEntryState.Resolved, null).Children);
        Assert.Equal("Hidden[[key]]", hidden.Key);
        Assert.Equal("[key]", hidden.Element?.KeyLabel);
        Assert.True(hidden.Element?.IsKeyRedacted);
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
                    "Name": "lookalike"
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
            services.AddConfigAuditKey<AppSettings>("App");
            services.AddConfigAuditKey<string>("App.Mode");

            var provider = services.BuildServiceProvider();
            var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Staging");

            AssertDiscovered(report, "KnownExact", ConfigAuditDiscoveredKeyClassification.Known, "registered");
            AssertDiscovered(report, "App", ConfigAuditDiscoveredKeyClassification.Known, null);
            AssertDiscovered(report, "App.Mode", ConfigAuditDiscoveredKeyClassification.Known, "file");
            AssertDiscovered(report, "App.Enabled", ConfigAuditDiscoveredKeyClassification.KnownDescendant, "True");
            AssertDiscovered(report, "App.RetryCount", ConfigAuditDiscoveredKeyClassification.KnownDescendant, "3");
            AssertDiscovered(report, "App.Ratio", ConfigAuditDiscoveredKeyClassification.KnownDescendant, "42.5");
            AssertDiscovered(
                report,
                "App.LongValue",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "9223372036854775807");
            AssertDiscovered(report, "App.HugeValue", ConfigAuditDiscoveredKeyClassification.KnownDescendant, "1E+100");
            var password = AssertDiscovered(
                report,
                "App.Password",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]");
            Assert.True(password.IsRedacted);
            var items = AssertDiscovered(report, "App.Items", ConfigAuditDiscoveredKeyClassification.KnownDescendant, null);
            Assert.False(items.IsRedacted);
            var tokenList = AssertDiscovered(
                report,
                "App.TokenList",
                ConfigAuditDiscoveredKeyClassification.KnownDescendant,
                "[redacted]");
            Assert.True(tokenList.IsRedacted);
            AssertDiscovered(
                report,
                "Application.Name",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                "lookalike");
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
                "Application.Name [Unknown to AppSurface audit registry] = lookalike",
                rendered,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Unused", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-one", rendered, StringComparison.Ordinal);
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
                "from-base");
            Assert.Contains(
                baseChild.Sources,
                source => source.FilePath?.EndsWith("appsettings.Staging.json", StringComparison.Ordinal) == true);
            var overrideChild = AssertDiscovered(
                report,
                "Composite.Override",
                ConfigAuditDiscoveredKeyClassification.Unknown,
                "from-override");
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

    private static ConfigAuditDiscoveredKey AssertDiscovered(
        ConfigAuditReport report,
        string key,
        ConfigAuditDiscoveredKeyClassification classification,
        string? displayValue)
    {
        var discoveredKey = Assert.Single(report.DiscoveredKeys, discoveredKey => discoveredKey.Key == key);
        Assert.Equal(classification, discoveredKey.Classification);
        Assert.Equal(displayValue, discoveredKey.DisplayValue);
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

    private sealed class ThrowingKeyEnumeratorProvider : IConfigProvider, IConfigAuditKeyEnumerator
    {
        public int Priority => 10;

        public string Name => nameof(ThrowingKeyEnumeratorProvider);

        public T? GetValue<T>(string environment, string key) => default;

        public IReadOnlyList<ConfigAuditProviderDiscoveredKey> EnumerateKeys(string environment) =>
            throw new InvalidOperationException("super-secret enumeration failure");
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
