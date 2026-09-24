using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class MacOsMigrationBackendGuardTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("index-read")]
    [InlineData("index-write")]
    public void Backend_ShouldRejectUnconfirmedNativeResults(string operation)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var source = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Source").Identity!;
        var destination = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Destination").Identity!;
        var owner = new MacOsV2CompatibilityLocalSecretStore(new InMemoryAppSurfaceLocalSecretStore(), new Results(operation));
        var backend = new MacOsV2MigrationBackend(owner, source, destination, true);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        backend.CommitJournal(new("id", app, "Development", null, source.StorageName, destination.StorageName, AppSurfaceLocalSecretMigrationState.DestinationVerified));
        Assert.Throws<IOException>(() =>
        {
            switch (operation)
            {
                case "read": backend.ReadExact(source.StorageName); break;
                case "write": backend.WriteExact(destination.StorageName, "marker"); break;
                case "delete": backend.DeleteExact(source.StorageName); break;
                default: backend.PublishIndex(); break;
            }
        });
    }

    [Fact]
    public void Backend_ShouldAcceptConfirmedMissingDeleteAndRejectMissingJournal()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var source = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Source").Identity!;
        var destination = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Destination").Identity!;
        var owner = new MacOsV2CompatibilityLocalSecretStore(new InMemoryAppSurfaceLocalSecretStore(), new Results("missing"));
        var backend = new MacOsV2MigrationBackend(owner, source, destination, true);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        backend.DeleteExact(source.StorageName);
        Assert.Throws<IOException>(backend.PublishIndex);
    }

    [Fact]
    public void Backend_ShouldRejectLegacyFoundWithoutData()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var source = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Source").Identity!;
        var destination = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Destination").Identity!;
        var backend = new MacOsV2MigrationBackend(new MacOsV2CompatibilityLocalSecretStore(new NullLegacy(), new Results("missing")), source, destination, false);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Throws<IOException>(() => backend.ReadExact(source.StorageName));
    }

    [Fact]
    public void UnsupportedLegacyAdapter_ShouldStopBeforePreparedOrAnyIo()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var owner = new MacOsV2CompatibilityLocalSecretStore(new UnsupportedLegacy(), new ForbiddenInterop());
        var destination = owner.GetKeyMigrationDestinationIdentity(app, "Development", null, AppSurfaceConfigKey.Parse("Destination")).Identity!;
        var source = LocalSecretMigrationIdentity.Resolve(app, "Development", null, $"appsurface:{app}:Development:Source", false)!;
        var backend = new MacOsV2MigrationBackend(owner, source, destination, false);
        Assert.False(backend.SupportsDurableMigration);
        var result = owner.MigrateKey(app, "Development", null, source.StorageName, destination.Key);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-migration-unsupported", result.Diagnostic?.Code);
        Assert.Null(backend.ReadJournal());
        Assert.Throws<IOException>(() => owner.ReadMigrationValue(source, false));
        Assert.Throws<IOException>(() => owner.DeleteMigrationValue(source, false));
    }

    private sealed class NullLegacy : IndexedLocalSecretStore
    {
        public override string Name => "LegacyFixture";
        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity) => new(LocalSecretResultStatus.Found, null, null, Name);
        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value) => throw new NotSupportedException();
        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) => throw new NotSupportedException();
        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) => throw new NotSupportedException();
    }

    private sealed class UnsupportedLegacy : IAppSurfaceLocalSecretStore
    {
        public string Name => "UnsupportedLegacyFixture";
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("No legacy value I/O is allowed.");
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => throw new InvalidOperationException("No legacy mutation is allowed.");
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => throw new InvalidOperationException("No legacy mutation is allowed.");
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("No legacy index I/O is allowed.");
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => throw new InvalidOperationException("No legacy I/O is allowed.");
    }

    private sealed class ForbiddenInterop : IMacOsSecItemInterop
    {
        public MacOsSecItemReadResult Read(MacOsSecItemQuery query) => throw new InvalidOperationException("No v2 value I/O is allowed.");
        public int Exists(MacOsSecItemQuery query) => throw new InvalidOperationException("No v2 I/O is allowed.");
        public int Add(MacOsSecItemQuery query, byte[] value) => throw new InvalidOperationException("No v2 mutation is allowed.");
        public int Update(MacOsSecItemQuery query, byte[] value) => throw new InvalidOperationException("No v2 mutation is allowed.");
        public int Delete(MacOsSecItemQuery query) => throw new InvalidOperationException("No v2 mutation is allowed.");
    }

    private sealed class Results(string operation) : IMacOsSecItemInterop
    {
        public MacOsSecItemReadResult Read(MacOsSecItemQuery query) =>
            new(operation == "read" || operation == "index-read" && query.Account == "__appsurface_index__" ? -25308 : -25300, null);
        public int Exists(MacOsSecItemQuery query) => -25300;
        public int Add(MacOsSecItemQuery query, byte[] value) => operation is "write" or "index-write" ? -25308 : 0;
        public int Update(MacOsSecItemQuery query, byte[] value) => -25308;
        public int Delete(MacOsSecItemQuery query) => operation == "delete" ? -25308 : -25300;
    }
}
