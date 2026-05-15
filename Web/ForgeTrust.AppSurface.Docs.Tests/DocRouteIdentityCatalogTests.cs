using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocRouteIdentityCatalogTests
{
    [Fact]
    public void Create_ShouldRedirectMarkdownSourceRoutesToCleanCanonicalRoutes()
    {
        var catalog = CreateCatalog(new DocNode("Evaluator", "start-here/appsurface-evaluator.md", "<p>Body</p>"));

        var canonical = catalog.ResolvePublicRoute("start-here/appsurface-evaluator");
        var source = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.md");
        var legacyHtml = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.md.html");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Equal("start-here/appsurface-evaluator.md", canonical.SourcePath);
        Assert.Equal("start-here/appsurface-evaluator", canonical.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, source.Kind);
        Assert.Equal("start-here/appsurface-evaluator", source.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, legacyHtml.Kind);
        Assert.Equal("start-here/appsurface-evaluator", legacyHtml.PublicRoutePath);
    }

    [Fact]
    public void Create_ShouldRedirectDotMarkdownSourceRoutesToCleanCanonicalRoutes()
    {
        var catalog = CreateCatalog(new DocNode("Evaluator", "start-here/appsurface-evaluator.markdown", "<p>Body</p>"));

        var canonical = catalog.ResolvePublicRoute("start-here/appsurface-evaluator");
        var source = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.markdown");
        var legacyHtml = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.markdown.html");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Equal("start-here/appsurface-evaluator.markdown", canonical.SourcePath);
        Assert.Equal("start-here/appsurface-evaluator", canonical.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, source.Kind);
        Assert.Equal("start-here/appsurface-evaluator", source.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, legacyHtml.Kind);
        Assert.Equal("start-here/appsurface-evaluator", legacyHtml.PublicRoutePath);
    }

    [Fact]
    public void Create_ShouldCollapseReadmeAndIndexMarkdownRoutes()
    {
        var catalog = CreateCatalog(
            new DocNode("Package", "packages/README.md", "<p>Package</p>"),
            new DocNode("Guide", "guides/index.md", "<p>Guide</p>"));

        Assert.Equal(DocRouteResolutionKind.Canonical, catalog.ResolvePublicRoute("packages").Kind);
        Assert.Equal("packages/README.md", catalog.ResolvePublicRoute("packages").SourcePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, catalog.ResolvePublicRoute("packages/README.md").Kind);
        Assert.Equal("packages", catalog.ResolvePublicRoute("packages/README.md").PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.Canonical, catalog.ResolvePublicRoute("guides").Kind);
        Assert.Equal("guides/index.md", catalog.ResolvePublicRoute("guides").SourcePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, catalog.ResolvePublicRoute("guides/index.md").Kind);
        Assert.Equal("guides", catalog.ResolvePublicRoute("guides/index.md").PublicRoutePath);
    }

    [Fact]
    public void Create_ShouldKeepNonMarkdownSourceRoutesInternal()
    {
        var catalog = CreateCatalog(new DocNode("Namespace", "Namespaces/Foo", "<p>API</p>"));

        var canonical = catalog.ResolvePublicRoute("Namespaces/Foo.html");
        var source = catalog.ResolvePublicRoute("Namespaces/Foo");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Equal("Namespaces/Foo", canonical.SourcePath);
        Assert.Equal("Namespaces/Foo.html", canonical.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.InternalSourceMatch, source.Kind);
        Assert.Equal("Namespaces/Foo.html", source.PublicRoutePath);
    }

    [Fact]
    public void Create_ShouldRejectRedirectAliasesThatShadowMarkdownSourceRoutes()
    {
        var catalog = CreateCatalog(
            new DocNode("Package", "packages/README.md", "<p>Package</p>"),
            new DocNode(
                "Other",
                "other.md",
                "<p>Other</p>",
                Metadata: new DocMetadata
                {
                    RedirectAliases = ["packages/README.md", "packages/README.md.html"]
                }));

        var source = catalog.ResolvePublicRoute("packages/README.md");
        var legacyHtml = catalog.ResolvePublicRoute("packages/README.md.html");

        Assert.Equal(DocRouteResolutionKind.AliasRedirect, source.Kind);
        Assert.Equal("packages", source.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, legacyHtml.Kind);
        Assert.Equal("packages", legacyHtml.PublicRoutePath);
        Assert.Equal(
            2,
            catalog.Diagnostics.Count(diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocRedirectAliasCollision));
    }

    [Fact]
    public void Create_ShouldUseCanonicalSlugAndRedirectAliases()
    {
        var catalog = CreateCatalog(
            new DocNode(
                "Intro",
                "docs/intro.md",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "start-here/intro",
                    RedirectAliases = ["legacy/intro", "docs/intro.md.html"]
                }));

        var canonical = catalog.ResolvePublicRoute("start-here/intro");
        var alias = catalog.ResolvePublicRoute("legacy/intro");
        var legacyAlias = catalog.ResolvePublicRoute("docs/intro.md.html");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, alias.Kind);
        Assert.Equal("start-here/intro", alias.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.AliasRedirect, legacyAlias.Kind);
        Assert.True(catalog.TryGetPublicRoutePath("legacy/intro", out var aliasRoutePath));
        Assert.Equal("start-here/intro", aliasRoutePath);
    }

    [Fact]
    public void Create_ShouldPreserveLiteralRedirectAliasRouteShape()
    {
        var catalog = CreateCatalog(
            new DocNode(
                "Intro",
                "docs/intro.md",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "start-here/intro",
                    RedirectAliases = ["Legacy_Path/Old_File.md.html"]
                }));

        var literalAlias = catalog.ResolvePublicRoute("Legacy_Path/Old_File.md.html");
        var slugifiedAlias = catalog.ResolvePublicRoute("legacy-path/old-file.md.html");

        Assert.Equal(DocRouteResolutionKind.AliasRedirect, literalAlias.Kind);
        Assert.Equal("start-here/intro", literalAlias.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.NotFound, slugifiedAlias.Kind);
    }

    [Fact]
    public void Create_ShouldLetRootReadmeFeedDocsHomeWithoutReservedCollisionDiagnostic()
    {
        var catalog = CreateCatalog(new DocNode("Home", "README.md", "<p>Home</p>"));

        Assert.True(catalog.TryGetPublicRoutePath("README.md", out var routePath));
        Assert.Equal(string.Empty, routePath);
        Assert.DoesNotContain(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocReservedRouteCollision);
    }

    [Fact]
    public void Create_ShouldEmitDiagnosticsAndKeepReservedRoutesNonPublic()
    {
        var catalog = CreateCatalog(new DocNode("Search", "search.md", "<p>Search</p>"));

        var resolution = catalog.ResolvePublicRoute("search");

        Assert.Equal(DocRouteResolutionKind.ReservedRoute, resolution.Kind);
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocReservedRouteCollision);
    }

    [Fact]
    public void Create_ShouldRejectDotDirectoryCanonicalSlugSegments()
    {
        var catalog = CreateCatalog(
            new DocNode(
                "Intro",
                "docs/intro.md",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "guides/../intro"
                }));

        var resolution = catalog.ResolvePublicRoute("guides/intro");

        Assert.Equal(DocRouteResolutionKind.NotFound, resolution.Kind);
        Assert.False(catalog.TryGetPublicRoutePath("docs/intro.md", out _));
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug);
    }

    [Fact]
    public void Create_ShouldNotLinkUnsafeCanonicalSlugAsRootFragment()
    {
        var catalog = CreateCatalog(
            new DocNode(
                "Intro",
                "docs/intro.md#overview",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "../intro"
                }));

        Assert.False(catalog.TryGetPublicRoutePath("docs/intro.md#overview", out _));
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug);
    }

    [Fact]
    public void Create_ShouldRejectDotDirectorySourceRouteSegments()
    {
        var catalog = CreateCatalog(new DocNode("Intro", "guides/../intro.md", "<p>Intro</p>"));

        var resolution = catalog.ResolvePublicRoute("guides/intro");

        Assert.Equal(DocRouteResolutionKind.NotFound, resolution.Kind);
        Assert.False(catalog.TryGetPublicRoutePath("guides/../intro.md", out _));
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug);
    }

    [Fact]
    public void Create_ShouldChooseDeterministicWinnerForDocRouteCollisions()
    {
        var catalog = CreateCatalog(
            new DocNode("First", "guides/first.md", "<p>First</p>", Metadata: new DocMetadata { CanonicalSlug = "guides/same" }),
            new DocNode("Second", "guides/second.md", "<p>Second</p>", Metadata: new DocMetadata { CanonicalSlug = "guides/same" }));

        var canonical = catalog.ResolvePublicRoute("guides/same");
        var loserSource = catalog.ResolvePublicRoute("guides/second.md");

        Assert.Equal("guides/first.md", canonical.SourcePath);
        Assert.Equal(DocRouteResolutionKind.CollisionLoser, loserSource.Kind);
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocRouteCollision);
    }

    [Fact]
    public void Create_ShouldFoldUnicodeAndUnsafeSegmentsDeterministically()
    {
        var catalog = CreateCatalog(new DocNode("Cafe", "Guides/Café_Intro.md", "<p>Cafe</p>"));

        var canonical = catalog.ResolvePublicRoute("guides/cafe-intro");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Contains(
            catalog.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.DocLossySlugNormalization);
    }

    private static DocRouteIdentityCatalog CreateCatalog(params DocNode[] docs)
    {
        return DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
    }
}
