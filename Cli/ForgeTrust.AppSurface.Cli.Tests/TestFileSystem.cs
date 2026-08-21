using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Creates bounded temporary filesystem fixtures for cleanup-command tests.</summary>
internal sealed class TestDirectory(string path) : IDisposable
{
    /// <summary>Gets the physical temporary-directory path.</summary>
    public string Path { get; } = path;

    /// <summary>Creates an empty temporary directory for one cleanup-command test.</summary>
    public static TestDirectory Create()
    {
        var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    /// <summary>Creates and returns a directory below the temporary root.</summary>
    public string CreateDirectory(string relativePath)
    {
        var path = TestPathUtils.PathUnder(Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes and returns a file below the temporary root.</summary>
    public string WriteFile(string relativePath, string contents)
    {
        var path = TestPathUtils.PathUnder(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>Removes the temporary root after the test completes.</summary>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>Creates symbolic-link fixtures or reports when the current test environment lacks link support.</summary>
internal static class TestFileSystem
{
    /// <summary>Creates a directory link or explicitly skips the test when the filesystem does not support it.</summary>
    public static void CreateDirectoryLinkOrSkip(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Symbolic link creation is not available in this environment ({exception.GetType().Name}).");
        }
    }

    /// <summary>Creates a file link or explicitly skips the test when the filesystem does not support it.</summary>
    public static void CreateFileLinkOrSkip(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Symbolic link creation is not available in this environment ({exception.GetType().Name}).");
        }
    }
}
