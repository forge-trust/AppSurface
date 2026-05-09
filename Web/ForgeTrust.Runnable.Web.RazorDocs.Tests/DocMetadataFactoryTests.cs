using FakeItEasy;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class DocMetadataFactoryTests
{
    [Fact]
    public void CreateMarkdownMetadata_ShouldSetResolvedTitle_AndBuildBreadcrumbsFromMergedNavGroup()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "guides/quickstart.md",
            "Quickstart",
            new DocMetadata
            {
                NavGroup = " Concepts "
            },
            "Get started fast.");

        Assert.Equal("Quickstart", metadata.Title);
        Assert.Equal("Concepts", metadata.NavGroup);
        Assert.Equal(["Concepts", "Quickstart"], metadata.Breadcrumbs);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldMarkAuthoredBreadcrumbsAsMatchingPathTargets_WhenMarkdownPathSegmentsAlign()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "guides/quickstart.md",
            "Quickstart",
            new DocMetadata
            {
                Breadcrumbs = ["Get Started", "Quickstart"]
            },
            null);

        Assert.Equal(["Get Started", "Quickstart"], metadata.Breadcrumbs);
        Assert.True(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldMarkExplicitBreadcrumbs_WhenTheyAlignWithPathTargets()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "releases/unreleased.md",
            "Unreleased",
            new DocMetadata
            {
                Breadcrumbs = ["Releases", "Unreleased"]
            },
            null);

        Assert.True(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldNotMarkAuthoredBreadcrumbsAsMatchingPathTargets_WhenMarkdownPathSegmentCountDiffers()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "guides/quickstart.md",
            "Quickstart",
            new DocMetadata
            {
                Breadcrumbs = ["Other", "Quickstart", "Extra"]
            },
            null);

        Assert.Equal(["Other", "Quickstart", "Extra"], metadata.Breadcrumbs);
        Assert.Null(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldTreatNestedReadmeAsDirectoryLanding_ForBreadcrumbAlignment()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "releases/README.md",
            "Releases",
            new DocMetadata
            {
                Breadcrumbs = ["Releases"]
            },
            null);

        Assert.True(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldMarkExplicitBreadcrumbs_WhenTheyIncludeAuthoredNavGroupParent()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "CHANGELOG.md",
            "Changelog",
            new DocMetadata
            {
                NavGroup = "Releases",
                Breadcrumbs = ["Releases", "Changelog"]
            },
            null);

        Assert.True(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldAcceptReleaseNavGroupWithoutWarning()
    {
        var logger = A.Fake<ILogger>();
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "releases/unreleased.md",
            "Unreleased",
            new DocMetadata
            {
                NavGroup = "Releases",
                Breadcrumbs = ["Releases", "Unreleased"]
            },
            null,
            logger);

        Assert.Equal("Releases", metadata.NavGroup);
        Assert.False(metadata.NavGroupIsDerived);
        Assert.Equal(["Releases", "Unreleased"], metadata.Breadcrumbs);
        Assert.True(metadata.BreadcrumbsMatchPathTargets);
        A.CallTo(logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Warning)
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("releases/README.md")]
    [InlineData("releases/upgrade-policy.md")]
    [InlineData("CHANGELOG.md")]
    public void CreateMarkdownMetadata_ShouldDeriveReleaseSection_ForReleasePaths(string path)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Release Notes", null, null);

        Assert.Equal("Releases", metadata.NavGroup);
        Assert.True(metadata.NavGroupIsDerived);
        Assert.Equal(["Releases", "Release Notes"], metadata.Breadcrumbs);
    }

    [Theory]
    [InlineData("docs/CHANGELOG.md")]
    [InlineData("packages/CHANGELOG.md")]
    public void CreateMarkdownMetadata_ShouldNotDeriveReleaseSection_ForNestedChangelogPaths(string path)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Package Changelog", null, null);

        Assert.Equal("How-to Guides", metadata.NavGroup);
        Assert.True(metadata.NavGroupIsDerived);
        Assert.Equal(["How-to Guides", "Package Changelog"], metadata.Breadcrumbs);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldNotMarkExplicitBreadcrumbs_WhenTargetCountDoesNotMatch()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "CHANGELOG.md",
            "Changelog",
            new DocMetadata
            {
                Breadcrumbs = ["Releases", "Changelog"]
            },
            null);

        Assert.Null(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldTreatExtensionlessInternalPathsAsInternal()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "docs/benchmarks",
            "Benchmarks",
            null,
            null);

        Assert.Equal("internals", metadata.PageType);
        Assert.Equal("contributor", metadata.Audience);
        Assert.True(metadata.HideFromPublicNav);
        Assert.Null(metadata.HideFromSearch);
    }

    [Theory]
    [InlineData("Tests/guide.md")]
    [InlineData("test/guide.md")]
    [InlineData("docs/ForgeTrust.Runnable.Web.Tests/README.md")]
    public void CreateMarkdownMetadata_ShouldTreatRootLevelTestPathsAsInternal(string path)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Guide", null, null);

        Assert.Equal("internals", metadata.PageType);
        Assert.Equal("contributor", metadata.Audience);
        Assert.True(metadata.HideFromPublicNav);
        Assert.Null(metadata.HideFromSearch);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldAllowExplicitSearchVisibilityOverrideForInternalPaths()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "Tests/guide.md",
            "Guide",
            new DocMetadata
            {
                HideFromSearch = false
            },
            null);

        Assert.False(metadata.HideFromSearch);
    }

    [Theory]
    [InlineData("", "guide", "implementer")]
    [InlineData(" /guide.md", "guide", "implementer")]
    public void CreateMarkdownMetadata_ShouldTreatEmptyOrWhitespaceSegmentsAsNonInternal(
        string path,
        string expectedPageType,
        string expectedAudience)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Guide", null, null);

        Assert.Equal(expectedPageType, metadata.PageType);
        Assert.Equal(expectedAudience, metadata.Audience);
        Assert.Null(metadata.HideFromPublicNav);
        Assert.Null(metadata.HideFromSearch);
    }

    [Theory]
    [InlineData("ForgeTrust.Runnable.Web.RazorDocs.Services", "RazorDocs")]
    [InlineData("ForgeTrust.Runnable.Web.RazorWire.Bridge", "RazorWire")]
    [InlineData("ForgeTrust.Runnable.Dependency.Autofac", "Autofac")]
    [InlineData("ForgeTrust.Runnable.Caching.Redis", "Caching")]
    [InlineData("RazorWireWebExample.Services", "RazorWireWebExample")]
    public void DeriveComponentFromNamespace_ShouldReturnOwningComponent(string namespaceName, string expectedComponent)
    {
        var component = DocMetadataFactory.DeriveComponentFromNamespace(namespaceName);

        Assert.Equal(expectedComponent, component);
    }

    [Fact]
    public void DeriveComponentFromNamespace_ShouldReturnRunnableForBareRunnableNamespace()
    {
        var component = DocMetadataFactory.DeriveComponentFromNamespace("ForgeTrust.Runnable.");

        Assert.Equal("Runnable", component);
    }

    [Theory]
    [InlineData("guides/quickstart.md", null)]
    [InlineData("docs/Runnable/overview.md", "Runnable")]
    public void DeriveComponentFromPath_ShouldReturnExpectedComponent(string path, string? expectedComponent)
    {
        var component = DocMetadataFactory.DeriveComponentFromPath(path);

        Assert.Equal(expectedComponent, component);
    }

    [Theory]
    [InlineData("ForgeTrust.Runnable.Web.Tests", true)]
    [InlineData("ForgeTrust.Runnable.Web.RazorWire.Cli.Tests", true)]
    [InlineData("RunnableBenchmarks.Web", true)]
    [InlineData("ForgeTrust.Runnable.Web.RazorDocs", false)]
    public void CreateApiReferenceMetadata_ShouldHideInternalNamespacesFromPublicNavigationOnly(string namespaceName, bool expectedHidden)
    {
        var metadata = DocMetadataFactory.CreateApiReferenceMetadata("Title", namespaceName);

        Assert.Equal(expectedHidden ? true : null, metadata.HideFromPublicNav);
        Assert.Null(metadata.HideFromSearch);
    }

    [Fact]
    public void CreateApiReferenceMetadata_ShouldHideExternalGeneratedNamespacesFromPublicNavigationOnly()
    {
        var metadata = DocMetadataFactory.CreateApiReferenceMetadata("Title", "RazorWireWebExample.Services");

        Assert.True(metadata.HideFromPublicNav);
        Assert.Null(metadata.HideFromSearch);
        Assert.Equal("RazorWireWebExample", metadata.Component);
    }

    [Fact]
    public void CreateApiReferenceMetadata_ShouldAddTitleToBreadcrumbs_WhenNamespaceIsEmpty()
    {
        var metadata = DocMetadataFactory.CreateApiReferenceMetadata("Web", string.Empty);

        Assert.Equal(["Namespaces", "Web"], metadata.Breadcrumbs);
        Assert.True(metadata.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldClassifyRootReadmeAsStartHere()
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata("README.md", "Home", null, null);

        Assert.Equal("Start Here", metadata.NavGroup);
        Assert.Equal("guide", metadata.PageType);
        Assert.Equal("implementer", metadata.Audience);
    }

    [Theory]
    [InlineData("concepts/architecture.md", "Concepts")]
    [InlineData("docs/troubleshoot-error.md", "Troubleshooting")]
    public void CreateMarkdownMetadata_ShouldClassifyConceptAndTroubleshootingPaths(
        string path,
        string expectedNavGroup)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Doc", null, null);

        Assert.Equal(expectedNavGroup, metadata.NavGroup);
    }

    [Theory]
    [InlineData("start_here")]
    [InlineData("Start-Here")]
    public void CreateMarkdownMetadata_ShouldNormalizeExplicitNavGroupAliases(string explicitNavGroup)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "README.md",
            "Home",
            new DocMetadata
            {
                NavGroup = explicitNavGroup
            },
            null);

        Assert.Equal("Start Here", metadata.NavGroup);
        Assert.False(metadata.NavGroupIsDerived);
        Assert.Equal(["Start Here", "Home"], metadata.Breadcrumbs);
    }

    [Fact]
    public void CreateMarkdownMetadata_ShouldFallbackToDerivedSection_WhenExplicitNavGroupIsInvalid()
    {
        var logger = A.Fake<ILogger>();
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(
            "README.md",
            "Home",
            new DocMetadata
            {
                NavGroup = "bogus-group"
            },
            null,
            logger);

        Assert.Equal("Start Here", metadata.NavGroup);
        Assert.True(metadata.NavGroupIsDerived);
        Assert.Equal(["Start Here", "Home"], metadata.Breadcrumbs);
        A.CallTo(logger)
            .Where(
                call => call.Method.Name == "Log"
                        && call.GetArgument<LogLevel>(0) == LogLevel.Warning
                        && call.GetArgument<object>(2) != null
                        && call.GetArgument<object>(2)!.ToString()!.Contains(
                            "Ignoring invalid nav_group value",
                            StringComparison.Ordinal))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void DeriveComponentFromNamespace_ShouldReturnOriginalValue_WhenFallbackNamespaceHasNoSegments()
    {
        var component = DocMetadataFactory.DeriveComponentFromNamespace(".");

        Assert.Equal(".", component);
    }

    [Theory]
    [InlineData(@"examples\mvc\guide.md", "example", "Examples", "implementer")]
    [InlineData(@"docs\ForgeTrust.Runnable.Web.Tests\README.md", "internals", "Internals", "contributor")]
    public void CreateMarkdownMetadata_ShouldNormalizeBackslashPaths(
        string path,
        string expectedPageType,
        string expectedNavGroup,
        string expectedAudience)
    {
        var metadata = DocMetadataFactory.CreateMarkdownMetadata(path, "Guide", null, null);

        Assert.Equal(expectedPageType, metadata.PageType);
        Assert.Equal(expectedNavGroup, metadata.NavGroup);
        Assert.Equal(expectedAudience, metadata.Audience);
    }
}
