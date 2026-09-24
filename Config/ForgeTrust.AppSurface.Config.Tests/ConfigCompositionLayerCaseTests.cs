using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionLayerCaseTests
{
    [Fact]
    public void ResolveRaw_CanonicalizesCaseVariantPathAndRetainsOrdinarySiblings()
    {
        using var fixture = new FileFixture(
            "{\"Service\":{\"ApiKey\":{\"key\":\"old\",\"version\":\"1\"},\"Endpoint\":\"https://service\"}}",
            "{\"service\":{\"apikey\":{\"key\":\"new\",\"version\":\"2\"},\"Region\":\"us-east1\"}}");

        var raw = ResolveRaw(fixture.Provider, "Service");
        var root = JsonNode.Parse(raw)!;
        var service = Assert.IsType<JsonObject>(root);
        var apiKey = Assert.Single(service, pair => pair.Key.Equals("apikey", StringComparison.OrdinalIgnoreCase)).Value;
        var descriptor = Assert.IsType<JsonObject>(apiKey);

        Assert.Equal("new", descriptor["key"]!.GetValue<string>());
        Assert.Equal("2", descriptor["version"]!.GetValue<string>());
        Assert.Equal("https://service", service["Endpoint"]!.GetValue<string>());
        Assert.Equal("us-east1", service["Region"]!.GetValue<string>());
        Assert.Single(service, pair => pair.Key.Equals("apikey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveRaw_HigherCaseVariantNullPreservesLowerOrdinaryValue()
    {
        using var fixture = new FileFixture(
            """{"Service":{"Endpoint":"https://lower.test","Settings":{"RetryCount":3}}}""",
            """{"service":{"endpoint":null,"settings":{"retrycount":null,"Region":"us-east1"}}}""");

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(ResolveRaw(fixture.Provider, "SERVICE")));

        Assert.Equal("https://lower.test", root["Endpoint"]!.GetValue<string>());
        var settings = GetObject(root, "Settings");
        Assert.Equal(3, settings["RetryCount"]!.GetValue<int>());
        Assert.Equal("us-east1", settings["Region"]!.GetValue<string>());
        Assert.Single(root, pair => pair.Key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveRaw_OrdinaryObjectWithDescriptorLikeMembersStillDeepMerges()
    {
        using var fixture = new FileFixture(
            "{\"Service\":{\"Settings\":{\"key\":{\"lower\":1},\"version\":{\"base\":true},\"provider\":\"old-provider\",\"enabled\":true}}}",
            "{\"service\":{\"settings\":{\"key\":{\"upper\":2},\"provider\":\"new-provider\"}}}");

        var settings = GetObject(ResolveRaw(fixture.Provider, "Service"), "settings");

        Assert.Equal(1, GetObject(settings, "key")["lower"]!.GetValue<int>());
        Assert.Equal(2, GetObject(settings, "key")["upper"]!.GetValue<int>());
        Assert.True(GetObject(settings, "version")["base"]!.GetValue<bool>());
        Assert.Equal("new-provider", settings["provider"]!.GetValue<string>());
        Assert.True(settings["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Execute_CompilerUsesLatestCaseVariantDescriptorAtomically()
    {
        using var fixture = new FileFixture(
            "{\"Service\":{\"ApiKey\":{\"key\":\"old\",\"version\":\"1\"}}}",
            "{\"Service\":{\"ApiKey\":{\"key\":\"new\",\"version\":\"7\"}}}");
        var provider = new RecordingSecretProvider();
        var engine = CreateEngine(fixture.Provider, provider);

        var result = engine.Execute(Environments.Production, "Service", typeof(SecretOptions));

        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        Assert.Equal("new", Assert.Single(provider.References).Key);
        Assert.Equal("7", provider.References[0].Version);
        Assert.Equal("resolved", Assert.IsType<SecretOptions>(result.Value).ApiKey.Value);
    }

    [Fact]
    public void Execute_LatestDisabledCaseVariantDescriptorDoesNotResolve()
    {
        using var fixture = new FileFixture(
            "{\"Service\":{\"ApiKey\":{\"key\":\"old\",\"version\":\"1\"}}}",
            "{\"Service\":{\"ApiKey\":{\"key\":\"new\",\"version\":\"7\",\"enabled\":false}}}");
        var provider = new RecordingSecretProvider();
        var engine = CreateEngine(fixture.Provider, provider);

        var result = engine.Execute(Environments.Production, "Service", typeof(SecretOptions));

        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        Assert.Empty(provider.References);
        var options = Assert.IsType<SecretOptions>(result.Value);
        Assert.False(options.ApiKey.Enabled);
        Assert.False(options.ApiKey.HasValue);
    }

    private static string ResolveRaw(FileBasedConfigProvider provider, string key) =>
        ((IConfigCompositionValueProvider)provider).ResolveRaw(Environments.Production, key).ReadRaw()!;

    private static JsonObject GetObject(string raw, string name)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(raw));
        return Assert.IsType<JsonObject>(root.First(pair =>
            pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value);
    }

    private static JsonObject GetObject(JsonObject root, string name) =>
        Assert.IsType<JsonObject>(root.First(pair =>
            pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value);

    private static ConfigCompositionEngine CreateEngine(
        FileBasedConfigProvider files, RecordingSecretProvider provider) =>
        new(new EnvironmentConfigProvider(new EmptyEnvironmentProvider()), [files], [provider], [],
            new AppSurfaceConfigOptions(), TimeProvider.System);

    private sealed class FileFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("appsurface-composition-case-");
        public FileBasedConfigProvider Provider { get; }

        public FileFixture(string baseDocument, string overrideDocument)
        {
            File.WriteAllText(Path.Combine(_directory.FullName, "appsettings.json"), baseDocument);
            File.WriteAllText(Path.Combine(_directory.FullName, "config_override.json"), overrideDocument);
            Provider = new FileBasedConfigProvider(
                new FileLocation(_directory.FullName), NullLogger<FileBasedConfigProvider>.Instance);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed record FileLocation(string Directory) : IConfigFileLocationProvider;

    private sealed class SecretOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
    }

    private sealed class RecordingSecretProvider : IConfigSecretProvider
    {
        public string Id => "test-provider";
        public List<ConfigSecretReference> References { get; } = [];

        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) =>
            ConfigSecretReferenceValidation.Supported();

        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            References.Add(reference);
            return ConfigSecretProviderResolution.Resolved(
                "resolved", ConfigSecretSourceMetadata.Create(Id));
        }
    }

    private sealed class EmptyEnvironmentProvider : IEnvironmentProvider
    {
        public string Environment => Environments.Production;
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
