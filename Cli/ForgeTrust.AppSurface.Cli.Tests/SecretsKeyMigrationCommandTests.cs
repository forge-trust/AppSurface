using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class SecretsKeyMigrationCommandTests
{
    [Theory]
    [InlineData("Microsoft.Hosting.Lifetime", "['Microsoft.Hosting.Lifetime']", 1)]
    [InlineData("Logging:LogLevel:Microsoft.Hosting.Lifetime", "['Logging', 'LogLevel', 'Microsoft.Hosting.Lifetime']", 3)]
    public async Task Preview_ShowsStrictSegmentsWithoutInvokingMigration(string destination, string segments, int count)
    {
        var store = new RecordingMigrationStore();
        var command = CreateCommand(store, destination);
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Contains($"Destination segments: {segments}", output, StringComparison.Ordinal);
        Assert.Contains($"Destination storage identity: 'appsurface:MyApp:Development:Payments:{destination}'", output, StringComparison.Ordinal);
        Assert.Contains("repeat with --apply", output, StringComparison.Ordinal);
        Assert.Equal(count, AppSurfaceConfigKey.Parse(destination).Segments.Length);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(":Legacy..Key__Mixed\\Path", "Microsoft.Hosting.Lifetime", 1)]
    [InlineData("appsurface:MyApp:Development:Payments:O'Brien.ApiKey", "Logging:LogLevel:Microsoft.Hosting.Lifetime", 3)]
    public async Task Apply_PassesExactSourceAndStrictDestinationAfterDisplayingPreview(string source, string destination, int segmentCount)
    {
        using var console = new FakeInMemoryConsole();
        var store = new RecordingMigrationStore
        {
            BeforeMigration = () => Assert.Contains("Destination segments:", console.ReadOutputString(), StringComparison.Ordinal)
        };
        var command = CreateCommand(store, destination);
        command.SourceStoredKey = source;
        command.Apply = true;
        command.SecretToolPath = "/test/tools/secret-tool";

        await command.ExecuteAsync(console);

        Assert.Equal(1, store.Calls);
        Assert.Equal(source, store.Source);
        Assert.Equal(destination, store.Destination!.Value);
        Assert.Equal(segmentCount, store.Destination.Segments.Length);
        Assert.Equal(("MyApp", "Development", "Payments"), store.Namespace);
        var output = console.ReadOutputString();
        Assert.Contains("--secret-tool-path '/test/tools/secret-tool'", output, StringComparison.Ordinal);
        Assert.Contains("Migration state: Complete", output, StringComparison.Ordinal);
        Assert.Contains("Migration id: 'operation-1'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Source retained", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value-sentinel", output, StringComparison.Ordinal);
        if (source.Contains('\''))
        {
            Assert.Contains("O'\\''Brien.ApiKey", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(":A")]
    [InlineData("A::B")]
    [InlineData("A\nB")]
    [InlineData("A: B")]
    public async Task InvalidDestination_FailsBeforeCreatingStore(string destination)
    {
        var command = CreateCommand(new RecordingMigrationStore(), destination);
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.Contains("config-key-invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.StoreCreations);
        Assert.Empty(console.ReadOutputString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptySource_FailsBeforeCreatingStore(string source)
    {
        var command = CreateCommand(new RecordingMigrationStore(), "Payments:ApiKey");
        command.SourceStoredKey = source;
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.Contains("--from-stored-key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.StoreCreations);
    }

    [Fact]
    public async Task OversizedStorageIdentity_FailsWithoutMigration()
    {
        var store = new RecordingMigrationStore();
        var command = CreateCommand(store, new string('x', 512));
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.Contains("local-secret-identity-too-long", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("Legacy\u001b[2J.Key")]
    [InlineData("Legacy\r\nForged: complete")]
    [InlineData("Legacy\u202eKey")]
    public async Task UnsafeLegacyIdentifier_IsEscapedForDisplayAndPreservedForMigration(string source)
    {
        var store = new RecordingMigrationStore();
        var command = CreateCommand(store, "New:Key");
        command.SourceStoredKey = source;
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Equal(source, store.Source);
        Assert.DoesNotContain(source, output, StringComparison.Ordinal);
        Assert.Contains("\\u", output, StringComparison.Ordinal);
        Assert.Contains("Command preview omitted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Command: appsurface", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedIdentifier_IsBoundedWithDigestAndNeverReusedAsCommand()
    {
        var source = new string('x', 10_000);
        var store = new RecordingMigrationStore();
        var command = CreateCommand(store, "New:Key");
        command.SourceStoredKey = source;
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Equal(source, store.Source);
        Assert.Contains(new string('x', 256) + "…#", output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 257), output, StringComparison.Ordinal);
        Assert.DoesNotContain("Command: appsurface", output, StringComparison.Ordinal);
        Assert.True(output.Length < 2000);
    }

    [Fact]
    public async Task UnsafeNativeToolPath_IsNeverPrintedAsExecutableOutput()
    {
        var command = CreateCommand(new RecordingMigrationStore(), "New:Key");
        command.SecretToolPath = "/tools/\u001bsecret-tool";
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.DoesNotContain('\u001b', output);
        Assert.Contains("Command preview omitted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_UsesSelectedBackendDestinationEncodingWithoutReadingValues()
    {
        var nativeDestination = "appsurface:v2:MyApp:Development:Payments:New:Key";
        var store = new RecordingMigrationStore { DestinationStorageName = nativeDestination };
        var command = CreateCommand(store, "New:Key");
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        Assert.Contains($"Destination storage identity: '{nativeDestination}'", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Equal(1, store.PreparationCalls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Preview_WithFileSelectionDoesNotCreateStoreFile()
    {
        var directory = Directory.CreateTempSubdirectory("config-key-preview-");
        try
        {
            var path = Path.Join(directory.FullName, "store.json");
            var command = new SecretsMigrateKeyCommand
            {
                ApplicationName = "MyApp",
                EnvironmentName = "Development",
                StoreFile = path,
                SourceStoredKey = "Legacy.Key",
                DestinationKey = "New:Key"
            };
            using var console = new FakeInMemoryConsole();

            await command.ExecuteAsync(console);

            Assert.False(File.Exists(path));
            Assert.Contains($"--store-file '{path}'", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("--prefix", console.ReadOutputString(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedStore_FailsWithoutReadingValues()
    {
        var command = CreateCommand(new UnusedStore(), "New:Key");
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.Contains("local-secret-migration-unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedMigration_ReportsDiagnosticOrSafeFallback(bool includeDiagnostic)
    {
        var store = new RecordingMigrationStore { Fail = true, IncludeDiagnostic = includeDiagnostic };
        var command = CreateCommand(store, "New:Key");
        command.Apply = true;
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

        Assert.Contains(includeDiagnostic ? "test-migration-failed" : "Exact-key migration failed.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Migration state: Complete", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value-sentinel", exception.Message + console.ReadOutputString(), StringComparison.Ordinal);
    }

    private static TestCommand CreateCommand(IAppSurfaceLocalSecretStore store, string destination) => new(store)
    {
        ApplicationName = "MyApp",
        EnvironmentName = "Development",
        KeyPrefix = "Payments",
        SourceStoredKey = "Legacy..ApiKey",
        DestinationKey = destination
    };

    private sealed class TestCommand(IAppSurfaceLocalSecretStore store) : SecretsMigrateKeyCommand
    {
        public int StoreCreations { get; private set; }

        protected override IAppSurfaceLocalSecretStore CreatePlatformStore(AppSurfaceLocalSecretsOptions options)
        {
            StoreCreations++;
            return store;
        }
    }

    private class UnusedStore : IAppSurfaceLocalSecretStore
    {
        public string Name => "test-store";
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("Unexpected value read.");
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => throw new InvalidOperationException("Unexpected write.");
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("Unexpected delete.");
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("Unexpected listing.");
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("Unexpected doctor.");
    }

    private sealed class RecordingMigrationStore : UnusedStore, IAppSurfaceLocalSecretMigrationStore
    {
        public int PreparationCalls { get; private set; }
        public string? DestinationStorageName { get; init; }
        public int Calls { get; private set; }
        public string? Source { get; private set; }
        public AppSurfaceConfigKey? Destination { get; private set; }
        public (string, string, string?) Namespace { get; private set; }
        public Action? BeforeMigration { get; init; }
        public bool Fail { get; init; }
        public bool IncludeDiagnostic { get; init; }

        public AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix) =>
            throw new InvalidOperationException("Unexpected bulk migration.");

        public AppSurfaceLocalSecretIdentityResult GetKeyMigrationDestinationIdentity(string applicationName, string environment,
            string? keyPrefix, AppSurfaceConfigKey destinationKey)
        {
            PreparationCalls++;
            var result = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(applicationName, environment, keyPrefix, destinationKey.Value);
            return result.Identity is { } identity && DestinationStorageName is not null
                ? AppSurfaceLocalSecretIdentityResult.Valid(identity with { StorageName = DestinationStorageName })
                : result;
        }

        public AppSurfaceLocalSecretKeyMigrationResult MigrateKey(string applicationName, string environment, string? keyPrefix,
            string sourceStoredKey, AppSurfaceConfigKey destinationKey)
        {
            BeforeMigration?.Invoke();
            Calls++;
            Source = sourceStoredKey;
            Destination = destinationKey;
            Namespace = (applicationName, environment, keyPrefix);
            return new(
                Fail ? LocalSecretResultStatus.ProviderFailed : LocalSecretResultStatus.Found,
                "operation-1",
                Fail ? AppSurfaceLocalSecretMigrationState.SourceDeletePending : AppSurfaceLocalSecretMigrationState.Complete,
                sourceStoredKey,
                destinationKey.Value,
                IncludeDiagnostic ? new("test-migration-failed", "Migration paused.", "Delete was not confirmed.", "Retry the migration.") : null,
                Name);
        }
    }
}
