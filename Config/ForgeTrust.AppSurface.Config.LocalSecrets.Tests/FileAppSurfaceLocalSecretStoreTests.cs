namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class FileAppSurfaceLocalSecretStoreTests
{
    [Fact]
    public void SetGetListDelete_Should_WorkWithoutPrintingSecretInResults()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Combine(temp.Path, "secrets.json"));
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

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"appsurface-local-secrets-{Guid.NewGuid():N}");
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
}
