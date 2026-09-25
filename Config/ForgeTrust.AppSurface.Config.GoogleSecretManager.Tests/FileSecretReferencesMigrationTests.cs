using System.Text;
using ForgeTrust.AppSurface.Core;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

/// <summary>
/// Verifies the root-sized migration boundary for a file-declared secret reference.
/// </summary>
public sealed class FileSecretReferencesMigrationTests
{
    [Fact]
    public void PartialMigrationWithRemainingMappedSiblingFailsBeforeGoogleIo()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"api-key\",\"version\":\"4\"},\"Plain\":\"legacy\"}}");
        var client = new RecordingClient(_ => new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("unused"), "unused"));
        var manager = fixture.CreateManager(CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Service:Plain", "plain-key", version: "4");
        }));

        var exception = Assert.Throws<ConfigurationCompositionException>(
            () => manager.GetValue<MigrationOptions>("Production", "Service"));

        Assert.Contains(exception.Failures, failure => failure.Code == "secret-claim-overlap");
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void RootConversionWithDisabledReferencePerformsNoInlineRead()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"Endpoint\":\"https://service.test\",\"ApiKey\":{\"key\":\"api-key\",\"version\":\"4\",\"enabled\":false}}}");
        var client = new RecordingClient(_ => throw new InvalidOperationException("Google must not be called"));
        var value = fixture.CreateManager(CreateProvider(client, options => options.ProjectId = "project"))
            .GetValue<MigrationOptions>("Production", "Service");

        Assert.NotNull(value);
        Assert.Equal("https://service.test", value!.Endpoint);
        Assert.False(value.ApiKey.Enabled);
        Assert.False(value.ApiKey.HasValue);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void RootConversionWithEnabledReferenceReportsGoogleProvenance()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"Endpoint\":\"https://service.test\",\"ApiKey\":{\"key\":\"api-key\",\"version\":\"4\",\"provider\":\"google-secret-manager\"}}}");
        var client = new RecordingClient(_ => new AppSurfaceGoogleSecretPayload(
            Encoding.UTF8.GetBytes("google-value"), "projects/project/secrets/api-key/versions/4"));
        var value = fixture.CreateManager(CreateProvider(client, options => options.ProjectId = "project"))
            .GetValue<MigrationOptions>("Production", "Service");

        Assert.NotNull(value);
        Assert.True(value!.ApiKey.TryGetValue(out var secret));
        Assert.Equal("google-value", secret);
        Assert.Equal(GoogleSecretManagerConfigProvider.ProviderId, value.ApiKey.ResolvedProvider);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("error")]
    public void EnabledFailureIsRescuedOnlyByExactEnvironmentValue(string failure)
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"Endpoint\":\"https://service.test\",\"ApiKey\":{\"key\":\"api-key\",\"version\":\"4\",\"provider\":\"google-secret-manager\"}}}",
            new Dictionary<string, string?> { ["SERVICE_APIKEY"] = "migration-value" });
        var client = new RecordingClient(_ => failure switch
        {
            "missing" => throw new RpcException(new Status(StatusCode.NotFound, "opaque")),
            "denied" => throw new RpcException(new Status(StatusCode.PermissionDenied, "opaque")),
            _ => throw new InvalidOperationException("opaque")
        });
        var value = fixture.CreateManager(CreateProvider(client, options => options.ProjectId = "project"))
            .GetValue<MigrationOptions>("Production", "Service");

        Assert.NotNull(value);
        Assert.True(value!.ApiKey.TryGetValue(out var secret));
        Assert.Equal("migration-value", secret);
        Assert.Equal("EnvironmentConfigProvider", value.ApiKey.ResolvedProvider);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void MigratingBothSiblingsReplacesLegacyPerPropertyReadsWithOneTypedRootRead()
    {
        using var legacyFiles = FileFixture.Create("""{"Service":{"Endpoint":"https://service.test"}}""");
        var legacyClient = new RecordingClient(resource => new AppSurfaceGoogleSecretPayload(
            Encoding.UTF8.GetBytes(resource.Contains("api-key", StringComparison.Ordinal) ? "api-value" : "signing-value"), resource));
        var legacy = legacyFiles.CreateManager(CreateProvider(legacyClient, options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Service:ApiKey", "api-key", "4");
            options.MapSecret("Service:SigningKey", "signing-key", "7");
        }));
        // This is the former consumer-owned glue: independent lookups and assignments for both siblings.
        var oldApi = legacy.GetValue<string>("Production", "Service:ApiKey");
        var oldSigning = legacy.GetValue<string>("Production", "Service:SigningKey");
        Assert.Equal(2, legacyClient.Calls);

        using var migratedFiles = FileFixture.Create(TwoSiblingDocument(enabled: true));
        var migratedClient = new RecordingClient(resource => new AppSurfaceGoogleSecretPayload(
            Encoding.UTF8.GetBytes(resource.Contains("api-key", StringComparison.Ordinal) ? "api-value" : "signing-value"), resource));
        // Both mappings are removed together. No process or fixture environment override is supplied.
        var migrated = migratedFiles.CreateManager(CreateProvider(migratedClient, options => options.ProjectId = "project"))
            .GetValue<TwoSiblingOptions>("Production", "Service");

        Assert.NotNull(migrated);
        Assert.Equal(oldApi, migrated.ApiKey.Value);
        Assert.Equal(oldSigning, migrated.SigningKey.Value);
        Assert.Equal("https://service.test", migrated.Endpoint);
        Assert.Equal(GoogleSecretManagerConfigProvider.ProviderId, migrated.ApiKey.ResolvedProvider);
        Assert.Equal(GoogleSecretManagerConfigProvider.ProviderId, migrated.SigningKey.ResolvedProvider);
        Assert.Equal(2, migratedClient.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothDisabledSiblingsPreserveIndependentExactEnvironmentRescuesWithoutReads(bool rescue)
    {
        var variables = rescue ? new Dictionary<string, string?>
        {
            ["SERVICE__APIKEY"] = "api-rescue",
            ["SERVICE__SIGNINGKEY"] = "signing-rescue"
        } : [];
        using var files = FileFixture.Create(TwoSiblingDocument(enabled: false), variables);
        var client = new RecordingClient(_ => throw new InvalidOperationException("Disabled references must not read."));

        var value = files.CreateManager(CreateProvider(client, options => options.ProjectId = "project"))
            .GetValue<TwoSiblingOptions>("Production", "Service");

        Assert.NotNull(value);
        Assert.False(value.ApiKey.Enabled);
        Assert.False(value.SigningKey.Enabled);
        Assert.Equal(rescue, value.ApiKey.HasValue);
        Assert.Equal(rescue, value.SigningKey.HasValue);
        Assert.Equal(0, client.Calls);
        if (rescue)
        {
            Assert.Equal("api-rescue", value.ApiKey.Value);
            Assert.Equal("signing-rescue", value.SigningKey.Value);
            Assert.Equal(nameof(EnvironmentConfigProvider), value.ApiKey.ResolvedProvider);
            Assert.Equal(nameof(EnvironmentConfigProvider), value.SigningKey.ResolvedProvider);
        }
    }

    private static string TwoSiblingDocument(bool enabled) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Service = new
        {
            Endpoint = "https://service.test",
            ApiKey = new { key = "api-key", version = "4", provider = GoogleSecretManagerConfigProvider.ProviderId, enabled },
            SigningKey = new { key = "signing-key", version = "7", provider = GoogleSecretManagerConfigProvider.ProviderId, enabled }
        }
    });

    private sealed class TwoSiblingOptions
    {
        public required string Endpoint { get; init; }
        public required Secret<string> ApiKey { get; init; }
        public required Secret<string> SigningKey { get; init; }
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(
        IAppSurfaceGoogleSecretManagerClient client,
        Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        configure(options);
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private sealed class MigrationOptions
    {
        public required string Endpoint { get; init; }
        public required Secret<string> ApiKey { get; init; }
    }

    private sealed class RecordingClient(
        Func<string, AppSurfaceGoogleSecretPayload> access) : IAppSurfaceGoogleSecretManagerClient
    {
        public int Calls { get; private set; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Calls++;
            return access(resourceName);
        }
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly string directory;
        private readonly IReadOnlyDictionary<string, string?> environmentValues;

        private FileFixture(string directory, IReadOnlyDictionary<string, string?> environmentValues)
        {
            this.directory = directory;
            this.environmentValues = environmentValues;
        }

        public static FileFixture Create(
            string json,
            IReadOnlyDictionary<string, string?>? environmentValues = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "appsurface-file-secret-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), json);
            return new(directory, environmentValues ?? new Dictionary<string, string?>());
        }

        public IConfigManager CreateManager(GoogleSecretManagerConfigProvider google)
        {
            var file = new FileBasedConfigProvider(
                new TestFileLocationProvider(directory),
                NullLogger<FileBasedConfigProvider>.Instance);
            var environment = new EnvironmentConfigProvider(new TestEnvironmentProvider(environmentValues));
            return new DefaultConfigManager(
                environment,
                [file, google],
                NullLogger<DefaultConfigManager>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestEnvironmentProvider(
        IReadOnlyDictionary<string, string?> values) : IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            values.TryGetValue(name, out var value) ? value : defaultValue;

        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            values.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    private sealed class TestFileLocationProvider(string directory) : IConfigFileLocationProvider
    {
        public string Directory { get; } = directory;
    }
}
