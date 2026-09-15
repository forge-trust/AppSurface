namespace AuthAspireKeycloakLocalSeedStore.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory($"appsurface-seed-store-tests-{Guid.NewGuid():N}").FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
