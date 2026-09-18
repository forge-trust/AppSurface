using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

/// <summary>Exercises scalar binding, nested environment composition, providerless rescue, and invalid-shape audit redaction.</summary>
public sealed class ConfigCompositionExtendedBehaviorTests
{
    private const string EnvironmentName = "Production";

    [Fact]
    public void Execute_RepresentativeScalarSecretPayloadsBindThroughTheRuntimeContract()
    {
        Assert.Equal(Guid.Parse("8d7f1c10-1d6f-4b4a-8b2b-4d2a7b8e4c11"),
            ExecuteScalar<Guid>("8d7f1c10-1d6f-4b4a-8b2b-4d2a7b8e4c11").Token.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-09-15T12:34:56+00:00"),
            ExecuteScalar<DateTimeOffset>("\"2026-09-15T12:34:56+00:00\"").Token.Value);
        Assert.Equal(new Uri("https://secrets.example.test/api"),
            ExecuteScalar<Uri>("\"https://secrets.example.test/api\"").Token.Value);
        Assert.Equal(SecretKind.Second,
            ExecuteScalar<SecretKind>("Second").Token.Value);

        Assert.Equal(42, ExecuteScalar<int>("42").Token.Value);
        Assert.Equal(922337203685477000L, ExecuteScalar<long>("922337203685477000").Token.Value);
    }

    [Theory]
    [InlineData("guid", "not-a-guid")]
    [InlineData("date-time-offset", "not-a-date")]
    [InlineData("uri", "not-a-uri")]
    [InlineData("enum", "not-an-enum")]
    [InlineData("int", "not-an-integer")]
    [InlineData("long", "not-a-long")]
    public void Execute_RepresentativeScalarConversionFailuresAreOpaque(string scalar, string payload)
    {
        using var files = new FileFixture("{\"Service\":{\"Token\":{\"key\":\"opaque-reference\"}}}");
        var provider = new RecordingSecretProvider("test-provider", payload);
        var result = scalar switch
        {
            "guid" => ExecuteInvalid<Guid>(files.Provider, provider),
            "date-time-offset" => ExecuteInvalid<DateTimeOffset>(files.Provider, provider),
            "uri" => ExecuteInvalid<Uri>(files.Provider, provider),
            "enum" => ExecuteInvalid<SecretKind>(files.Provider, provider),
            "int" => ExecuteInvalid<int>(files.Provider, provider),
            "long" => ExecuteInvalid<long>(files.Provider, provider),
            _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar, null)
        };

        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("secret-value-conversion-failed", failure.Code);
        Assert.DoesNotContain(payload, failure.ToString(), StringComparison.Ordinal);
        Assert.Single(provider.Resolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ExactEnvironmentRescueAfterProviderlessSuccessOrAmbiguityKeepsOrderAndTrace(bool ambiguous)
    {
        using var files = new FileFixture("{\"Service\":{\"Token\":{\"key\":\"opaque-reference\"}}}");
        var alpha = new RecordingSecretProvider("alpha", "alpha-value");
        var bravo = new RecordingSecretProvider("bravo", "bravo-value");
        var environment = new TestEnvironmentProvider(new() { ["SERVICE__TOKEN"] = "environment-value" });

        if (!ambiguous)
        {
            bravo.Outcome = ConfigSecretProviderResolution.Missing("bravo");
        }

        var result = CreateEngine(files.Provider, [bravo, alpha], environment)
            .Execute(EnvironmentName, "Service", typeof(StringScalarOptions));

        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        var value = Assert.IsType<StringScalarOptions>(result.Value);
        Assert.Equal("environment-value", value.Token.Value);
        var slot = Assert.Single(result.Slots);
        Assert.Equal(ambiguous ? "secret-environment-rescued" : "secret-environment-supplied", slot.Code);
        Assert.Equal(nameof(EnvironmentConfigProvider), slot.ResolvedProvider);
        Assert.Equal(new[] { "alpha", "bravo" }, slot.Providers.Select(observation => observation.ProviderId));
        Assert.Equal(
            new[] { ConfigSecretProviderResolutionStatus.Resolved, ambiguous ? ConfigSecretProviderResolutionStatus.Resolved : ConfigSecretProviderResolutionStatus.Missing },
            slot.Providers.Select(observation => observation.Status));
        Assert.Equal(new[] { "alpha:Service:Token", "bravo:Service:Token" },
            alpha.ResolveSequence.Concat(bravo.ResolveSequence));
        Assert.Contains(slot.Sources, source =>
            source.ProviderName == nameof(EnvironmentConfigProvider)
            && source.EnvironmentVariableName == "SERVICE__TOKEN");
    }

    [Fact]
    public void Execute_NestedOrdinaryOnlyMemberUsesEnvironmentPatchBesideSecretSlot()
    {
        using var files = new FileFixture("""
            {"Service":{"Token":{"key":"opaque-reference"},"Metadata":{"Endpoint":"file-endpoint"}}}
            """);
        var provider = new RecordingSecretProvider("test-provider", "secret-value");
        var environment = new TestEnvironmentProvider(new() { ["SERVICE__METADATA__ENDPOINT"] = "environment-endpoint" });

        var result = CreateEngine(files.Provider, [provider], environment)
            .Execute(EnvironmentName, "Service", typeof(NestedOrdinaryOptions));

        var value = Assert.IsType<NestedOrdinaryOptions>(result.Value);
        Assert.Equal("environment-endpoint", value.Metadata.Endpoint);
        Assert.Equal("secret-value", value.Token.Value);
        Assert.Equal("test-provider", value.Token.ResolvedProvider);
        Assert.Contains(result.Sources, source =>
            source.EnvironmentVariableName == "SERVICE__METADATA__ENDPOINT"
            && source.ConfigPath == "Service.Metadata.Endpoint");
    }

    [Fact]
    public void Audit_UnsupportedSecretShapeRedactsEverySerializedSurface()
    {
        const string key = "opaque-resource-key-sentinel";
        const string version = "opaque-version-sentinel";
        const string payload = "opaque-payload-sentinel";
        using var files = new FileFixture(
            "{\"Service\":{"
            + "\"Token\":{\"key\":\"" + key + "\",\"version\":\"" + version + "\"},"
            + "\"Converted\":{\"NestedToken\":{\"key\":\"" + payload + "\",\"version\":\"" + version + "\"}}}}}");
        var environment = new TestEnvironmentProvider();
        var environmentProvider = new EnvironmentConfigProvider(environment);
        var reporter = new ConfigAuditReporter(
            environmentProvider,
            [files.Provider],
            [new ConfigAuditKnownEntry("Service", null, typeof(UnsupportedShapeOptions))],
            new ServiceCollection().BuildServiceProvider(),
            new ConfigAuditRedactor(),
            Options.Create(new ConfigAuditDictionaryKeyCorrelationOptions()));

        var report = reporter.GetReport(EnvironmentName);

        var root = Assert.Single(report.Entries);
        Assert.Equal(ConfigAuditEntryState.Invalid, root.State);
        Assert.Contains(root.Diagnostics, diagnostic => diagnostic.Code == "secret-destination-type-unsupported");
        var surfaces = new[]
        {
            JsonSerializer.Serialize(report),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new ConfigAuditTextRenderer().Render(report)
        };
        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(key, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(version, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(payload, surface, StringComparison.Ordinal);
        }
    }

    private static ScalarOptions<T> ExecuteScalar<T>(string payload) where T : notnull
    {
        using var files = new FileFixture("{\"Service\":{\"Token\":{\"key\":\"opaque-reference\"}}}");
        var provider = new RecordingSecretProvider("test-provider", payload);
        var result = CreateEngine(files.Provider, [provider]).Execute(
            EnvironmentName, "Service", typeof(ScalarOptions<T>));
        return Assert.IsType<ScalarOptions<T>>(result.Value);
    }

    private static ConfigCompositionExecutionResult ExecuteInvalid<T>(
        FileBasedConfigProvider files,
        RecordingSecretProvider provider) where T : notnull =>
        CreateEngine(files, [provider]).Execute(EnvironmentName, "Service", typeof(ScalarOptions<T>));

    private static ConfigCompositionEngine CreateEngine(
        FileBasedConfigProvider files,
        IReadOnlyList<IConfigSecretProvider> providers,
        TestEnvironmentProvider? environment = null) =>
        new(new EnvironmentConfigProvider(environment ?? new TestEnvironmentProvider()),
            [files], providers, [], new AppSurfaceConfigOptions(), TimeProvider.System);

    private sealed class StringScalarOptions
    {
        public Secret<string> Token { get; init; } = new();
    }

    private sealed class ScalarOptions<T> where T : notnull
    {
        public Secret<T> Token { get; init; } = new();
    }

    private sealed class NestedOrdinaryOptions
    {
        public Secret<string> Token { get; init; } = new();
        public NestedMetadata Metadata { get; init; } = new();
    }

    private sealed class NestedMetadata
    {
        public string Endpoint { get; init; } = "";
    }

    private sealed class UnsupportedShapeOptions
    {
        public Secret<string> Token { get; init; } = new();

        [JsonConverter(typeof(UnsupportedChildConverter))]
        public UnsupportedChild Converted { get; init; } = new();
    }

    private sealed class UnsupportedChild
    {
        public Secret<string> NestedToken { get; init; } = new();
    }

    private sealed class UnsupportedChildConverter : JsonConverter<UnsupportedChild>
    {
        public override UnsupportedChild Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();

        public override void Write(Utf8JsonWriter writer, UnsupportedChild value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private enum SecretKind
    {
        First,
        Second
    }

    private sealed class RecordingSecretProvider(string id, string payload) : IConfigSecretProvider
    {
        public string Id { get; } = id;
        public string Payload { get; } = payload;
        public ConfigSecretProviderResolution? Outcome { get; set; }
        public List<string> ResolveSequence { get; } = [];
        public List<ConfigSecretProviderResolution> Resolutions { get; } = [];

        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) =>
            ConfigSecretReferenceValidation.Supported();

        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            ResolveSequence.Add($"{Id}:{reference.LogicalPath}");
            var result = Outcome ?? ConfigSecretProviderResolution.Resolved(
                Payload, ConfigSecretSourceMetadata.Create(Id));
            Resolutions.Add(result);
            return result;
        }
    }

    private sealed class TestEnvironmentProvider(Dictionary<string, string?>? values = null) : IEnvironmentProvider
    {
        public string Environment => EnvironmentName;
        public bool IsDevelopment => false;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            values is not null && values.TryGetValue(name, out var value) ? value : defaultValue;
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("appsurface-accepted-regression-");
        public FileBasedConfigProvider Provider { get; }

        public FileFixture(string document)
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), document);
            Provider = new FileBasedConfigProvider(
                new FileLocation(directory.FullName), NullLogger<FileBasedConfigProvider>.Instance);
        }

        public void Dispose() => directory.Delete(recursive: true);
    }

    private sealed record FileLocation(string Directory) : IConfigFileLocationProvider;
}
