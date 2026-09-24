using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretFileMigrationRecoveryTests
{
    [Fact]
    public void RetainedJournal_AllowsUnrelatedMigrationButProtectsBothIdentifiersUntilRelease()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        var bytesBeforeRecovery = File.ReadAllBytes(fixture.Path);

        var preview = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, false, false);
        Assert.Equal(LocalSecretResultStatus.Found, preview.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, preview.State);
        Assert.False(preview.SourcePresent);
        Assert.False(preview.DestinationPresent);
        Assert.False(preview.Retained);
        Assert.False(File.Exists(fixture.Path + ".migration-recovery.json"));

        var wrongState = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId,
            true, false, AppSurfaceLocalSecretMigrationState.DestinationWritten);
        Assert.Equal("local-secret-migration-recovery-state-changed", wrongState.Diagnostic?.Code);
        Assert.Equal(bytesBeforeRecovery, File.ReadAllBytes(fixture.Path));

        var retained = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId,
            true, false, preview.State);
        Assert.Equal(LocalSecretResultStatus.Found, retained.Status);
        Assert.True(retained.Retained);
        Assert.Equal(bytesBeforeRecovery, File.ReadAllBytes(fixture.Path));
        Assert.DoesNotContain("secret-marker", File.ReadAllText(fixture.Path + ".migration-recovery.json"));

        var reopened = new FileAppSurfaceLocalSecretStore(fixture.Path);
        var doctor = reopened.Doctor("App", "Development", null);
        Assert.Equal(LocalSecretResultStatus.Missing, doctor.Status);
        Assert.Equal("local-secret-migration-recovery-pending", doctor.Diagnostic?.Code);
        Assert.Contains(failed.MigrationId, doctor.Diagnostic!.ToDisplayString(), StringComparison.Ordinal);

        var overlappingSource = reopened.MigrateKey("App", "Development", null, fixture.MissingSource.ToUpperInvariant(),
            fixture.OtherDestination.Key);
        Assert.Equal("local-secret-migration-recovery-conflict", overlappingSource.Diagnostic?.Code);
        var overlappingDestination = reopened.MigrateKey("App", "Development", null, fixture.OtherSource.StorageName,
            fixture.Destination.Key);
        Assert.Equal("local-secret-migration-recovery-conflict", overlappingDestination.Diagnostic?.Code);
        var crossNamespaceSource = reopened.MigrateKey("OtherApp", "Development", null, fixture.MissingSource,
            fixture.OtherDestination.Key);
        Assert.Equal("local-secret-migration-recovery-conflict", crossNamespaceSource.Diagnostic?.Code);

        Assert.Equal(LocalSecretResultStatus.Found, reopened.Set(fixture.OtherSource, "secret-marker").Status);
        var unrelated = reopened.MigrateKey("App", "Development", null, fixture.OtherSource.StorageName,
            fixture.OtherDestination.Key);
        Assert.Equal(LocalSecretResultStatus.Found, unrelated.Status);
        Assert.Equal("secret-marker", reopened.Get(fixture.OtherDestination).Value);
        Assert.Equal("local-secret-migration-recovery-not-needed",
            reopened.RecoverKeyMigration("App", "Development", null, unrelated.MigrationId, false, false).Diagnostic?.Code);

        var releasePreview = reopened.RecoverKeyMigration("App", "Development", null, failed.MigrationId, false, true);
        Assert.True(releasePreview.Retained);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, releasePreview.State);
        var wrongId = reopened.RecoverKeyMigration("App", "Development", null, new string('0', 32), true, true,
            releasePreview.State);
        Assert.Equal("local-secret-migration-recovery-id-mismatch", wrongId.Diagnostic?.Code);
        var released = reopened.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, true,
            releasePreview.State);
        Assert.Equal(LocalSecretResultStatus.Found, released.Status);
        Assert.False(released.Retained);
        Assert.NotEqual("local-secret-migration-recovery-pending",
            reopened.Doctor("App", "Development", null).Diagnostic?.Code);
        Assert.Equal("local-secret-migration-source-missing",
            reopened.MigrateKey("App", "Development", null, fixture.MissingSource, fixture.Destination.Key).Diagnostic?.Code);
    }

    [Fact]
    public void FailedActiveSlotTransition_RetainsGuardAndCanBeRetried()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        var faultFiles = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(
            () => !OperatingSystem.IsWindows(), OperatingSystem.IsMacOS,
            beforeMove: temporary =>
            {
                if (temporary.Contains(".migration-journal.json.", StringComparison.Ordinal))
                    throw new IOException("Injected active-slot failure.");
            });
        var interrupted = new FileAppSurfaceLocalSecretStore(fixture.Path, faultFiles)
            .RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
                AppSurfaceLocalSecretMigrationState.Prepared);
        Assert.Equal(LocalSecretResultStatus.Unavailable, interrupted.Status);
        Assert.True(File.Exists(fixture.Path + ".migration-recovery.json"));
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared,
            JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(fixture.Path + ".migration-journal.json"))?.State);

        var reopened = new FileAppSurfaceLocalSecretStore(fixture.Path);
        Assert.Equal("local-secret-migration-recovery-conflict",
            reopened.MigrateKey("App", "Development", null, fixture.MissingSource, fixture.Destination.Key).Diagnostic?.Code);
        var resumed = reopened.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
            AppSurfaceLocalSecretMigrationState.Prepared);
        Assert.Equal(LocalSecretResultStatus.Found, resumed.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Retained,
            JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(fixture.Path + ".migration-journal.json"))?.State);

        Assert.Equal(LocalSecretResultStatus.Found, reopened.Set(fixture.OtherSource, "secret-marker").Status);
        Assert.Equal(LocalSecretResultStatus.Found,
            reopened.MigrateKey("App", "Development", null, fixture.OtherSource.StorageName, fixture.OtherDestination.Key).Status);
    }

    [Fact]
    public void MissingRetainedEvidenceDoesNotMakeTheActiveMarkerReusable()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        var retained = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
            AppSurfaceLocalSecretMigrationState.Prepared);
        Assert.Equal(LocalSecretResultStatus.Found, retained.Status);
        File.Delete(fixture.Path + ".migration-recovery.json");

        Assert.Equal(LocalSecretResultStatus.Found, fixture.Store.Set(fixture.OtherSource, "secret-marker").Status);
        var before = File.ReadAllBytes(fixture.Path);
        var blocked = fixture.Store.MigrateKey("App", "Development", null, fixture.OtherSource.StorageName,
            fixture.OtherDestination.Key);

        Assert.Equal("local-secret-migration-journal-conflict", blocked.Diagnostic?.Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Null(fixture.Store.Get(fixture.OtherDestination).Value);
    }

    [Fact]
    public void MalformedRetainedInventoryStopsMigrationWithoutChangingValues()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        Assert.Equal(LocalSecretResultStatus.Found,
            fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
                AppSurfaceLocalSecretMigrationState.Prepared).Status);
        File.WriteAllText(fixture.Path + ".migration-recovery.json", "{invalid json");
        Assert.Equal(LocalSecretResultStatus.Found, fixture.Store.Set(fixture.OtherSource, "secret-marker").Status);
        var before = File.ReadAllBytes(fixture.Path);

        var blocked = fixture.Store.MigrateKey("App", "Development", null, fixture.OtherSource.StorageName,
            fixture.OtherDestination.Key);

        Assert.NotEqual(LocalSecretResultStatus.Found, blocked.Status);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Null(fixture.Store.Get(fixture.OtherDestination).Value);
    }

    [Fact]
    public void OversizedRecoveryMetadataLeavesTheActiveJournalAndSecretsUnchanged()
    {
        using var fixture = new Fixture();
        var source = new string('L', 8 * 1024 * 1024);
        var failed = fixture.Store.MigrateKey("App", "Development", null, source, fixture.Destination.Key);
        Assert.Equal("local-secret-migration-source-missing", failed.Diagnostic?.Code);
        var before = File.ReadAllBytes(fixture.Path);

        var recovery = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
            AppSurfaceLocalSecretMigrationState.Prepared);

        Assert.Equal(LocalSecretResultStatus.Unavailable, recovery.Status);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".migration-recovery.json"));
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared,
            JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(fixture.Path + ".migration-journal.json"))?.State);
    }

    [Fact]
    public void MalformedSecretStoreReturnsValueSafeRecoveryFailure()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        File.WriteAllText(fixture.Path, "{\"Secret\":\"SENTINEL_VALUE\",");
        var journalBefore = File.ReadAllBytes(fixture.Path + ".migration-journal.json");

        var result = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, false, false);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-store-invalid", result.Diagnostic?.Code);
        Assert.DoesNotContain("SENTINEL_VALUE", result.Diagnostic!.ToDisplayString(), StringComparison.Ordinal);
        Assert.Equal(journalBefore, File.ReadAllBytes(fixture.Path + ".migration-journal.json"));
        Assert.False(File.Exists(fixture.Path + ".migration-recovery.json"));
    }

    [Fact]
    public void ReleaseRefusesAnOverlappingUnfinishedActiveJournal()
    {
        using var fixture = new Fixture();
        var failed = fixture.FailMissingSource();
        Assert.Equal(LocalSecretResultStatus.Found,
            fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, false,
                AppSurfaceLocalSecretMigrationState.Prepared).Status);
        var journalPath = fixture.Path + ".migration-journal.json";
        var active = JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(journalPath))!;
        File.WriteAllText(journalPath, JsonSerializer.Serialize(active with { State = AppSurfaceLocalSecretMigrationState.Prepared }));
        var retainedPath = fixture.Path + ".migration-recovery.json";
        var retainedBefore = File.ReadAllBytes(retainedPath);

        var refused = fixture.Store.RecoverKeyMigration("App", "Development", null, failed.MigrationId, true, true,
            AppSurfaceLocalSecretMigrationState.Prepared);

        Assert.Equal("local-secret-migration-recovery-active-overlap", refused.Diagnostic?.Code);
        Assert.Equal(retainedBefore, File.ReadAllBytes(retainedPath));
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared,
            JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(journalPath))?.State);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "local-recovery-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public string MissingSource => "appsurface:App:Development:Legacy.Key";
        public AppSurfaceLocalSecretIdentity Destination { get; } = Identity("Payments:ApiKey");
        public AppSurfaceLocalSecretIdentity OtherSource { get; } = Identity("Other:Old");
        public AppSurfaceLocalSecretIdentity OtherDestination { get; } = Identity("Other:New");
        public FileAppSurfaceLocalSecretStore Store { get; }

        public Fixture()
        {
            Path = System.IO.Path.Combine(_directory, "records.json");
            Assert.NotEqual(FileSecretPostureKind.Unsupported,
                DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(Path, "{}").Kind);
            Store = new FileAppSurfaceLocalSecretStore(Path);
        }

        public AppSurfaceLocalSecretKeyMigrationResult FailMissingSource()
        {
            var result = Store.MigrateKey("App", "Development", null, MissingSource, Destination.Key);
            Assert.Equal("local-secret-migration-source-missing", result.Diagnostic?.Code);
            Assert.Equal(AppSurfaceLocalSecretMigrationState.Unrecoverable, result.State);
            return result;
        }

        private static AppSurfaceLocalSecretIdentity Identity(string key) =>
            new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, key).Identity!;

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
