using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocRouteIdentityCatalogTests
{
    [Fact]
    public void Create_ShouldPublishMarkdownDocsAtCleanCanonicalRoutes()
    {
        var catalog = CreateCatalog(new DocNode("Evaluator", "start-here/appsurface-evaluator.md", "<p>Body</p>"));

        var canonical = catalog.ResolvePublicRoute("start-here/appsurface-evaluator");
        var source = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.md");
        var legacyHtml = catalog.ResolvePublicRoute("start-here/appsurface-evaluator.md.html");

        Assert.Equal(DocRouteResolutionKind.Canonical, canonical.Kind);
        Assert.Equal("start-here/appsurface-evaluator.md", canonical.SourcePath);
        Assert.Equal("start-here/appsurface-evaluator", canonical.PublicRoutePath);
        Assert.Equal(DocRouteResolutionKind.InternalSourceMatch, source.Kind);
        Assert.Equal(DocRouteResolutionKind.InternalSourceMatch, legacyHtml.Kind);
    }

    [Fact]
    public void Create_ShouldCollapseReadmeAndIndexMarkdownRoutes()
    {
        var catalog = CreateCatalog(
            new DocNode("Package", "packages/README.md", "<p>Package</p>"),
            new DocNode("Guide", "guides/index.md", "<p>Guide</p>"));

        Assert.Equal(DocRouteResolutionKind.Canonical, catalog.ResolvePublicRoute("packages").Kind);
        Assert.Equal("packages/README.md", catalog.ResolvePublicRoute("packages").SourcePath);
        Assert.Equal(DocRouteResolutionKind.Canonical, catalog.ResolvePublicRoute("guides").Kind);
        Assert.Equal("guides/index.md", catalog.ResolvePublicRoute("guides").SourcePath);
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
