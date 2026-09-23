using System.Diagnostics;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretFileMigrationPersistenceTests
{
    private const string Source = "appsurface:App:Development: legacy..key ";
    private static AppSurfaceLocalSecretIdentity Destination => new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, "Payments:ApiKey").Identity!;

    public static IEnumerable<object[]> Boundaries => Enumerable.Range(0, 17)
        .SelectMany(position => new[] { new object[] { position, false }, new object[] { position, true } });

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void AbruptProcessExit_ShouldReopenAndRecoverEveryProtocolBoundary(int boundary, bool after)
    {
        using var fixture = new Fixture();
        var startedPath = fixture.Path + ".started";
        using var process = LocalSecretTestProcess.Start($"crash:{boundary}:{after}", fixture.Path, startedPath);
        try
        {
            Assert.True(process.WaitForExit(30000),
                $"Crash child did not exit within 30 seconds (started: {File.Exists(startedPath)}).");
            Assert.Equal(91, process.ExitCode);
            var journalPath = fixture.Path + ".migration-journal.json";
            var journal = File.Exists(journalPath) ? JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(journalPath)) : null;
            var store = new FileAppSurfaceLocalSecretStore(fixture.Path);
            var result = store.MigrateKey("App", "Development", null, Source, Destination.Key);
            Assert.Equal(LocalSecretResultStatus.Found, result.Status);
            Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
            if (journal is not null) Assert.Equal(journal.MigrationId, result.MigrationId);
            Assert.Equal("retained-marker", store.Get(Destination).Value);
            using var data = JsonDocument.Parse(File.ReadAllText(fixture.Path));
            Assert.False(data.RootElement.TryGetProperty(Source, out _));
            Assert.DoesNotContain("retained-marker", File.ReadAllText(journalPath));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    internal static int CrashAtBoundary(string path, int boundary, bool after)
    {
        Action<int> crash = current => { if (current == boundary) Environment.Exit(91); };
        var backend = new FileAppSurfaceLocalSecretStore(path).CreateMigrationBackend(Destination);
        Run(new FaultBackend(backend, null, false, after ? null : crash, after ? crash : null));
        return 4; // The selected boundary must terminate before this point, bypassing normal lease disposal.
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Reopen_ShouldRecoverRealFilesAtEveryBeforeAndAfterBoundary(int boundary, bool after)
    {
        using var fixture = new Fixture();
        var fault = new FaultBackend(fixture.Store.CreateMigrationBackend(Destination), boundary, after);
        var first = Run(fault);
        Assert.Equal(LocalSecretResultStatus.Unavailable, first.Status);
        var journalPath = fixture.Path + ".migration-journal.json";
        var persisted = File.Exists(journalPath) ? JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(File.ReadAllText(journalPath)) : null;
        var reopened = new FileAppSurfaceLocalSecretStore(fixture.Path);

        var resumed = reopened.MigrateKey("App", "Development", null, Source, Destination.Key);

        Assert.Equal(LocalSecretResultStatus.Found, resumed.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, resumed.State);
        if (persisted is not null) Assert.Equal(persisted.MigrationId, resumed.MigrationId);
        Assert.Equal("retained-marker", reopened.Get(Destination).Value);
        using var data = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.False(data.RootElement.TryGetProperty(Source, out _));
        Assert.DoesNotContain("retained-marker", File.ReadAllText(journalPath));
        Assert.DoesNotContain("retained-marker", first.ToString());
    }

    [Theory]
    [InlineData(1, false)] // Prepared
    [InlineData(1, true)]
    [InlineData(5, false)] // DestinationWritten
    [InlineData(5, true)]
    [InlineData(8, false)] // DestinationVerified
    [InlineData(8, true)]
    [InlineData(10, false)] // SourceDeletePending
    [InlineData(10, true)]
    [InlineData(16, false)] // Complete
    [InlineData(16, true)]
    public void IndependentProcessWriter_ShouldWaitAtEveryDurableJournalBoundary(int boundary, bool after)
    {
        using var fixture = new Fixture();
        Process? writer = null;
        var startedPath = fixture.Path + ".started";
        Action<int> startWriter = position =>
        {
            if (position != boundary) return;
            writer = LocalSecretTestProcess.Start("write", fixture.Path, startedPath);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(startedPath), TimeSpan.FromSeconds(10)));
            Assert.False(writer.WaitForExit(100));
        };
        var fault = new FaultBackend(fixture.Store.CreateMigrationBackend(Destination), null, false,
            after ? null : startWriter, after ? startWriter : null);
        try
        {
            Assert.Equal(LocalSecretResultStatus.Found, Run(fault).Status);
            Assert.NotNull(writer);
            Assert.True(writer.WaitForExit(10000));
            Assert.Equal(0, writer.ExitCode);
            var other = new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, "Independent:Writer").Identity!;
            var reopened = new FileAppSurfaceLocalSecretStore(fixture.Path);
            Assert.Equal("writer-marker", reopened.Get(other).Value);
            Assert.Equal("retained-marker", reopened.Get(Destination).Value);
        }
        finally
        {
            if (writer is { HasExited: false }) writer.Kill(entireProcessTree: true);
            writer?.Dispose();
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void ReopenedWriterChangedDestination_ShouldRetainExactSource(int boundary)
    {
        using var fixture = new Fixture();
        var fault = new FaultBackend(fixture.Store.CreateMigrationBackend(Destination), boundary, true);
        Assert.Equal(LocalSecretResultStatus.Unavailable, Run(fault).Status);
        Assert.Equal(LocalSecretResultStatus.Found, new FileAppSurfaceLocalSecretStore(fixture.Path).Set(Destination, "intervening-writer-marker").Status);
        var result = new FileAppSurfaceLocalSecretStore(fixture.Path).MigrateKey("App", "Development", null, Source, Destination.Key);
        Assert.Equal("local-secret-migration-source-destination-changed", result.Diagnostic?.Code);
        using var data = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.True(data.RootElement.TryGetProperty(Source, out _));
        Assert.Equal("intervening-writer-marker", new FileAppSurfaceLocalSecretStore(fixture.Path).Get(Destination).Value);
    }

    [Fact]
    public void UnsupportedFileSystem_ShouldStopBeforePreparedAndBeforeAnyIo()
    {
        var files = new UnsupportedFiles();
        var store = new FileAppSurfaceLocalSecretStore("unused-test-store.json", files);
        var result = store.MigrateKey("App", "Development", null, Source, Destination.Key);
        Assert.Equal("local-secret-migration-unsupported", result.Diagnostic?.Code);
        Assert.Equal(0, files.Operations);
    }

    [Fact]
    public void ExistingCaseVariantDestination_ShouldStopBeforeWritingOrDeletingEitherRecord()
    {
        using var fixture = new Fixture();
        var variant = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("App", "Development", null, "payments:apikey").Identity!;
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(fixture.Path))!;
        data[variant.StorageName] = new
        {
            ApplicationName = "App",
            Environment = "Development",
            KeyPrefix = (string?)null,
            Key = variant.Key.Value,
            Value = "existing-marker"
        };
        Assert.NotEqual(FileSecretPostureKind.Unsupported,
            DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance
                .WriteAllTextWithPosture(fixture.Path, JsonSerializer.Serialize(data)).Kind);

        var result = fixture.Store.MigrateKey("App", "Development", null, Source, Destination.Key);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        using var after = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.True(after.RootElement.TryGetProperty(Source, out _));
        Assert.True(after.RootElement.TryGetProperty(variant.StorageName, out _));
        Assert.False(after.RootElement.TryGetProperty(Destination.StorageName, out _));
    }

    [Fact]
    public void CaseOnlySourceRename_ShouldWriteDesiredSpellingAndDeleteOnlyTheSource()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "local-case-rename-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "records.json");
        const string source = "appsurface:App:Development:payments:apikey";
        var data = new Dictionary<string, object>
        {
            [source] = new
            {
                ApplicationName = "App",
                Environment = "Development",
                KeyPrefix = (string?)null,
                Key = "payments:apikey",
                Value = "case-rename-marker"
            }
        };

        try
        {
            Assert.NotEqual(FileSecretPostureKind.Unsupported,
                DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance
                    .WriteAllTextWithPosture(path, JsonSerializer.Serialize(data)).Kind);
            var store = new FileAppSurfaceLocalSecretStore(path);

            var result = store.MigrateKey("App", "Development", null, source, Destination.Key);

            Assert.Equal(LocalSecretResultStatus.Found, result.Status);
            Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
            Assert.Equal("case-rename-marker", store.Get(Destination).Value);
            using var after = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(after.RootElement.TryGetProperty(source, out _));
            Assert.True(after.RootElement.TryGetProperty(Destination.StorageName, out _));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CollisionRetry_ShouldReusePreparedJournalAfterConflictIsRemoved()
    {
        using var fixture = new Fixture();
        var variant = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("App", "Development", null, "payments:apikey").Identity!;
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(fixture.Path))!;
        data[variant.StorageName] = new
        {
            ApplicationName = "App",
            Environment = "Development",
            KeyPrefix = (string?)null,
            Key = variant.Key.Value,
            Value = "existing-marker"
        };
        Assert.NotEqual(FileSecretPostureKind.Unsupported,
            DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance
                .WriteAllTextWithPosture(fixture.Path, JsonSerializer.Serialize(data)).Kind);

        var first = fixture.Store.MigrateKey("App", "Development", null, Source, Destination.Key);
        Assert.Equal("config-key-collision", first.Diagnostic?.Code);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, first.State);

        data.Remove(variant.StorageName);
        Assert.NotEqual(FileSecretPostureKind.Unsupported,
            DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance
                .WriteAllTextWithPosture(fixture.Path, JsonSerializer.Serialize(data)).Kind);

        var retry = new FileAppSurfaceLocalSecretStore(fixture.Path)
            .MigrateKey("App", "Development", null, Source, Destination.Key);

        Assert.Equal(LocalSecretResultStatus.Found, retry.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, retry.State);
        Assert.Equal("retained-marker", new FileAppSurfaceLocalSecretStore(fixture.Path).Get(Destination).Value);
    }

    private static AppSurfaceLocalSecretKeyMigrationResult Run(IAppSurfaceLocalSecretMigrationBackend backend) =>
        AppSurfaceLocalSecretMigrationCoordinator.Run(backend, "App", "Development", null, Source, Destination.StorageName, Destination.Key, "file");

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "local-persist-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public FileAppSurfaceLocalSecretStore Store { get; }
        public Fixture()
        {
            Path = System.IO.Path.Combine(_directory, "records.json");
            var data = new Dictionary<string, object> { [Source] = new { ApplicationName = "App", Environment = "Development", KeyPrefix = (string?)null, Key = " legacy..key ", Value = "retained-marker" } };
            Assert.NotEqual(FileSecretPostureKind.Unsupported, DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(Path, JsonSerializer.Serialize(data)).Kind);
            Store = new FileAppSurfaceLocalSecretStore(Path);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }

    internal sealed class FaultBackend(IAppSurfaceLocalSecretMigrationBackend inner, int? failAt, bool after, Action<int>? before = null, Action<int>? afterEffect = null) : IAppSurfaceLocalSecretMigrationBackend
    {
        private int _position;
        public bool SupportsDurableMigration => inner.SupportsDurableMigration;
        public IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken) => inner.AcquireMaintenanceLease(timeout, cancellationToken);
        public AppSurfaceLocalSecretMigrationJournal? ReadJournal() => At(inner.ReadJournal);
        public void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal) => At(() => { inner.CommitJournal(journal); return true; });
        public string? ReadExact(string key) => At(() => inner.ReadExact(key));
        public void WriteExact(string key, string value) => At(() => { inner.WriteExact(key, value); return true; });
        public void DeleteExact(string key) => At(() => { inner.DeleteExact(key); return true; });
        public void PublishIndex() => At(() => { inner.PublishIndex(); return true; });
        private T At<T>(Func<T> operation)
        {
            var current = _position++;
            before?.Invoke(current);
            if (failAt == current && !after) throw new IOException("before disk effect");
            var result = operation();
            afterEffect?.Invoke(current);
            if (failAt == current) throw new IOException("after disk effect");
            return result;
        }
    }

    private sealed class UnsupportedFiles : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        public int Operations { get; private set; }
        private T Fail<T>() { Operations++; throw new InvalidOperationException("Capability gate must precede I/O."); }
        public bool FileExists(string path) => Fail<bool>();
        public string ReadAllText(string path) => Fail<string>();
        public Stream OpenRead(string path) => Fail<Stream>();
        public FileSecretPostureResult InspectReadPath(string path) => Fail<FileSecretPostureResult>();
        public FileSecretPostureResult InspectExistingFilePosture(string path) => Fail<FileSecretPostureResult>();
        public FileSecretPostureResult PrepareWrite(string path) => Fail<FileSecretPostureResult>();
        public FileSecretPostureResult WriteAllTextWithPosture(string path, string contents) => Fail<FileSecretPostureResult>();
        public FileSecretPostureResult Doctor(string path) => Fail<FileSecretPostureResult>();
    }
}
