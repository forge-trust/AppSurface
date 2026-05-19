using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RazorDocsHarvestPathPolicyTests
{
    [Theory]
    [MemberData(nameof(DefaultMarkdownExcludedPaths))]
    public void Evaluate_WithDefaultPolicyExcludesLegacyMarkdownDefaultGroups(
        string relativePath,
        string[] expectedGroups)
    {
        var decision = RazorDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, RazorDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, decision.Code);
        Assert.All(expectedGroups, group => Assert.Contains(group, decision.MatchedDefaultGroups));
    }

    [Theory]
    [MemberData(nameof(DefaultCSharpExcludedPaths))]
    public void Evaluate_WithDefaultPolicyExcludesCSharpDefaultGroups(
        string relativePath,
        string[] expectedGroups)
    {
        var decision = RazorDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, RazorDocsHarvestSourceKind.CSharp);

        Assert.False(decision.Included);
        Assert.Equal(RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, decision.Code);
        Assert.All(expectedGroups, group => Assert.Contains(group, decision.MatchedDefaultGroups));
    }

    [Theory]
    [InlineData("docs/readme.md", "Markdown")]
    [InlineData(".hidden.md", "Markdown")]
    [InlineData("examples/web-app/README.md", "Markdown")]
    [InlineData(".hidden.cs", "CSharp")]
    [InlineData("src/Contests/Fixture.cs", "CSharp")]
    public void Evaluate_WithDefaultPolicyIncludesNormalCandidates(
        string relativePath,
        string sourceKind)
    {
        var decision = RazorDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, ParseSourceKind(sourceKind));

        Assert.True(decision.Included);
        Assert.Equal(RazorDocsHarvestPathDecisionCode.IncludedByDefaultCandidate, decision.Code);
        Assert.Empty(decision.MatchedDefaultGroups);
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
        var decision = RazorDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, RazorDocsHarvestSourceKind.Markdown);

        Assert.False(decision.Included);
        Assert.Equal(RazorDocsHarvestPathDecisionCode.ExcludedByInvalidPath, decision.Code);
    }

    [Theory]
    [InlineData("src/Product.cs", "Markdown")]
    [InlineData("README.md", "CSharp")]
    public void Evaluate_WhenCandidateDoesNotBelongToSourceKindExcludesAsBaseCandidateMiss(
        string relativePath,
        string sourceKind)
    {
        var decision = RazorDocsHarvestPathPolicy.CreateDefault()
            .Evaluate(relativePath, ParseSourceKind(sourceKind));

        Assert.False(decision.Included);
        Assert.Equal(RazorDocsHarvestPathDecisionCode.ExcludedByBaseCandidate, decision.Code);
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
            });

        AssertDecision(
            policy.Evaluate("docs/guides/start.md", RazorDocsHarvestSourceKind.Markdown),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedBySourceInclude);
        AssertDecision(
            policy.Evaluate("docs/reference/readme.md", RazorDocsHarvestSourceKind.Markdown),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/private/api/Widget.cs", RazorDocsHarvestSourceKind.CSharp),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByGlobalIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/public/Services/Widget.cs", RazorDocsHarvestSourceKind.CSharp),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedBySourceIncludeMiss);
        AssertDecision(
            policy.Evaluate("src/public/api/Widget.cs", RazorDocsHarvestSourceKind.CSharp),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedBySourceInclude);
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
            policy.Evaluate("docs/.github/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
        AssertDecision(
            policy.Evaluate("docs/private/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
        AssertDecision(
            policy.Evaluate("docs/.github/private/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
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

        var blockedDecision = onlyHiddenAllowed.Evaluate("docs/.github/bin/README.md", RazorDocsHarvestSourceKind.Markdown);
        AssertDecision(blockedDecision, included: false, RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
        Assert.Contains("HiddenDirectories", blockedDecision.MatchedDefaultGroups);
        Assert.Contains("BuildOutput", blockedDecision.MatchedDefaultGroups);

        AssertDecision(
            hiddenAndBuildAllowed.Evaluate("docs/.github/bin/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
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
            policy.Evaluate("src/Tests/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
        AssertDecision(
            policy.Evaluate(".github/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
        AssertDecision(
            policy.Evaluate(".github/Workflow.cs", RazorDocsHarvestSourceKind.CSharp),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
        AssertDecision(
            policy.Evaluate("examples/web-app/Service.cs", RazorDocsHarvestSourceKind.CSharp),
            included: true,
            RazorDocsHarvestPathDecisionCode.IncludedByDefaultCandidate);
    }

    [Fact]
    public void Evaluate_DoesNotTreatDogfoodExcludesAsPackageDefaults()
    {
        var defaultPolicy = RazorDocsHarvestPathPolicy.CreateDefault();
        var dogfoodPolicy = CreatePolicy(
            options =>
            {
                options.Harvest.Paths.ExcludeGlobs = ["**/TestResults/**", "**/generated/**"];
            });

        Assert.True(defaultPolicy.ShouldIncludeFilePath("artifacts/TestResults/README.md", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(defaultPolicy.ShouldIncludeFilePath("src/generated/README.md", RazorDocsHarvestSourceKind.Markdown));
        AssertDecision(
            dogfoodPolicy.Evaluate("artifacts/TestResults/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
        AssertDecision(
            dogfoodPolicy.Evaluate("src/generated/README.md", RazorDocsHarvestSourceKind.Markdown),
            included: false,
            RazorDocsHarvestPathDecisionCode.ExcludedByGlobalExclude);
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

        Assert.True(RazorDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory(".github", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(RazorDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("src/bin", RazorDocsHarvestSourceKind.CSharp));
        Assert.False(RazorDocsHarvestPathPolicy.CreateDefault().ShouldPruneDirectory("docs", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(excludeSubtreePolicy.ShouldPruneDirectory("docs/generated", RazorDocsHarvestSourceKind.Markdown));
        Assert.False(filePatternPolicy.ShouldPruneDirectory("docs/generated", RazorDocsHarvestSourceKind.Markdown));
        Assert.False(filePatternPolicy.ShouldPruneDirectory("samples", RazorDocsHarvestSourceKind.Markdown));
    }

    [Fact]
    public void ShouldPruneDirectory_KeepsDefaultGroupDirectoryWhenAnyAllowExists()
    {
        var policy = CreatePolicy(
            options =>
            {
                options.Harvest.Markdown.DefaultExclusions.AllowGlobs["HiddenDirectories"] = [".github/workflows/**"];
            });

        Assert.False(policy.ShouldPruneDirectory(".github", RazorDocsHarvestSourceKind.Markdown));
        Assert.True(policy.ShouldPruneDirectory(".github", RazorDocsHarvestSourceKind.CSharp));
    }

    [Theory]
    [InlineData("BuildOutput", true, "BuildOutput")]
    [InlineData(" buildoutput ", true, "BuildOutput")]
    [InlineData("0", false, "0")]
    [InlineData("42", false, "42")]
    [InlineData("", false, "")]
    public void DefaultGroupHelpers_AcceptNamesButNotNumericValues(
        string groupId,
        bool expectedKnown,
        string expectedNormalized)
    {
        Assert.Equal(expectedKnown, RazorDocsHarvestPathPolicy.IsKnownDefaultGroupId(groupId));
        Assert.Equal(expectedNormalized, RazorDocsHarvestPathPolicy.NormalizeDefaultGroupId(groupId));
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

        var decision = policy.Evaluate("src/bin/README.md", RazorDocsHarvestSourceKind.Markdown);

        AssertDecision(decision, included: false, RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup);
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

        var decision = policy.Evaluate("docs/.github/README.md", RazorDocsHarvestSourceKind.Markdown);

        AssertDecision(decision, included: true, RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow);
        Assert.Equal("docs/.github/README.md", decision.RelativePath);
        Assert.Equal(RazorDocsHarvestSourceKind.Markdown, decision.SourceKind);
        Assert.Equal(["HiddenDirectories"], decision.MatchedDefaultGroups);
        Assert.Collection(
            decision.Trace,
            trace =>
            {
                Assert.Equal(RazorDocsHarvestPathDecisionCode.IncludedByGlobalInclude, trace.Code);
                Assert.Equal("global", trace.Scope);
                Assert.Equal("docs/**", trace.Pattern);
                Assert.Null(trace.DefaultGroup);
                Assert.True(trace.Matched);
            },
            trace =>
            {
                Assert.Equal(RazorDocsHarvestPathDecisionCode.ExcludedByDefaultGroup, trace.Code);
                Assert.Equal("default", trace.Scope);
                Assert.Null(trace.Pattern);
                Assert.Equal("HiddenDirectories", trace.DefaultGroup);
                Assert.True(trace.Matched);
            },
            trace =>
            {
                Assert.Equal(RazorDocsHarvestPathDecisionCode.IncludedByDefaultGroupAllow, trace.Code);
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

    private static RazorDocsHarvestPathPolicy CreatePolicy(Action<RazorDocsOptions> configure)
    {
        var options = new RazorDocsOptions();
        configure(options);

        return new RazorDocsHarvestPathPolicy(
            options,
            NullLogger<RazorDocsHarvestPathPolicy>.Instance);
    }

    private static RazorDocsHarvestSourceKind ParseSourceKind(string sourceKind)
    {
        return Enum.Parse<RazorDocsHarvestSourceKind>(sourceKind);
    }

    private static void AssertDecision(
        RazorDocsHarvestPathDecision decision,
        bool included,
        RazorDocsHarvestPathDecisionCode code)
    {
        Assert.Equal(included, decision.Included);
        Assert.Equal(code, decision.Code);
    }
}
