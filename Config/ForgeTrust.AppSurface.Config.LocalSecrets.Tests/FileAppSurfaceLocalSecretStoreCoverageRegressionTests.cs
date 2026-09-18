using System.Text;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

[Collection(FileAppSurfaceLocalSecretStoreCollection.Name)]
public sealed class FileAppSurfaceLocalSecretStoreCoverageRegressionTests
{
    [Fact]
    public void MigrateKey_InvalidNamespaceDestination_ShouldPreserveSourceFile()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var source = Normalize("App", "Development", "Legacy.Key");
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(source, "retained-marker").Status);
        var before = File.ReadAllBytes(path);

        var result = store.MigrateKey(
            "bad:app",
            "Development",
            null,
            source.StorageName,
            AppSurfaceConfigKey.Parse("Moved:Key"));

        Assert.Equal(LocalSecretResultStatus.InvalidIdentity, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Equal("local-secret-applicationName-invalid-character", result.Diagnostic?.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("retained-marker", store.Get(source).Value);
    }

    [Fact]
    public void MigrateKey_IOExceptionFromDurableBackend_ShouldReturnUnavailable()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            Path.Join(Path.GetTempPath(), "coverage-io-failure", "secrets.json"),
            new MigrationPreparationFailureFileSystem(() => new IOException("sentinel-io")));

        var result = store.MigrateKey(
            "App",
            "Development",
            null,
            "appsurface:App:Development:Legacy.Key",
            AppSurfaceConfigKey.Parse("Moved:Key"));

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("unavailable", result.MigrationId);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("sentinel-io", result.ToString());
    }

    [Fact]
    public void MigrateKey_CancellationFromDurableBackend_ShouldRemainCancellation()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            Path.Join(Path.GetTempPath(), "coverage-cancellation", "secrets.json"),
            new MigrationPreparationFailureFileSystem(() => new OperationCanceledException("sentinel-cancellation")));

        var exception = Assert.Throws<OperationCanceledException>(() => store.MigrateKey(
            "App",
            "Development",
            null,
            "appsurface:App:Development:Legacy.Key",
            AppSurfaceConfigKey.Parse("Moved:Key")));

        Assert.Equal("sentinel-cancellation", exception.Message);
    }

    [Fact]
    public void CaseAliasCollision_ShouldRejectMutationsAndDiagnosticsWithoutChangingFile()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var original = Normalize("App", "Development", "Payments:ApiKey");
        var variant = Normalize("App", "Development", "payments:apikey");
        File.WriteAllText(path, $$"""
            {
              "{{original.StorageName}}": {
                "ApplicationName": "App",
                "Environment": "Development",
                "KeyPrefix": null,
                "Key": "Payments:ApiKey",
                "Value": "original-marker"
              },
              "{{variant.StorageName}}": {
                "ApplicationName": "App",
                "Environment": "Development",
                "KeyPrefix": null,
                "Key": "payments:apikey",
                "Value": "variant-marker"
              }
            }
            """);
        if (!OperatingSystem.IsWindows())
        {
            new FileInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        AssertCollisionWithoutMutation(
            () => store.Set(variant, "attempted-change-marker"),
            path,
            File.ReadAllBytes(path),
            expectedStatus: LocalSecretResultStatus.ProviderFailed);
        AssertCollisionWithoutMutation(
            () => store.Delete(original),
            path,
            File.ReadAllBytes(path),
            expectedStatus: LocalSecretResultStatus.ProviderFailed);

        var beforeList = File.ReadAllBytes(path);
        var list = store.List("App", "Development", null);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, list.Status);
        Assert.Equal("config-key-collision", list.Diagnostic?.Code);
        Assert.Equal(beforeList, File.ReadAllBytes(path));

        var beforeDoctor = File.ReadAllBytes(path);
        var doctor = store.Doctor("App", "Development", null);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, doctor.Status);
        Assert.Equal("config-key-collision", doctor.Diagnostic?.Code);
        Assert.Equal(beforeDoctor, File.ReadAllBytes(path));
        Assert.Equal("config-key-collision", store.Get(original).Diagnostic?.Code);
        Assert.Equal("config-key-collision", store.Get(variant).Diagnostic?.Code);
    }

    private static void AssertCollisionWithoutMutation(
        Func<AppSurfaceLocalSecretResult> operation,
        string path,
        byte[] before,
        LocalSecretResultStatus expectedStatus)
    {
        var result = operation();
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static AppSurfaceLocalSecretIdentity Normalize(string app, string environment, string key) =>
        new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, environment, null, key).Identity!;

    private sealed class MigrationPreparationFailureFileSystem(Func<Exception> exceptionFactory)
        : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        public bool SupportsDurableMigration => true;

        public bool FileExists(string path) => false;

        public string ReadAllText(string path) => "{}";

        public Stream OpenRead(string path) => new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        public FileSecretPostureResult InspectReadPath(string path) => FileSecretPostureResult.Ready();

        public FileSecretPostureResult InspectExistingFilePosture(string path) => FileSecretPostureResult.Ready();

        public FileSecretPostureResult PrepareWrite(string path) => throw exceptionFactory();

        public FileSecretPostureResult WriteAllTextWithPosture(string path, string contents) => FileSecretPostureResult.Ready();

        public FileSecretPostureResult Doctor(string path) => FileSecretPostureResult.Ready();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-coverage-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                new DirectoryInfo(path).UnixFileMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }

            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
