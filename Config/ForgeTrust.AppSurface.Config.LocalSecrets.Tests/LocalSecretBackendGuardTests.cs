using System.Text.Json;
using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretBackendGuardTests
{
    [Theory]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("index-read", false)]
    [InlineData("index-read", true)]
    [InlineData("index-write", false)]
    [InlineData("index-write", true)]
    public void PlatformBackend_ShouldRejectUnconfirmedNativeResult(string operation, bool diagnostic)
    {
        using var fixture = new Fixture();
        var owner = new ResultStore(fixture.Directory, operation, diagnostic);
        var backend = new PlatformLocalSecretMigrationBackend(owner, fixture.Source, fixture.Destination);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        backend.CommitJournal(fixture.Journal);
        Assert.Throws<IOException>(() =>
        {
            switch (operation)
            {
                case "read": backend.ReadExact(fixture.Source.StorageName); break;
                case "write": backend.WriteExact(fixture.Destination.StorageName, "marker"); break;
                case "delete": backend.DeleteExact(fixture.Source.StorageName); break;
                default: backend.PublishIndex(); break;
            }
        });
    }

    [Fact]
    public void PlatformBackend_ShouldRejectFoundWithoutValueAndMissingJournal()
    {
        using var fixture = new Fixture();
        var owner = new ResultStore(fixture.Directory, "null-value", false);
        var backend = new PlatformLocalSecretMigrationBackend(owner, fixture.Source, fixture.Destination);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Throws<IOException>(() => backend.ReadExact(fixture.Source.StorageName));
        Assert.Throws<IOException>(backend.PublishIndex);
    }

    [Fact]
    public void PlatformBackend_ShouldAcceptConfirmedMissingDelete()
    {
        using var fixture = new Fixture();
        var backend = new PlatformLocalSecretMigrationBackend(new ResultStore(fixture.Directory, "delete-missing", false), fixture.Source, fixture.Destination);
        using var lease = backend.AcquireMaintenanceLease(TimeSpan.FromSeconds(1), CancellationToken.None);
        backend.DeleteExact(fixture.Source.StorageName);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("journal-write")]
    [InlineData("journal-read")]
    [InlineData("data-read")]
    [InlineData("data-write")]
    [InlineData("data-delete")]
    public void FileBackend_ShouldRejectUnsafePostureAtEveryIoBoundary(string operation)
    {
        using var fixture = new Fixture();
        var files = new GuardFiles(operation);
        var store = new FileAppSurfaceLocalSecretStore(fixture.Path, files);
        var backend = store.CreateMigrationBackend(fixture.Destination);
        Assert.Throws<IOException>(() =>
        {
            switch (operation)
            {
                case "prepare": using (backend.AcquireMaintenanceLease(TimeSpan.Zero, CancellationToken.None)) { } break;
                case "journal-write": backend.CommitJournal(fixture.Journal); break;
                case "journal-read": backend.ReadJournal(); break;
                case "data-read": backend.ReadExact(fixture.Source.StorageName); break;
                case "data-write": backend.WriteExact(fixture.Destination.StorageName, "marker"); break;
                case "data-delete": backend.DeleteExact(fixture.Source.StorageName); break;
            }
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public void FileBackend_ShouldRejectInvalidPersistedJournal(string contents)
    {
        using var fixture = new Fixture();
        DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(fixture.Path + ".migration-journal.json", contents);
        Assert.Throws<IOException>(() => new FileAppSurfaceLocalSecretStore(fixture.Path).CreateMigrationBackend(fixture.Destination).ReadJournal());
    }

    [Theory]
    [InlineData("Other", "Development", null)]
    [InlineData("App", "Other", null)]
    [InlineData("App", "Development", "Other")]
    public void FileBackend_ShouldNeverReadOrDeleteOutsideRequestedNamespace(string app, string environment, string? prefix)
    {
        using var fixture = new Fixture();
        fixture.Seed(app, environment, prefix);
        var backend = new FileAppSurfaceLocalSecretStore(fixture.Path).CreateMigrationBackend(fixture.Destination);
        Assert.Null(backend.ReadExact(fixture.Source.StorageName));
        backend.DeleteExact(fixture.Source.StorageName);
        backend.DeleteExact("absent");
        Assert.Contains(fixture.Source.StorageName, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void Journal_ShouldRejectSymlinkReadAndWriteWithoutChangingTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var link = System.IO.Path.Combine(fixture.Directory, "journal-link");
        File.CreateSymbolicLink(link, fixture.Path);
        var original = File.ReadAllText(fixture.Path);
        var journal = new LocalSecretMigrationJournalFile(link);
        Assert.Throws<IOException>(() => journal.Read());
        Assert.Throws<IOException>(() => journal.Commit(fixture.Journal));
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    private sealed class ResultStore(string directory, string operation, bool diagnostic) : IndexedLocalSecretStore
    {
        public override string Name => "NativeResultFixture";
        internal override string MigrationStateDirectory => directory;
        private AppSurfaceLocalSecretResult Failed => new(LocalSecretResultStatus.Unavailable, null,
            diagnostic ? new AppSurfaceLocalSecretDiagnostic("native-failure", "Unavailable", "Fixture failure", "Retry") : null, Name);
        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (operation == "read" || (operation == "index-read" && identity.StoredKey == "__appsurface_index__")) return Failed;
            if (operation == "null-value") return new(LocalSecretResultStatus.Found, null, null, Name);
            return identity.StoredKey == "__appsurface_index__" ? AppSurfaceLocalSecretResult.Found("[]", Name) : AppSurfaceLocalSecretResult.Missing(Name);
        }
        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value) =>
            operation is "write" or "index-write" ? Failed : AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) =>
            operation == "delete" ? Failed : AppSurfaceLocalSecretResult.Missing(Name);
        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretResult.Missing(Name);
    }

    private sealed class GuardFiles(string operation) : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        private static readonly DefaultFileAppSurfaceLocalSecretStoreFileSystem Files = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance;
        private static FileSecretPostureResult Unsafe => FileSecretPostureResult.Unsupported("unsafe", "Unsafe path", "Fixture", "Use safe path");
        public bool SupportsDurableMigration => true;
        public bool FileExists(string path) => Files.FileExists(path);
        public string ReadAllText(string path) => Files.ReadAllText(path);
        public Stream OpenRead(string path) => Files.OpenRead(path);
        public FileSecretPostureResult InspectReadPath(string path) => Files.InspectReadPath(path);
        public FileSecretPostureResult InspectExistingFilePosture(string path) => operation is "journal-read" or "data-read" ? Unsafe : Files.InspectExistingFilePosture(path);
        public FileSecretPostureResult PrepareWrite(string path) => operation == "prepare" ? Unsafe : Files.PrepareWrite(path);
        public FileSecretPostureResult WriteAllTextWithPosture(string path, string contents) => operation is "journal-write" or "data-write" or "data-delete" ? Unsafe : Files.WriteAllTextWithPosture(path, contents);
        public FileSecretPostureResult Doctor(string path) => Files.Doctor(path);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "local-guards-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "records.json");
        public AppSurfaceLocalSecretIdentity Source { get; } = new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, "Source").Identity!;
        public AppSurfaceLocalSecretIdentity Destination { get; } = new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, "Destination").Identity!;
        public AppSurfaceLocalSecretMigrationJournal Journal => new("id", "App", "Development", null, Source.StorageName, Destination.StorageName, AppSurfaceLocalSecretMigrationState.DestinationVerified);
        public Fixture() => Seed("App", "Development", null);
        public void Seed(string app, string environment, string? prefix) => DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(Path,
            JsonSerializer.Serialize(new Dictionary<string, object> { [Source.StorageName] = new { ApplicationName = app, Environment = environment, KeyPrefix = prefix, Key = "Source", Value = "marker" } }));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
