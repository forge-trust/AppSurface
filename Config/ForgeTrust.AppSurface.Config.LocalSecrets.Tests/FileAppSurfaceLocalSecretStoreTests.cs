namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class FileAppSurfaceLocalSecretStoreTests
{
    [Fact]
    public void SetGetListDelete_Should_WorkWithoutPrintingSecretInResults()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var set = store.Set(identity, "sk_test_secret");
        var get = store.Get(identity);
        var list = store.List("MyApp", "Development", null);
        var delete = store.Delete(identity);
        var missing = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Found, set.Status);
        Assert.Equal("sk_test_secret", get.Value);
        Assert.Contains("Stripe:ApiKey", list.Keys);
        Assert.Equal(LocalSecretResultStatus.Found, delete.Status);
        Assert.Equal(LocalSecretResultStatus.Missing, missing.Status);
        Assert.DoesNotContain("sk_test_secret", set.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", delete.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_ReturnMissing_WhenKeyDoesNotExist()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
    }

    [Fact]
    public void Doctor_Should_CreateFileAndReturnReadyDiagnostic()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "nested", "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal("local-secret-store-ready", result.Diagnostic?.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetDefaultPath_Should_ReturnAppSurfaceLocalSecretsPath()
    {
        var path = FileAppSurfaceLocalSecretStore.GetDefaultPath();

        Assert.EndsWith(Path.Join("AppSurface", "local-secrets.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Should_FilterByPrefixAndReturnEmptyForNullJsonDocument()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", "Payments", "Stripe:ApiKey").Identity!, "stripe");
        store.Set(normalizer.Normalize("MyApp", "Development", null, "SendGrid:ApiKey").Identity!, "sendgrid");

        var prefixed = store.List("MyApp", "Development", "Payments");
        var unprefixed = store.List("MyApp", "Development", null);
        File.WriteAllText(path, "null");
        var empty = store.List("MyApp", "Development", null);

        Assert.Equal(["Stripe:ApiKey"], prefixed.Keys);
        Assert.Equal(["SendGrid:ApiKey"], unprefixed.Keys);
        Assert.Empty(empty.Keys);
    }

    [Fact]
    public void ReadOperations_Should_ReturnPasteSafeDiagnostic_WhenFileContainsInvalidJson()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        File.WriteAllText(path, "{not-json: raw-secret}");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var get = store.Get(identity);
        var list = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, get.Status);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, list.Status);
        Assert.Equal("local-secret-store-invalid", get.Diagnostic?.Code);
        Assert.Equal("local-secret-store-invalid", list.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", get.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", list.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_ReturnPasteSafeDiagnostic_WhenStorePathIsDirectory()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(temp.Path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.True(result.Status is LocalSecretResultStatus.Locked or LocalSecretResultStatus.Unavailable);
        Assert.StartsWith("local-secret-store-", result.Diagnostic?.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnLockedDiagnostic_WhenReadIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void List_Should_ReturnUnavailableDiagnostic_WhenReadFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new IOException()));

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Fact]
    public void Set_Should_ReturnLockedDiagnostic_WhenReadIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_ReturnUnavailableDiagnostic_WhenReadFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new IOException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Fact]
    public void Delete_Should_ReturnUnavailableDiagnostic_WhenWriteFailsWithIoException()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var entry = """
            {
              "appsurface:MyApp:Development:Stripe:ApiKey": {
                "ApplicationName": "MyApp",
                "Environment": "Development",
                "KeyPrefix": null,
                "Key": "Stripe:ApiKey",
                "Value": "sk_test_secret"
              }
            }
            """;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => entry, write: _ => throw new IOException()));

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
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
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"appsurface-local-secrets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ThrowingFileSystem(
        Func<string>? read = null,
        Action<string>? write = null) : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        public bool FileExists(string path) => true;

        public string ReadAllText(string path) => read?.Invoke() ?? "{}";

        public void WriteAllText(string path, string contents) => (write ?? (_ => { }))(contents);

        public void CreateDirectory(string path)
        {
        }

        public Stream OpenOrCreate(string path) => new MemoryStream();
    }
}
