using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class DocPathResolverTests
{
    [Fact]
    public void Create_ShouldThrow_WhenDocsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => DocPathResolver.Create(null!));
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenPathIsNull()
    {
        var resolver = DocPathResolver.Create([]);

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!));
    }

    [Fact]
    public void Resolve_ShouldMatchSourceAndCanonicalPaths_WithNormalizedSeparators()
    {
        var doc = Doc("Install", "guides/install.md", canonicalPath: "guides/install.md.html");
        var resolver = DocPathResolver.Create([doc]);

        var bySource = resolver.Resolve(" /guides\\install.md/ ");
        var byCanonical = resolver.Resolve("\\guides\\install.md.html");

        Assert.Same(doc, bySource);
        Assert.Same(doc, byCanonical);
    }

    [Fact]
    public void Resolve_ShouldPreserveExactFragmentMatches_AndFallbackToBasePage()
    {
        var fragment = Doc(
            "Foo Type",
            "Namespaces/Foo#Foo-Type",
            content: string.Empty,
            canonicalPath: "Namespaces/Foo.html#Foo-Type");
        var basePage = Doc("Foo", "Namespaces/Foo", canonicalPath: "Namespaces/Foo.html");
        var resolver = DocPathResolver.Create([fragment, basePage]);

        var exactFragment = resolver.Resolve("Namespaces/Foo.html#Foo-Type");
        var missingFragment = resolver.Resolve("Namespaces/Foo.html#Missing");
        var sourceBase = resolver.Resolve("Namespaces/Foo");
        var rootedExactFragment = resolver.Resolve("/docs/Namespaces/Foo.html#Foo-Type", "/docs");
        var rootedMissingFragment = resolver.Resolve("/docs/Namespaces/Foo.html#Missing", "/docs");
        var rootedBase = resolver.Resolve("/docs/Namespaces/Foo", "/docs");

        Assert.Same(fragment, exactFragment);
        Assert.Same(basePage, missingFragment);
        Assert.Same(basePage, sourceBase);
        Assert.Same(fragment, rootedExactFragment);
        Assert.Same(basePage, rootedMissingFragment);
        Assert.Same(basePage, rootedBase);
    }

    [Fact]
    public void Resolve_ShouldPreferNonEmptyCandidate_WhenOnlyFragmentPagesMatch()
    {
        var emptyFragment = Doc(
            "Empty Fragment",
            "guides/advanced.md#details",
            content: "   ",
            canonicalPath: "guides/advanced.md.html#details");
        var richFragment = Doc(
            "Rich Fragment",
            "guides/advanced.md#setup",
            canonicalPath: "guides/advanced.md.html#setup");
        var resolver = DocPathResolver.Create([emptyFragment, richFragment]);

        var resolved = resolver.Resolve("guides/advanced.md.html#missing-fragment");

        Assert.Same(richFragment, resolved);
    }

    [Fact]
    public void Resolve_ShouldPreferNonEmptyCandidate_WhenFallbackCandidateHasNoCanonicalPath()
    {
        var emptyFragment = Doc("Empty Fragment", "guides/advanced.md#details", content: "   ");
        var richFragment = Doc("Rich Fragment", "guides/advanced.md#setup");
        var resolver = DocPathResolver.Create([emptyFragment, richFragment]);

        var resolved = resolver.Resolve("guides/advanced.md#missing-fragment");

        Assert.Same(richFragment, resolved);
    }

    [Fact]
    public void Resolve_ShouldStripConfiguredAndStableRouteRoots()
    {
        var doc = Doc("Composition", "guides/composition.md", canonicalPath: "guides/composition.md.html");
        var resolver = DocPathResolver.Create([doc]);

        var currentRootMatch = resolver.Resolve("/preview/docs/guides/composition.md.html", "/preview/docs", "/docs");
        var stableRootMatch = resolver.Resolve("/docs/guides/composition.md.html", "/preview/docs", "/docs");

        Assert.Same(doc, currentRootMatch);
        Assert.Same(doc, stableRootMatch);
    }

    [Fact]
    public void Resolve_ShouldPreferRouteRelativeMatch_BeforeRawPathMatch()
    {
        var routeRelative = Doc("Route Relative", "guide.html", canonicalPath: "guide.html");
        var rawShadow = Doc("Raw Shadow", "docs/guide.html", canonicalPath: "docs/guide.html");
        var resolver = DocPathResolver.Create([rawShadow, routeRelative]);

        var resolved = resolver.Resolve("/docs/guide.html", "/docs");

        Assert.Same(routeRelative, resolved);
    }

    [Fact]
    public void Resolve_ShouldNotStripRouteRoots_FromSourceRelativePaths()
    {
        var sourceRelative = Doc("Source Relative", "docs/guide.html", canonicalPath: "docs/guide.html");
        var routeRelative = Doc("Route Relative", "guide.html", canonicalPath: "guide.html");
        var resolver = DocPathResolver.Create([routeRelative, sourceRelative]);

        var resolved = resolver.Resolve("docs/guide.html", "/docs");

        Assert.Same(sourceRelative, resolved);
    }

    [Fact]
    public void Resolve_ShouldIgnoreBlankRouteRoots_AndContinueToUsableRoots()
    {
        var doc = Doc("Composition", "guides/composition.md", canonicalPath: "guides/composition.md.html");
        var resolver = DocPathResolver.Create([doc]);

        var resolved = resolver.Resolve("/docs/guides/composition.md.html", "   ", "/docs");

        Assert.Same(doc, resolved);
    }

    [Fact]
    public void Resolve_ShouldMatchEmptyRelativePath_WhenInputEqualsRouteRoot()
    {
        var doc = Doc("Index", string.Empty, canonicalPath: "index.html");
        var resolver = DocPathResolver.Create([doc]);

        var resolved = resolver.Resolve("/docs", "/docs");

        Assert.Same(doc, resolved);
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenPathAndRouteRootsAreBlank()
    {
        var resolver = DocPathResolver.Create([]);

        var resolved = resolver.Resolve("   ", "   ");

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenRouteRootsArrayIsNull()
    {
        var resolver = DocPathResolver.Create([]);

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve("guides/intro.md", null!));
    }

    [Theory]
    [InlineData("docs/service.cs.html#MethodId", "docs/service.cs.html#MethodId")]
    [InlineData(" \\docs\\service.cs.html#MethodId/ ", "docs/service.cs.html#MethodId")]
    public void NormalizeCanonicalPath_ShouldPreserveFragments(string path, string expected)
    {
        var normalized = DocPathResolver.NormalizeCanonicalPath(path);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("docs/service.cs.html#MethodId", "docs/service.cs.html")]
    [InlineData(" \\docs\\service.cs.html#MethodId/ ", "docs/service.cs.html")]
    public void NormalizeLookupPath_ShouldRemoveFragments(string path, string expected)
    {
        var normalized = DocPathResolver.NormalizeLookupPath(path);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("docs/service.cs.html#MethodId", "MethodId")]
    [InlineData("docs/service.cs.html#", null)]
    [InlineData("docs/service.cs.html", null)]
    public void GetFragment_ShouldReturnOnlyNonEmptyFragments(string path, string? expected)
    {
        var fragment = DocPathResolver.GetFragment(path);

        Assert.Equal(expected, fragment);
    }

    private static DocNode Doc(
        string title,
        string path,
        string content = "<p>Content</p>",
        string? canonicalPath = null)
    {
        return new DocNode(title, path, content, CanonicalPath: canonicalPath);
    }
}
