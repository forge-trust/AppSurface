using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretDestinationIdentityTests
{
    [Theory]
    [InlineData("file", null)]
    [InlineData("file", " Prefix ")]
    [InlineData("indexed", null)]
    [InlineData("indexed", " Prefix ")]
    [InlineData("mac", null)]
    [InlineData("mac", " Prefix ")]
    public void Preparation_UsesSelectedEncodingAndNormalizedNamespaceWithoutIo(string backend, string? prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "metadata-only-" + Guid.NewGuid().ToString("N"), "secrets.json");
        IAppSurfaceLocalSecretMigrationStore store = backend switch
        {
            "file" => new FileAppSurfaceLocalSecretStore(path),
            "indexed" => new NoIoIndexed(),
            _ => new MacOsV2CompatibilityLocalSecretStore(new InMemoryAppSurfaceLocalSecretStore(), new NoIoMac())
        };
        var key = AppSurfaceConfigKey.FromSegments("Literal.Dot", "Literal__Underscore", "Literal\\Slash");
        var result = store.GetKeyMigrationDestinationIdentity(" My App ", " Development ", prefix, key);
        Assert.True(result.Succeeded);
        Assert.Null(result.Diagnostic);
        var identity = Assert.IsType<AppSurfaceLocalSecretIdentity>(result.Identity);
        Assert.Equal("My-App", identity.ApplicationName);
        Assert.Equal("Development", identity.Environment);
        Assert.Equal(prefix is null ? null : "Prefix", identity.KeyPrefix);
        Assert.Equal(key, identity.Key);
        var expected = backend == "mac"
            ? $"appsurface:v2:My-App:Development:{identity.KeyPrefix}:{key.Value}"
            : $"appsurface:My-App:Development:{(prefix is null ? "" : "Prefix:")}{key.Value}";
        Assert.Equal(expected, identity.StorageName);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        var invalid = store.GetKeyMigrationDestinationIdentity("bad:app", "Development", null, key);
        Assert.False(invalid.Succeeded);
        Assert.NotNull(invalid.Diagnostic);
        Assert.Throws<ArgumentNullException>(() => store.GetKeyMigrationDestinationIdentity("app", "Development", null, null!));
    }

    [Fact]
    public void UnknownMigrationStore_DefaultPreparationReturnsUnsupportedWithoutCallingMigration()
    {
        IAppSurfaceLocalSecretMigrationStore store = new Unsupported();
        var result = store.GetKeyMigrationDestinationIdentity("app", "Development", null, AppSurfaceConfigKey.Parse("Key"));
        Assert.False(result.Succeeded);
        Assert.Equal("local-secret-migration-unsupported", result.Diagnostic?.Code);
    }

    private sealed class Unsupported : IAppSurfaceLocalSecretMigrationStore
    {
        public AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("No migration during preview.");
    }

    private sealed class NoIoIndexed : IndexedLocalSecretStore
    {
        public override string Name => "NoIoIndexed";
        internal override string MigrationStateDirectory => throw new InvalidOperationException("No lease or journal during preview.");
        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("No reads during preview.");
        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value) => throw new InvalidOperationException("No writes during preview.");
        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("No deletes during preview.");
        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("No doctor during preview.");
    }

    private sealed class NoIoMac : IMacOsSecItemInterop
    {
        public MacOsSecItemReadResult Read(MacOsSecItemQuery query) => throw new InvalidOperationException("No reads during preview.");
        public int Exists(MacOsSecItemQuery query) => throw new InvalidOperationException("No existence checks during preview.");
        public int Add(MacOsSecItemQuery query, byte[] value) => throw new InvalidOperationException("No writes during preview.");
        public int Update(MacOsSecItemQuery query, byte[] value) => throw new InvalidOperationException("No updates during preview.");
        public int Delete(MacOsSecItemQuery query) => throw new InvalidOperationException("No deletes during preview.");
    }
}
