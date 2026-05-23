using ForgeTrust.AppSurface.Docs.Models;
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
    public async Task Evaluate_ShouldCombineConfiguredPolicyWithVcsIgnoreEvaluator()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        var snapshot = CreateSnapshot(
            options =>
            {
                options.Harvest.Paths.ExcludeGlobs = ["blocked/**"];
                options.Harvest.Paths.VcsIgnore.AllowGlobs = ["generated/public/**"];
            });

        var configuredExclusion = snapshot.Evaluate("blocked/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var vcsIgnoreExclusion = snapshot.Evaluate("generated/private.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var restoredVcsIgnorePath = snapshot.Evaluate("generated/public/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(configuredExclusion.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude, configuredExclusion.Code);
        Assert.False(vcsIgnoreExclusion.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, vcsIgnoreExclusion.Code);
        Assert.True(restoredVcsIgnorePath.Included);
    }

    [Fact]
    public async Task ShouldIncludeFilePath_ShouldReturnEvaluateInclusion()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        var snapshot = CreateSnapshot();

        Assert.True(snapshot.ShouldIncludeFilePath("README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(snapshot.ShouldIncludeFilePath("generated/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_ShouldReturnConfiguredOrVcsIgnorePruning()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        var snapshot = CreateSnapshot(
            options => options.Harvest.Paths.ExcludeGlobs = ["blocked/**"]);

        Assert.True(snapshot.ShouldPruneDirectory("blocked", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(snapshot.ShouldPruneDirectory("generated", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(snapshot.ShouldPruneDirectory("docs", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task GetVcsIgnoreDiagnostics_ShouldExposeLoadedIgnoreFilesAndCounts()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        var snapshot = CreateSnapshot();

        _ = snapshot.Evaluate("generated/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var diagnostics = snapshot.GetVcsIgnoreDiagnostics();

        Assert.True(diagnostics.Enabled);
        Assert.Contains(".gitignore", diagnostics.IgnoreFileSamples);
        Assert.Equal(1, diagnostics.ExclusionCountsBySourceKind[AppSurfaceDocsHarvestSourceKind.Markdown]);
        var sample = Assert.Single(diagnostics.ExclusionSamples);
        Assert.Equal("generated/guide.md", sample.CandidatePath);
    }

    [Fact]
    public async Task CreateVcsIgnoreHealthDiagnostics_ShouldExposeSummaryDiagnostics()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        var snapshot = CreateSnapshot();

        _ = snapshot.Evaluate("generated/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var diagnostics = snapshot.CreateVcsIgnoreHealthDiagnostics();

        var summary = Assert.Single(diagnostics);
        Assert.Equal(DocHarvestDiagnosticCodes.VcsIgnoreSummary, summary.Code);
        Assert.Equal(DocHarvestDiagnosticSeverity.Information, summary.Severity);
        Assert.Contains("generated/guide.md", summary.Cause, StringComparison.Ordinal);
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

    [Fact]
    public async Task EnumerateCandidateFiles_ShouldRejectNullArguments()
    {
        var snapshot = CreateSnapshot();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ToArrayAsync(snapshot.EnumerateCandidateFiles(
                null!,
                AppSurfaceDocsHarvestSourceKind.Markdown,
                "*.md",
                CancellationToken.None)));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ToArrayAsync(snapshot.EnumerateCandidateFiles(
                _root,
                AppSurfaceDocsHarvestSourceKind.Markdown,
                null!,
                CancellationToken.None)));
    }

    [Fact]
    public async Task EnumerateCandidateFiles_ShouldObserveCancellationBeforeExpandingDirectory()
    {
        await File.WriteAllTextAsync(Path.Join(_root, "README.md"), "# docs");
        var snapshot = CreateSnapshot();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await ToArrayAsync(snapshot.EnumerateCandidateFiles(
                _root,
                AppSurfaceDocsHarvestSourceKind.Markdown,
                "*.md",
                cts.Token)));
    }

    [Fact]
    public async Task EnumerateCandidateFiles_ShouldPruneConfiguredAndVcsIgnoredDirectories()
    {
        await File.WriteAllTextAsync(Path.Join(_root, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Join(_root, "docs"));
        Directory.CreateDirectory(Path.Join(_root, "blocked"));
        Directory.CreateDirectory(Path.Join(_root, "generated"));
        await File.WriteAllTextAsync(Path.Join(_root, "docs", "Included.md"), "# docs");
        await File.WriteAllTextAsync(Path.Join(_root, "blocked", "Blocked.md"), "# blocked");
        await File.WriteAllTextAsync(Path.Join(_root, "generated", "Ignored.md"), "# ignored");
        var snapshot = CreateSnapshot(
            options => options.Harvest.Paths.ExcludeGlobs = ["blocked/**"]);

        var candidates = snapshot.EnumerateCandidateFiles(
            _root,
            AppSurfaceDocsHarvestSourceKind.Markdown,
            "*.md",
            CancellationToken.None).Select(path => Path.GetFileName(path)).ToArray();

        Assert.Contains("Included.md", candidates);
        Assert.DoesNotContain("Blocked.md", candidates);
        Assert.DoesNotContain("Ignored.md", candidates);
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

    private AppSurfaceDocsHarvestPathPolicySnapshot CreateSnapshot(Action<AppSurfaceDocsOptions>? configure = null)
    {
        var options = new AppSurfaceDocsOptions();
        configure?.Invoke(options);
        return new AppSurfaceDocsHarvestPathPolicySnapshotFactory(options, NullLogger.Instance).Create(_root);
    }

    private static Task<string[]> ToArrayAsync(IEnumerable<string> candidates)
    {
        return Task.FromResult(candidates.ToArray());
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
