namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretJournalSafetyTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public void CorruptJournal_ShouldFailWithoutTreatingItAsNew(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "local-journal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "journal.json");
        try
        {
            DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(path, json);
            Assert.Throws<IOException>(() => new LocalSecretMigrationJournalFile(path).Read());
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Journal_ShouldPersistMetadataOnlyAndReopenAtEveryState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "local-journal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "journal.json");
        try
        {
            Assert.Null(new LocalSecretMigrationJournalFile(path).Read());
            foreach (var state in Enum.GetValues<AppSurfaceLocalSecretMigrationState>())
            {
                var record = new AppSurfaceLocalSecretMigrationJournal("id", "app", "env", null, "source", "destination", state);
                new LocalSecretMigrationJournalFile(path).Commit(record);
                Assert.Equal(record, new LocalSecretMigrationJournalFile(path).Read());
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void StateDirectory_ShouldRejectMissingOrRelativeUserProfile(string profile) =>
        Assert.Throws<IOException>(() => PlatformLocalSecretStatePaths.ForUserProfile(profile));

    [Fact]
    public void StateDirectory_ShouldUseCurrentUserProfileInsteadOfTempOrWorkingDirectory()
    {
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "isolated-user"));
        Assert.Equal(Path.Combine(profile, ".appsurface", "local-secrets-state"), PlatformLocalSecretStatePaths.ForUserProfile(profile));
        Assert.NotEqual(PlatformLocalSecretStatePaths.ForUserProfile(profile), PlatformLocalSecretStatePaths.ForUserProfile(profile + "2"));
    }

    [Fact]
    public void Lease_ShouldReturnStructuredFailureWithoutInvokingMutation()
    {
        var called = false;
        var result = PlatformLocalSecretMaintenanceLease.Run<int>(() => throw new IOException("unavailable"),
            () => { called = true; return 1; }, diagnostic =>
            {
                Assert.Equal("local-secret-maintenance-unavailable", diagnostic.Code);
                Assert.True(diagnostic.Retryable);
                return 2;
            });
        Assert.Equal(2, result);
        Assert.False(called);
    }

    [Fact]
    public void DefaultMigrationCapability_ShouldReturnApprovedDiagnosticWithoutAnyRead()
    {
        var store = new NoMigrationStore();
        var result = ((IAppSurfaceLocalSecretMigrationStore)store).MigrateKey("app", "Development", null,
            " exact legacy source ", AppSurfaceConfigKey.Parse("Payments:ApiKey"));
        Assert.Equal("local-secret-migration-unsupported", result.Diagnostic?.Code);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
    }

    private sealed class NoMigrationStore : IAppSurfaceLocalSecretMigrationStore
    {
        public AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix) =>
            throw new InvalidOperationException("The default capability must not call the namespace migration.");
    }
}
