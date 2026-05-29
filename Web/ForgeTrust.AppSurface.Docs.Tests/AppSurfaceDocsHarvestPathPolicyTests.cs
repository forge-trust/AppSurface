using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestPathPolicyTests
{
    [Theory]
    [MemberData(nameof(DefaultMarkdownExcludedPaths))]
    public void Evaluate_WithDefaultPolicyExcludesLegacyMarkdownDefaultGroups(
        string relativePath,
        string[] expectedGroups)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, decision.Code);
        Assert.All(expectedGroups, group => Assert.Contains(group, decision.MatchedDefaultGroups));
    }

    [Theory]
    [MemberData(nameof(DefaultCSharpExcludedPaths))]
    public void Evaluate_WithDefaultPolicyExcludesCSharpDefaultGroups(
        string relativePath,
        string[] expectedGroups)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, AppSurfaceDocsHarvestSourceKind.CSharp);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, decision.Code);
        Assert.All(expectedGroups, group => Assert.Contains(group, decision.MatchedDefaultGroups));
    }

    [Theory]
    [MemberData(nameof(DefaultJavaScriptExcludedPaths))]
    public void Evaluate_WithDefaultPolicyExcludesJavaScriptDefaultGroups(
        string relativePath,
        string[] expectedGroups)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, AppSurfaceDocsHarvestSourceKind.JavaScript);

        Assert.False(decision.Included);
        if (expectedGroups.Length == 0)
        {
            Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceExclude, decision.Code);
        }
        else
        {
            Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, decision.Code);
            Assert.All(expectedGroups, group => Assert.Contains(group, decision.MatchedDefaultGroups));
        }
    }

    [Theory]
    [InlineData("docs/readme.md", "Markdown")]
    [InlineData(".hidden.md", "Markdown")]
    [InlineData("examples/web-app/README.md", "Markdown")]
    [InlineData(".hidden.cs", "CSharp")]
    [InlineData("src/Contests/Fixture.cs", "CSharp")]
    [InlineData("src/browser/runtime.js", "JavaScript")]
    public void Evaluate_WithDefaultPolicyIncludesNormalCandidates(
        string relativePath,
        string sourceKind)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, ParseSourceKind(sourceKind));

        Assert.True(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultCandidate, decision.Code);
        Assert.Empty(decision.MatchedDefaultGroups);
    }

    [Fact]
    public async Task EnumerateCandidateFiles_WhenFileIsReparsePointSkipsCandidate()
    {
        var root = CreateTempDirectory();
        var externalRoot = CreateTempDirectory();
        try
        {
            var externalFile = Path.Join(externalRoot, "External.md");
            await File.WriteAllTextAsync(externalFile, "# External");
            var linkPath = Path.Join(root, "Linked.md");
            if (!TryCreateFileSymbolicLink(linkPath, externalFile))
            {
                return;
            }

            var candidates = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
                .EnumerateCandidateFiles(
                    root,
                    AppSurfaceDocsHarvestSourceKind.Markdown,
                    "*.md",
                    CancellationToken.None)
                .ToArray();

            Assert.DoesNotContain(candidates, path => path.EndsWith("Linked.md", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task EnumerateCandidateFiles_WhenDirectoryIsReparsePointSkipsTraversal()
    {
        var root = CreateTempDirectory();
        var externalRoot = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Join(externalRoot, "External.md"), "# External");
            var linkPath = Path.Join(root, "linked");
            if (!TryCreateDirectorySymbolicLink(linkPath, externalRoot))
            {
                return;
            }

            var candidates = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
                .EnumerateCandidateFiles(
                    root,
                    AppSurfaceDocsHarvestSourceKind.Markdown,
                    "*.md",
                    CancellationToken.None)
                .ToArray();

            Assert.DoesNotContain(candidates, path => path.EndsWith("External.md", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(externalRoot);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/docs/readme.md")]
    [InlineData("./docs/readme.md")]
    [InlineData("C:/repo/readme.md")]
    [InlineData("https://example.com/readme.md")]
    [InlineData("../README.md")]
    [InlineData("docs/../README.md")]
    [InlineData("docs/readme.md?raw=1")]
    [InlineData("docs/readme.md#intro")]
    [InlineData("docs/\u0000/readme.md")]
    public void Evaluate_WhenCandidatePathIsUnsafeExcludesAsInvalid(string relativePath)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, AppSurfaceDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByInvalidPath, decision.Code);
    }

    [Theory]
    [InlineData("src/Product.cs", "Markdown")]
    [InlineData("README.md", "CSharp")]
    public void Evaluate_WhenCandidateDoesNotBelongToSourceKindExcludesAsBaseCandidateMiss(
        string relativePath,
        string sourceKind)
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, ParseSourceKind(sourceKind));

        Assert.False(decision.Included);
        Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByBaseCandidate, decision.Code);
    }

    [Fact]
    public void Evaluate_AppliesGlobalAndSourceIncludesAsNestedBoundaries()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.IncludeGlobs = ["docs/**", "src/public/**"];
                options.Harvest.Markdown.IncludeGlobs = ["docs/guides/**"];
                options.Harvest.CSharp.IncludeGlobs = ["src/public/api/**"];
                options.Harvest.JavaScript.IncludeGlobs = ["src/public/api/**"];
            });

        AssertDecision(
            policy.Evaluate("docs/guides/start.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedBySourceInclude);
        AssertDecision(
            policy.Evaluate("docs/reference/readme.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/private/api/Widget.cs", AppSurfaceDocsHarvestSourceKind.CSharp),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/public/Services/Widget.cs", AppSurfaceDocsHarvestSourceKind.CSharp),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/public/api/Widget.cs", AppSurfaceDocsHarvestSourceKind.CSharp),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedBySourceInclude);
        AssertDecision(
            policy.Evaluate("src/public/api/widget.js", AppSurfaceDocsHarvestSourceKind.JavaScript),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedBySourceInclude);
    }

    [Fact]
    public void Evaluate_AppliesConfiguredExcludesAfterIncludesAndDefaultAllows()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.IncludeGlobs = ["docs/**"];
                options.Harvest.Paths.ExcludeGlobs = ["docs/private/**", "docs/.github/private/**"];
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["HiddenDirectories"] = ["docs/.github/**"];
            });

        AssertDecision(
            policy.Evaluate("docs/.github/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
        AssertDecision(
            policy.Evaluate("docs/private/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
        AssertDecision(
            policy.Evaluate("docs/.github/private/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
    }

    [Fact]
    public void Evaluate_AppliesSourceSpecificExcludesAfterGlobalAllows()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.IncludeGlobs = ["docs/**"];
                options.Harvest.Markdown.ExcludeGlobs = ["docs/private/**"];
            });

        var decision = policy.Evaluate("docs/private/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        AssertDecision(decision, included: false, AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceExclude);
        Assert.Contains(
            decision.Trace,
            trace => trace.Code == AppSurfaceDocsHarvestPathDecisionCode.ExcludedBySourceExclude
                     && trace.Scope == "Markdown"
                     && trace.Pattern == "docs/private/**"
                     && trace.Matched);
    }

    [Fact]
    public void Evaluate_RequiresAllowForEveryMatchedDefaultGroup()
    {
        var onlyHiddenAllowed = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["HiddenDirectories"] = ["docs/.github/bin/**"];
            });
        var hiddenAndBuildAllowed = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["HiddenDirectories"] = ["docs/.github/bin/**"];
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["BuildOutput"] = ["docs/.github/bin/**"];
            });

        var blockedDecision = onlyHiddenAllowed.Evaluate("docs/.github/bin/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);
        AssertDecision(blockedDecision, included: false, AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
        Assert.Contains("HiddenDirectories", blockedDecision.MatchedDefaultGroups);
        Assert.Contains("BuildOutput", blockedDecision.MatchedDefaultGroups);

        AssertDecision(
            hiddenAndBuildAllowed.Evaluate("docs/.github/bin/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
    }

    [Fact]
    public void Evaluate_AppliesDisabledDefaultGroupsByScope()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.DefaultExclusions.DisabledGroups = ["TestProjects"];
                options.Harvest.Markdown.DefaultExclusions.DisabledGroups = ["HiddenDirectories"];
                options.Harvest.CSharp.DefaultExclusions.DisabledGroups = ["CSharpExampleSource"];
            });

        AssertDecision(
            policy.Evaluate("src/Tests/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
        AssertDecision(
            policy.Evaluate(".github/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
        AssertDecision(
            policy.Evaluate(".github/Workflow.cs", AppSurfaceDocsHarvestSourceKind.CSharp),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
        AssertDecision(
            policy.Evaluate("examples/web-app/Service.cs", AppSurfaceDocsHarvestSourceKind.CSharp),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
    }

    [Fact]
    public void Evaluate_DoesNotTreatDogfoodExcludesAsPackageDefaults()
    {
        var defaultPolicy = AppSurfaceDocsHarvestPathPolicy.CreateDefault();
        var dogfoodPolicy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.ExcludeGlobs = ["**/TestResults/**", "**/generated/**"];
            });

        Assert.True(defaultPolicy.ShouldIncludeFilePath("artifacts/TestResults/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(defaultPolicy.ShouldIncludeFilePath("src/generated/README.md", AppSurfaceDocsHarvestSourceKind.Markdown));
        AssertDecision(
            dogfoodPolicy.Evaluate("artifacts/TestResults/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
        AssertDecision(
            dogfoodPolicy.Evaluate("src/generated/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: false,
            AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
    }

    [Fact]
    public void ShouldPruneDirectory_PrunesDefaultGroupsAndClearExclusionSubtrees()
    {
        var excludeSubtreePolicy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.ExcludeGlobs = ["docs/generated/**"];
            });
        var filePatternPolicy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.ExcludeGlobs = ["docs/generated/*.md"];
                options.Harvest.Markdown.IncludeGlobs = ["docs/**"];
            });

        Assert.True(AppSurfaceDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory(".github", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(AppSurfaceDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("src/bin", AppSurfaceDocsHarvestSourceKind.CSharp));
        Assert.False(AppSurfaceDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("docs", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(excludeSubtreePolicy.ShouldPruneDirectory("docs/generated", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(filePatternPolicy.ShouldPruneDirectory("docs/generated", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(filePatternPolicy.ShouldPruneDirectory("samples", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public void ShouldPruneDirectory_KeepsOnlyDefaultGroupDirectoriesWithMatchingAllows()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Markdown.DefaultExclusions.AllowGlobs["HiddenDirectories"] = [".github/workflows/**"];
            });

        Assert.False(policy.ShouldPruneDirectory(".github", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldPruneDirectory(".github/workflows", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldPruneDirectory(".git", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldPruneDirectory(".github", AppSurfaceDocsHarvestSourceKind.CSharp));
    }

    [Fact]
    public void ShouldPruneDirectory_KeepsDefaultGroupDirectoriesForFileLevelAllows()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Markdown.DefaultExclusions.AllowGlobs["HiddenDirectories"] = [".github/workflows/*.md"];
            });

        Assert.False(policy.ShouldPruneDirectory(".github", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.False(policy.ShouldPruneDirectory(".github/workflows", AppSurfaceDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldPruneDirectory(".git", AppSurfaceDocsHarvestSourceKind.Markdown));
        AssertDecision(
            policy.Evaluate(".github/workflows/README.md", AppSurfaceDocsHarvestSourceKind.Markdown),
            included: true,
            AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
    }

    [Fact]
    public void ShouldPruneDirectory_WhenDirectoryPathIsUnsafeReturnsTrue()
    {
        Assert.True(AppSurfaceDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("../secret", AppSurfaceDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public void Evaluate_WhenSourceKindIsUnknownExcludesAsBaseCandidateMiss()
    {
        var decision = AppSurfaceDocsHarvestPathPolicy.CreateDefault()
            .Evaluate("docs/readme.md", (AppSurfaceDocsHarvestSourceKind)999);

        AssertDecision(decision, included: false, AppSurfaceDocsHarvestPathDecisionCode.ExcludedByBaseCandidate);
    }

    [Fact]
    public void ShouldPruneDirectory_WhenSourceKindIsUnknownThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AppSurfaceDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("docs", (AppSurfaceDocsHarvestSourceKind)999));
    }

    [Theory]
    [InlineData("BuildOutput", true, "BuildOutput")]
    [InlineData(" buildoutput ", true, "BuildOutput")]
    [InlineData("0", false, "0")]
    [InlineData("42", false, "42")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void DefaultGroupHelpers_AcceptNamesButNotNumericValues(
        string? groupId,
        bool expectedKnown,
        string expectedNormalized)
    {
        Assert.Equal(expectedKnown, AppSurfaceDocsHarvestPathPolicy.IsKnownDefaultGroupId(groupId));
        Assert.Equal(expectedNormalized, AppSurfaceDocsHarvestPathPolicy.NormalizeDefaultGroupId(groupId));
    }

    [Fact]
    public void Evaluate_IgnoresNumericDefaultGroupConfiguration()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.DefaultExclusions.DisabledGroups = ["0"];
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["0"] = ["src/bin/**"];
            });

        var decision = policy.Evaluate("src/bin/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        AssertDecision(decision, included: false, AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
        Assert.Equal(["BuildOutput"], decision.MatchedDefaultGroups);
    }

    [Fact]
    public void Evaluate_ReturnsRuleTraceForMatchedIncludesAndDefaultAllows()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.IncludeGlobs = ["docs/**"];
                options.Harvest.Paths.DefaultExclusions.AllowGlobs["HiddenDirectories"] = ["docs/.github/**"];
            });

        var decision = policy.Evaluate("docs/.github/README.md", AppSurfaceDocsHarvestSourceKind.Markdown);

        AssertDecision(decision, included: true, AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
        Assert.Equal("docs/.github/README.md", decision.RelativePath);
        Assert.Equal(AppSurfaceDocsHarvestSourceKind.Markdown, decision.SourceKind);
        Assert.Equal(["HiddenDirectories"], decision.MatchedDefaultGroups);
        Assert.Collection(
            decision.Trace,
            trace =>
            {
                Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude, trace.Code);
                Assert.Equal("global", trace.Scope);
                Assert.Equal("docs/**", trace.Pattern);
                Assert.Null(trace.DefaultGroup);
                Assert.True(trace.Matched);
            },
            trace =>
            {
                Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, trace.Code);
                Assert.Equal("default", trace.Scope);
                Assert.Null(trace.Pattern);
                Assert.Equal("HiddenDirectories", trace.DefaultGroup);
                Assert.True(trace.Matched);
            },
            trace =>
            {
                Assert.Equal(AppSurfaceDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow, trace.Code);
                Assert.Equal("default-allow", trace.Scope);
                Assert.Equal("docs/.github/**", trace.Pattern);
                Assert.Equal("HiddenDirectories", trace.DefaultGroup);
                Assert.True(trace.Matched);
            });
    }

    public static TheoryData<string, string[]> DefaultMarkdownExcludedPaths()
    {
        return new TheoryData<string, string[]>
        {
            { ".github/workflows/file.md", ["HiddenDirectories"] },
            { ".github/bin/file.md", ["HiddenDirectories", "BuildOutput"] },
            { "docs/.agent/nested/file.md", ["HiddenDirectories"] },
            { "src/bin/file.md", ["BuildOutput"] },
            { "src/obj/file.md", ["BuildOutput"] },
            { "src/Tests/file.md", ["TestProjects"] },
            { "src/Test/file.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web.Tests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web.UnitTests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web.IntegrationTests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web.FunctionalTests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web.E2ETests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web-Tests/README.md", ["TestProjects"] },
            { "Web/ForgeTrust.AppSurface.Web_Tests/README.md", ["TestProjects"] },
            { "node_modules/pkg/readme.md", ["BuildOutput"] }
        };
    }

    public static TheoryData<string, string[]> DefaultCSharpExcludedPaths()
    {
        return new TheoryData<string, string[]>
        {
            { "examples/web-app/Service.cs", ["CSharpExampleSource"] },
            { "Web/ForgeTrust.AppSurface.Web.Tests/Fixture.cs", ["TestProjects"] },
            { "src/bin/Generated.cs", ["BuildOutput"] },
            { ".codex/Agent.cs", ["HiddenDirectories"] }
        };
    }

    public static TheoryData<string, string[]> DefaultJavaScriptExcludedPaths()
    {
        return new TheoryData<string, string[]>
        {
            { "wwwroot/app.min.js", [] },
            { "src/bin/generated.js", ["BuildOutput"] },
            { "src/Tests/browser.js", ["TestProjects"] },
            { ".config/browser.js", ["HiddenDirectories"] }
        };
    }

    private static AppSurfaceDocsHarvestPathPolicy CreatePolicy(Action<AppSurfaceDocsOptions> configure)
    {
        var options = new AppSurfaceDocsOptions();
        configure(options);

        return new AppSurfaceDocsHarvestPathPolicy(
            options,
            NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
    }

    private static AppSurfaceDocsHarvestSourceKind ParseSourceKind(string sourceKind)
    {
        return Enum.Parse<AppSurfaceDocsHarvestSourceKind>(sourceKind);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), "AppSurfaceDocsHarvestPathPolicyTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
            // Best effort cleanup for temporary symlink tests.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup for temporary symlink tests.
        }
    }

    private static void AssertDecision(
        AppSurfaceDocsHarvestPathDecision decision,
        bool included,
        AppSurfaceDocsHarvestPathDecisionCode code)
    {
        Assert.Equal(included, decision.Included);
        Assert.Equal(code, decision.Code);
    }
}
