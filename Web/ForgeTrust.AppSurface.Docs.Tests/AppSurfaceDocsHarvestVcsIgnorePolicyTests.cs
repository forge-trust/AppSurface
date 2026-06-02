using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
    public async Task ShouldPruneDirectory_WhenDirectoryIsEmptyDoesNotPrune()
    {
        await WriteAsync(".gitignore", "*\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.False(policy.ShouldPruneDirectory(string.Empty, AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenDirectoryIsRootedDoesNotProbeOutsideRepository()
    {
        await WriteAsync(".gitignore", "*\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        var rootedDirectory = Path.Join(Path.GetPathRoot(_root)!, "outside-repository");

        Assert.False(policy.ShouldPruneDirectory(rootedDirectory, AppSurfaceDocsHarvestSourceKind.Markdown));
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

    [Theory]
    [InlineData("/dist", "dist/app.md")]
    [InlineData("src/generated", "src/generated/nested/guide.md")]
    public async Task Evaluate_WhenDirectoryPatternHasNoTrailingSlashExcludesDescendant(
        string pattern,
        string candidatePath)
    {
        await WriteAsync(".gitignore", $"{pattern}\n");
        var snapshot = CreateSnapshot();

        var decision = snapshot.Evaluate(candidatePath, AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, decision.Code);
        Assert.Contains(decision.Trace, trace => trace.Pattern == pattern);
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
            [!a]*.md
            broken[.md
            trail\
            """);
        var snapshot = CreateSnapshot();

        Assert.False(snapshot.Evaluate("#literal.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("!literal.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("file1.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("data7.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("banana.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.True(snapshot.Evaluate("apple.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.False(snapshot.Evaluate("broken[.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
        Assert.True(snapshot.Evaluate("acomment.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
    }

    [Fact]
    public async Task EvaluateFile_WhenNegatedCharacterClassContainsClosingBracketUsesGitStyleClass()
    {
        await WriteAsync(
            ".gitignore",
            """
            [!]
            [!]]
            """);
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.NotNull(policy.EvaluateFile("!", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.Null(policy.EvaluateFile("]", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.Null(policy.EvaluateFile("[!]", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Theory]
    [InlineData("[", "[")]
    [InlineData("[!", "[!")]
    [InlineData("[]", "[]")]
    [InlineData("[^]", "^")]
    [InlineData("[[]", "[")]
    [InlineData("[\\\\]", "\\")]
    public async Task EvaluateFile_WhenCharacterClassFallsBackOrEscapesMatchesExpectedLiteral(
        string pattern,
        string candidatePath)
    {
        await WriteAsync(".gitignore", $"{pattern}\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.NotNull(policy.EvaluateFile(candidatePath, AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public async Task EvaluateFile_WhenNegatedCharacterClassIsUnclosedSkipsRule()
    {
        await WriteAsync(".gitignore", "[!a\n");
        var policy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        Assert.Null(policy.EvaluateFile("[!a", AppSurfaceDocsHarvestSourceKind.Markdown));
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
    public async Task Evaluate_GitParityFixtureMatchesStructuredCheckIgnoreRecordsWhenGitIsAvailable()
    {
        if (!await IsGitAvailableAsync())
        {
            return;
        }

        await RunGitAsync("init");
        await WriteAsync(".git/info/exclude", string.Empty);
        await WriteAsync(
            ".gitignore",
            """
            bower_components/
            /dist/**
            *.generated.md
            CaseSensitive.md
            """);
        var snapshot = CreateSnapshot();
        var paths = new[]
        {
            "bower_components/pkg/readme.md",
            "src/bower_components/pkg/readme.md",
            "dist/app.md",
            "src/dist/app.md",
            "docs/api.generated.md",
            "docs/space name.generated.md",
            "docs/api+generated.generated.md",
            "docs/public.md",
            "casesensitive.md",
            "CaseSensitive.md"
        };
        foreach (var path in paths)
        {
            await WriteAsync(path, "# candidate");
        }

        await WriteAsync("docs/tracked.generated.md", "# tracked but still ignored by AppSurface");
        await RunGitAsync("add", "-f", "docs/tracked.generated.md");
        var allPaths = paths.Append("docs/tracked.generated.md").ToArray();
        var gitResult = await RunGitCheckIgnoreAsync(allPaths);
        var gitRecords = gitResult.Records.ToDictionary(record => record.Path, StringComparer.Ordinal);

        foreach (var path in allPaths)
        {
            var gitRecord = gitRecords[path];
            var decision = snapshot.Evaluate(path, AppSurfaceDocsHarvestSourceKind.Markdown);

            var appSurfaceIgnored = decision.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore;
            Assert.True(
                gitRecord.IsIgnored == appSurfaceIgnored,
                BuildGitParityFailureMessage(gitResult, gitRecord, decision));

            if (!gitRecord.IsIgnored)
            {
                Assert.Null(gitRecord.SourcePath);
                Assert.Null(gitRecord.LineNumber);
                Assert.Null(gitRecord.Pattern);
                continue;
            }

            var trace = Assert.Single(decision.Trace, trace => trace.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore);
            Assert.Equal(gitRecord.SourcePath, trace.SourcePath);
            Assert.Equal(gitRecord.LineNumber, trace.LineNumber);
            Assert.Equal(gitRecord.Pattern, trace.Pattern);
        }

        Assert.False(gitRecords["casesensitive.md"].IsIgnored);
        Assert.True(gitRecords["CaseSensitive.md"].IsIgnored);
        Assert.True(gitRecords["docs/tracked.generated.md"].IsIgnored);
        Assert.Contains("docs/public.md", gitRecords.Keys);
        Assert.False(gitRecords["docs/public.md"].IsIgnored);
    }

    [Fact]
    public void GitParityFailureMessage_ShouldIncludeDebugContext()
    {
        var gitResult = new GitCheckIgnoreResult(
            "git version 2.test",
            "git -c core.excludesFile=<empty> check-ignore --verbose --non-matching -z --stdin --no-index",
            1,
            "raw-out",
            "raw-err",
            [
                new GitCheckIgnoreRecord("docs/public.md", IsIgnored: false, SourcePath: null, LineNumber: null, Pattern: null)
            ]);
        var decision = new AppSurfaceDocsHarvestPathDecision(
            false,
            "docs/public.md",
            AppSurfaceDocsHarvestSourceKind.Markdown,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore,
            [
                new AppSurfaceDocsHarvestPathRuleTrace(
                    AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore,
                    "VCS ignore",
                    "*.md",
                    null,
                    true,
                    ".gitignore",
                    7)
            ],
            []);

        var message = BuildGitParityFailureMessage(gitResult, gitResult.Records[0], decision);

        Assert.Contains("git version 2.test", message, StringComparison.Ordinal);
        Assert.Contains("check-ignore", message, StringComparison.Ordinal);
        Assert.Contains("raw-out", message, StringComparison.Ordinal);
        Assert.Contains("raw-err", message, StringComparison.Ordinal);
        Assert.Contains("docs/public.md", message, StringComparison.Ordinal);
        Assert.Contains(".gitignore:7", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_WhenVcsIgnorePatternDiffersOnlyByCaseUsesOrdinalMatching()
    {
        await WriteAsync(".gitignore", "Readme.md\n");
        var snapshot = CreateSnapshot();

        var lowerCaseDecision = snapshot.Evaluate("README.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        var exactCaseDecision = snapshot.Evaluate("Readme.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.True(lowerCaseDecision.Included);
        Assert.False(exactCaseDecision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByVcsIgnore, exactCaseDecision.Code);
    }

    [Fact]
    public async Task ShouldPruneDirectory_WhenManyNestedRulesAndBroadAllowGlobsExistKeepsReachableTrees()
    {
        await WriteAsync(
            ".gitignore",
            """
            generated/
            !generated/public/
            !generated/public/**/*.md
            """);
        for (var index = 0; index < 25; index++)
        {
            await WriteAsync($"src/level{index}/.gitignore", "cache/\n!cache/public.md\n");
        }

        var snapshot = CreateSnapshot(options => options.Harvest.Paths.VcsIgnore.AllowGlobs = ["generated/public/**"]);

        Assert.False(snapshot.ShouldPruneDirectory("generated", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(snapshot.ShouldPruneDirectory("src/level0/cache", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(snapshot.Evaluate("src/level0/cache/private.md", AppSurfaceDocsHarvestSourceKind.Markdown).Included);
    }

    private async Task<GitCheckIgnoreResult> RunGitCheckIgnoreAsync(IReadOnlyList<string> paths)
    {
        var emptyGlobalExcludesPath = Path.Join(_root, ".git", "appsurface-empty-global-excludes");
        await File.WriteAllTextAsync(emptyGlobalExcludesPath, string.Empty);
        var gitVersion = await RunProcessAsync(Directory.GetCurrentDirectory(), "git", ["--version"]);
        var arguments = new[]
        {
            "-c",
            $"core.excludesFile={emptyGlobalExcludesPath}",
            "-c",
            "core.ignoreCase=false",
            "check-ignore",
            "--verbose",
            "--non-matching",
            "-z",
            "--stdin",
            "--no-index"
        };
        var result = await RunProcessAsync(_root, "git", arguments, string.Join('\0', paths) + '\0');
        Assert.True(
            result.ExitCode is 0 or 1,
            $"git check-ignore failed with exit code {result.ExitCode}.\nCommand: git {string.Join(' ', arguments)}\nstdout:\n{result.Output}\nstderr:\n{result.Error}");

        return new GitCheckIgnoreResult(
            gitVersion.Output.Trim(),
            $"git {string.Join(' ', arguments)}",
            result.ExitCode,
            result.Output,
            result.Error,
            ParseGitCheckIgnoreRecords(result.Output));
    }

    private static IReadOnlyList<GitCheckIgnoreRecord> ParseGitCheckIgnoreRecords(string output)
    {
        var fields = output.Split('\0');
        var records = new List<GitCheckIgnoreRecord>();
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            var sourcePath = fields[index];
            var lineText = fields[index + 1];
            var pattern = fields[index + 2];
            var path = fields[index + 3];
            if (path.Length == 0)
            {
                continue;
            }

            var ignored = sourcePath.Length > 0;
            records.Add(
                new GitCheckIgnoreRecord(
                    path,
                    ignored,
                    ignored ? sourcePath : null,
                    ignored ? int.Parse(lineText, System.Globalization.CultureInfo.InvariantCulture) : null,
                    ignored ? pattern : null));
        }

        return records;
    }

    private static string BuildGitParityFailureMessage(
        GitCheckIgnoreResult gitResult,
        GitCheckIgnoreRecord gitRecord,
        AppSurfaceDocsHarvestPathDecision decision)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Path '{gitRecord.Path}' diverged between Git and AppSurface VCS-ignore decisions.");
        builder.AppendLine($"Git ignored: {gitRecord.IsIgnored}");
        builder.AppendLine($"AppSurface decision: {decision.Code}, included: {decision.Included}");
        builder.AppendLine($"Git version: {gitResult.GitVersion}");
        builder.AppendLine($"Git command: {gitResult.Command}");
        builder.AppendLine($"Git exit code: {gitResult.ExitCode}");
        builder.AppendLine($"Git record: source={gitRecord.SourcePath ?? "<none>"}, line={gitRecord.LineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<none>"}, pattern={gitRecord.Pattern ?? "<none>"}");
        builder.AppendLine("AppSurface trace:");
        foreach (var trace in decision.Trace)
        {
            builder.AppendLine($"- {trace.Code}: {trace.SourcePath}:{trace.LineNumber} {trace.Pattern}");
        }

        builder.AppendLine("All Git records:");
        foreach (var record in gitResult.Records)
        {
            builder.AppendLine($"- {record.Path}: ignored={record.IsIgnored}, source={record.SourcePath ?? "<none>"}, line={record.LineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<none>"}, pattern={record.Pattern ?? "<none>"}");
        }

        builder.AppendLine("stdout:");
        builder.AppendLine(gitResult.Output);
        builder.AppendLine("stderr:");
        builder.AppendLine(gitResult.Error);
        return builder.ToString();
    }

    private AppSurfaceDocsHarvestPathPolicySnapshot CreateSnapshot(Action<AppSurfaceDocsOptions>? configure = null)
    {
        var options = new AppSurfaceDocsOptions();
        configure?.Invoke(options);
        return new AppSurfaceDocsHarvestPathPolicySnapshotFactory(options, NullLogger.Instance).Create(_root);
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        string fullPath;
        try
        {
            fullPath = TestPathUtils.PathUnder(_root, relativePath);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Fixture paths must be relative and stay under the test root.", nameof(relativePath), exception);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private static async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            var result = await RunProcessAsync(Directory.GetCurrentDirectory(), "git", ["--version"]);
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

    private async Task RunGitAsync(params string[] arguments)
    {
        var result = await RunProcessAsync(_root, "git", arguments);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between timeout observation and kill.
            }

            try
            {
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The timeout failure is the useful test signal; stream cleanup exceptions are secondary.
            }

            throw new TimeoutException($"Process '{fileName}' exceeded the 10 second test timeout.");
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record GitCheckIgnoreRecord(
        string Path,
        bool IsIgnored,
        string? SourcePath,
        int? LineNumber,
        string? Pattern);

    private sealed record GitCheckIgnoreResult(
        string GitVersion,
        string Command,
        int ExitCode,
        string Output,
        string Error,
        IReadOnlyList<GitCheckIgnoreRecord> Records);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

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
