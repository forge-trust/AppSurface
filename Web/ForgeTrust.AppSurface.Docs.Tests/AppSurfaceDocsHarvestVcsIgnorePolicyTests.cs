using System.ComponentModel;
using System.Diagnostics;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestVcsIgnorePolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("appsurface-vcs-ignore-").FullName;

    [Fact]
    public async Task Evaluate_WhenRootIgnoreExcludesVendorDirectoryExcludesCandidateWithTrace()
    {
        await WriteAsync(".gitignore", "bower_components/\n");
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("bower_components/jquery/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, decision.Code);
        var trace = Assert.Single(decision.Trace, trace => trace.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore);
        Assert.Equal(".gitignore", trace.SourcePath);
        Assert.Equal(1, trace.LineNumber);
        Assert.Equal("bower_components/", trace.Pattern);
    }

    [Fact]
    public async Task Evaluate_WhenVcsIgnoreAllowGlobMatchesRestoresOnlyVcsIgnoreExclusion()
    {
        await WriteAsync(".gitignore", "generated/\n");
        var snapshot = CreateSnapshot(
            options =>
            {
                options.Harvest.Paths.VcsIgnore.AllowGlobs = ["generated/public/**"];
                options.Harvest.Paths.ExcludeGlobs = ["generated/public/private.md"];
            });

        var restored = snapshot.Evaluate("generated/public/index.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var excluded = snapshot.Evaluate("generated/public/private.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(restored.Included);
        Assert.False(excluded.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude, excluded.Code);
    }

    [Fact]
    public async Task Evaluate_WhenVcsIgnoreIsDisabledDoesNotExcludeOrPrune()
    {
        await WriteAsync(".gitignore", "bower_components/\n");
        var snapshot = CreateSnapshot(options => options.Harvest.Paths.VcsIgnore.Enabled = false);

        var decision = snapshot.Evaluate("bower_components/jquery/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(decision.Included);
        Assert.False(snapshot.ShouldPruneDirectory("bower_components", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(snapshot.GetVcsIgnoreDiagnostics().Enabled);
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenDirectoryIsRootDoesNotPrune()
    {
        await WriteAsync(".gitignore", "*\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.False(policy.ShouldPruneDirectory("/", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenIgnoredDirectoryHasNoReachableNegationPrunesSubtree()
    {
        await WriteAsync(".gitignore", "bower_components/\n");
        var snapshot = CreateSnapshot();

        Assert.True(snapshot.ShouldPruneDirectory("bower_components", AppSurfaceDocsHarvestSourceKind.JavaScript));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenAllowGlobCanRestoreDescendantKeepsDirectoryEnumerable()
    {
        await WriteAsync(".gitignore", "generated/\n");
        var snapshot = CreateSnapshot(options => options.Harvest.Paths.VcsIgnore.AllowGlobs = ["generated/public/**"]);

        Assert.False(snapshot.ShouldPruneDirectory("generated", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenIgnoredDirectoryHasUnrelatedSlashlessNegationPrunesSubtree()
    {
        await WriteAsync(
            ".gitignore",
            """
            bower_components/
            !README.md
            """);
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("bower_components/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(snapshot.ShouldPruneDirectory("bower_components", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(decision.Included);
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenEarlierNegationMatchesIgnoredDirectoryKeepsDirectoryEnumerable()
    {
        await WriteAsync(
            ".gitignore",
            """
            !generated/
            generated/
            """);
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.ShouldPruneDirectory("generated", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenDoubleStarSubtreeIgnoreHasNoNegationPrunesSubtree()
    {
        await WriteAsync(".gitignore", "/dist/**\n");
        var snapshot = CreateSnapshot();

        Assert.True(snapshot.ShouldPruneDirectory("dist", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenDoubleStarSubtreeIgnoreHasReachableNegationKeepsDirectoryEnumerable()
    {
        await WriteAsync(
            ".gitignore",
            """
            /dist/**
            !README.md
            """);
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.ShouldPruneDirectory("dist", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenNestedIgnoreNegationCanRestoreSubtreeFileKeepsDirectoryEnumerable()
    {
        await WriteAsync(".gitignore", "/dist/**\n");
        await WriteAsync("dist/.gitignore", "!README.md\n");
        var snapshot = CreateSnapshot();

        var restored = snapshot.Evaluate("dist/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(snapshot.ShouldPruneDirectory("dist", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(restored.Included);
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenRootNegationCanReachDescendantKeepsDirectoryEnumerable()
    {
        await WriteAsync(
            ".gitignore",
            """
            build/
            !build/
            !build/public.md
            """);
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.ShouldPruneDirectory("build", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task Evaluate_WhenIgnoredParentIsNotUnignoredDoesNotRestoreChildNegation()
    {
        await WriteAsync(
            ".gitignore",
            """
            build/
            !build/public.md
            """);
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("build/public.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, decision.Code);
    }

    [Fact]
    public async Task Evaluate_WhenNestedIgnoredAncestorsBlockNegatedFileUsesClosestIgnoredAncestor()
    {
        await WriteAsync(
            ".gitignore",
            """
            build/
            build/generated/
            !build/generated/public.md
            """);
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("build/generated/public.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, decision.Code);
        var trace = Assert.Single(decision.Trace, trace => trace.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore);
        Assert.Equal(2, trace.LineNumber);
        Assert.Equal("build/generated/", trace.Pattern);
    }

    [Fact]
    public async Task Evaluate_WhenParentAndChildAreUnignoredRestoresChild()
    {
        await WriteAsync(
            ".gitignore",
            """
            build/
            !build/
            !build/public.md
            """);
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("build/public.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(decision.Included);
        Assert.Contains(decision.Trace, trace => trace.Code == AppSurfaceDocsHarvestPathDecisionCode.MatchedVcsIgnoreNegation);
    }

    [Fact]
    public async Task Evaluate_WhenNestedIgnoreExistsAppliesRelativeToNestedDirectory()
    {
        await WriteAsync("src/.gitignore", "/generated/\n");
        var snapshot = CreateSnapshot();

        var ignored = snapshot.Evaluate("src/generated/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var outsideNestedBase = snapshot.Evaluate("generated/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(ignored.Included);
        Assert.True(outsideNestedBase.Included);
    }

    [Fact]
    public async Task Evaluate_WhenSlashfulDirectoryOnlyPatternMatchesAncestorExcludesDescendant()
    {
        await WriteAsync(".gitignore", "src/generated/\n");
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("src/generated/nested/guide.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, decision.Code);
    }

    [Fact]
    public async Task Evaluate_WhenPatternsUseEscapesAndCharacterGlobsMatchesGitStyleRules()
    {
        await WriteAsync(
            ".gitignore",
            """

            # comment
            \#literal.md
            \!literal.md
            file?.md
            data[0-9].md
            broken[.md
            trail\
            """);
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.Evaluate("#literal.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("!literal.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("file1.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("data7.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("broken[.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.True(snapshot.Evaluate("comment.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
    }

    [Fact]
    public async Task Evaluate_WhenIgnorePatternContainsMalformedCharacterClassSkipsRule()
    {
        await WriteAsync(".gitignore", "[z-a]\nvalid.md\n");
        var snapshot = CreateSnapshot();

        var validDecision = snapshot.Evaluate("valid.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var malformedDecision = snapshot.Evaluate("README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(validDecision.Included);
        Assert.True(malformedDecision.Included);
    }

    [Fact]
    public void VcsIgnoreRule_WhenRegexPatternIsMalformedDoesNotThrowOrMatch()
    {
        var rule = new AppSurfaceDocsHarvestVcsIgnoreRule(
            ".gitignore",
            1,
            "[z-a]",
            BaseDirectory: string.Empty,
            IsNegated: false,
            DirectoryOnly: false,
            RootRelative: false,
            "[z-a]");

        Assert.False(rule.Matches("z", isDirectory: false));
        Assert.False(rule.MatchesDirectorySubtree("z"));
    }

    [Fact]
    public async Task Evaluate_WhenEscapedTrailingSpacePatternMatchesLiteralSpace()
    {
        await WriteAsync(".gitignore", "space\\ .md\nliteral\\ \ntrimmed.md   \n");
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.Evaluate("space .md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("literal ", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("trimmed.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
    }

    [Fact]
    public async Task Evaluate_WhenPatternsBecomeEmptyAfterNegationOrTrimmingIgnoresThoseRules()
    {
        await WriteAsync(".gitignore", "!\n/\nvalid.md\n");
        var snapshot = CreateSnapshot();

        Assert.True(snapshot.Evaluate("other.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("valid.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
    }

    [Fact]
    public async Task Evaluate_WhenNestedRepositoryBoundaryExistsDoesNotReadNestedIgnoreFile()
    {
        Directory.CreateDirectory(Path.Join(_root, "nested", ".git"));
        await WriteAsync("nested/.gitignore", "Hidden.md\n");
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate("nested/Hidden.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(decision.Included);
    }

    [Fact]
    public async Task EvaluateFile_WhenRelativePathEscapesRepositoryIgnoresEscapingIgnoreDirectory()
    {
        await WriteAsync(".gitignore", "*.md\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        var match = policy.EvaluateFile("../outside/file.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.NotNull(match);
        Assert.True(match.Ignored);
    }

    [Fact]
    public async Task GetDiagnostics_WhenMoreThanMaxSamplesRecordsCountsAndCapsSamples()
    {
        await WriteAsync(".gitignore", "generated/\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        for (var index = 0; index < 25; index++)
        {
            _ = policy.EvaluateFile($"generated/{index}.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        }

        var diagnostics = policy.GetDiagnostics();

        Assert.Equal(25, diagnostics.ExclusionCountsBySourceKind[AppSurfaceDocsHarvestSourceKind.Markdown]);
        Assert.Equal(20, diagnostics.ExclusionSamples.Count);
    }

    [Fact]
    public void VcsIgnoreDiagnosticsCollector_WhenMoreThanMaxWarningsCapsWarnings()
    {
        var collector = new VcsIgnoreDiagnosticsCollector(maxSamples: 2);

        collector.RecordWarning(".gitignore", "one", "cause", "fix");
        collector.RecordWarning("nested/.gitignore", "two", "cause", "fix");
        collector.RecordWarning("other/.gitignore", "three", "cause", "fix");

        var diagnostics = collector.CreateSnapshot(enabled: true);

        Assert.Equal(2, diagnostics.Warnings.Count);
    }

    [Fact]
    public async Task CreateHealthDiagnostics_WhenIgnoreFileCannotBeReadReturnsWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteAsync(".gitignore", "generated/\n");
        var ignorePath = Path.Join(_root, ".gitignore");
        File.SetUnixFileMode(ignorePath, UnixFileMode.UserWrite);

        try
        {
            var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
                _root,
                new AppSurfaceDocsHarvestVcsIgnoreOptions(),
                NullLogger.Instance);

            var match = policy.EvaluateFile("README.md", AppSurfaceDocsHarvestSourceKind.Markdown);
            var diagnostics = policy.CreateHealthDiagnostics();

            Assert.Null(match);
            var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.VcsIgnoreWarning);
            Assert.Equal(DocHarvestDiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("could not read", warning.Problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(ignorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void VcsIgnoreRule_WhenPathIsOutsideBaseDoesNotMatch()
    {
        var rule = new AppSurfaceDocsHarvestVcsIgnoreRule(
            "src/.gitignore",
            1,
            "*.md",
            "src",
            IsNegated: false,
            DirectoryOnly: false,
            RootRelative: false,
            "*.md");

        Assert.False(rule.Matches("README.md", isDirectory: false));
        Assert.False(rule.CouldMatchDirectoryOrDescendant("docs"));
    }

    [Fact]
    public void VcsIgnoreRule_WhenSlashfulLiteralPrefixOverlapsDirectoryCanMatchDescendant()
    {
        var rule = new AppSurfaceDocsHarvestVcsIgnoreRule(
            ".gitignore",
            1,
            "src/generated/**",
            BaseDirectory: string.Empty,
            IsNegated: true,
            DirectoryOnly: false,
            RootRelative: false,
            "src/generated/**");

        Assert.True(rule.CouldMatchDirectoryOrDescendant("src"));
        Assert.True(rule.CouldMatchDirectoryOrDescendant("src/generated"));
        Assert.False(rule.CouldMatchDirectoryOrDescendant("docs"));
    }

    [Fact]
    public async Task Evaluate_GitParityFixtureMatchesCheckIgnoreWhenGitIsAvailable()
    {
        if (!await IsGitAvailableAsync())
        {
            return;
        }

        await RunGitAsync("init");
        await WriteAsync(
            ".gitignore",
            """
            bower_components/
            /dist/**
            *.generated.md
            """);
        var snapshot = CreateSnapshot();
        var paths = new[]
        {
            "bower_components/pkg/readme.md",
            "src/bower_components/pkg/readme.md",
            "dist/app.md",
            "src/dist/app.md",
            "docs/api.generated.md",
            "docs/public.md"
        };
        foreach (var path in paths)
        {
            await WriteAsync(path, "# candidate");
        }

        foreach (var path in paths)
        {
            var gitIgnored = await IsIgnoredByGitAsync(path);
            var decision = snapshot.Evaluate(path, AppSurfaceDocsHarvestSourceKind.Markdown);

            var appSurfaceIgnored = decision.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore;
            Assert.True(
                gitIgnored == appSurfaceIgnored,
                $"Path '{path}' expected Git ignored={gitIgnored} but AppSurface decision was {decision.Code}.");
        }
    }

    private AppSurfaceDocsHarvestPathPolicySnapshot CreateSnapshot(Action<AppSurfaceDocsOptions>? configure = null)
    {
        var options = new AppSurfaceDocsOptions();
        configure?.Invoke(options);
        return new AppSurfaceDocsHarvestPathPolicySnapshotFactory(options, NullLogger.Instance).Create(_root);
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        var localRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(localRelativePath))
        {
            throw new ArgumentException("Fixture paths must be relative.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Join(_root, localRelativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Fixture paths must stay under the test root.", nameof(relativePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private static async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            var result = await RunProcessAsync(Directory.GetCurrentDirectory(), "git", "--version");
            return result.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task RunGitAsync(string arguments)
    {
        var result = await RunProcessAsync(_root, "git", arguments);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task<bool> IsIgnoredByGitAsync(string relativePath)
    {
        var result = await RunProcessAsync(_root, "git", $"check-ignore --verbose -- {relativePath}");
        return result.ExitCode == 0;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string workingDirectory, string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process.");
        var output = await process.StandardOutput.ReadToEndAsync();
        output += await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
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
