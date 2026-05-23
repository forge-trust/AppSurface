using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestPathPolicySnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("appsurface-path-policy-").FullName;
    private readonly string _externalRoot = Directory.CreateTempSubdirectory("appsurface-path-policy-target-").FullName;

    [Fact]
    public void Constructor_ShouldRejectNullPolicies()
    {
        var configuredPolicy = CreateConfiguredPolicy(new AppSurfaceDocsOptions());
        var vcsIgnorePolicy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestPathPolicySnapshot(null!, vcsIgnorePolicy));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestPathPolicySnapshot(configuredPolicy, null!));
    }

    [Fact]
    public void FactoryConstructor_ShouldRejectNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestPathPolicySnapshotFactory(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestPathPolicySnapshotFactory(new AppSurfaceDocsOptions(), null!));
    }

    [Fact]
    public void FactoryCreate_WhenHarvestPathsIsNullUsesDefaultVcsIgnoreOptions()
    {
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Paths = null!
            }
        };
        var factory = new AppSurfaceDocsHarvestPathPolicySnapshotFactory(options, NullLogger.Instance);

        var snapshot = factory.Create(_root);

        Assert.True(snapshot.GetVcsIgnoreDiagnostics().Enabled);
    }

    [Fact]
    public async Task EnumerateCandidateFiles_WhenDirectoryIsReparsePointSkipsTraversal()
    {
        var externalFile = Path.Join(_externalRoot, "External.md");
        await File.WriteAllTextAsync(externalFile, "# external");
        var linkPath = Path.Join(_root, "linked");

        try
        {
            Directory.CreateSymbolicLink(linkPath, _externalRoot);
        }
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var snapshot = new AppSurfaceDocsHarvestPathPolicySnapshotFactory(
            new AppSurfaceDocsOptions(),
            NullLogger.Instance).Create(_root);

        var candidates = snapshot.EnumerateCandidateFiles(
            _root,
            AppSurfaceDocsHarvestSourceKind.Markdown,
            "*.md",
            CancellationToken.None).ToArray();

        Assert.DoesNotContain(candidates, path => path.EndsWith("External.md", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        DeleteTempDirectory(_root);
        DeleteTempDirectory(_externalRoot);
    }

    private static AppSurfaceDocsHarvestPathPolicy CreateConfiguredPolicy(AppSurfaceDocsOptions options)
    {
        return new AppSurfaceDocsHarvestPathPolicy(
            options,
            NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Best effort cleanup for temp fixture directories.
        }
        catch (IOException)
        {
            // Best effort cleanup for temp fixture directories.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup for temp fixture directories.
        }
    }
}
