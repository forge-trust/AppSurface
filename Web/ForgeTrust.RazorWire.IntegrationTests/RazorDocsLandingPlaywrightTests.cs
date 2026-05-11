using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsLandingPlaywrightTests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsLandingPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Landing_UsesFeaturedPagesFromRootReadme()
    {
        // Regression: ISSUE-001 — the flagship /docs landing silently fell back to the neutral grid instead of promoting AppSurface.
        // Found by /qa on 2026-04-17
        // Report: .gstack/qa-reports/qa-report-localhost-5189-2026-04-17.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.DocsUrl);

        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var heading = await page.TextContentAsync("h1");
        Assert.Equal("AppSurface", heading?.Trim());
        Assert.DoesNotContain(
            "Welcome to the technical documentation.",
            await page.InnerTextAsync("body"));
        Assert.Equal("/docs/start-here/should-i-use-appsurface.md.html", await page.GetAttributeAsync("main a.group", "href"));

        await AssertFeaturedCardAsync(
            page,
            "/docs/start-here/should-i-use-appsurface.md.html",
            "Should my team use AppSurface?",
            "Should I Use AppSurface?",
            "GUIDE");

        await AssertFeaturedCardAsync(
            page,
            "/docs/start-here/appsurface-evaluator.md.html",
            "What is the evaluator path?",
            "Start Here for AppSurface Evaluators",
            "GUIDE");
        await AssertFeaturedCardAsync(
            page,
            "/docs/start-here/first-success-path.md.html",
            "Can I run it in minutes?",
            "First Success Path",
            "TUTORIAL");
        await AssertFeaturedCardAsync(
            page,
            "/docs/packages/README.md.html",
            "Which AppSurface package should I install first?",
            "AppSurface v0.1 package chooser",
            "GUIDE");
        await AssertFeaturedCardAsync(
            page,
            "/docs/releases/README.md.html",
            "What is shipping next, and how risky is the upgrade?",
            "Releases",
            "GUIDE");
        await AssertFeaturedCardAsync(
            page,
            "/docs/examples/web-app/README.md.html",
            "Show me a working app, not just abstractions",
            "web-app",
            "EXAMPLE");
        await AssertFeaturedCardAsync(
            page,
            "/docs/Aspire/README.md.html",
            "How does this fit distributed systems?",
            "Aspire",
            "GUIDE");
        await AssertFeaturedCardAsync(
            page,
            "/docs/Console/README.md.html",
            "What about CLI and worker flows?",
            "Console",
            "GUIDE");
    }

    [Fact]
    public async Task Landing_NavigatesToReleaseHub_AndUnreleasedProofArtifact()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.DocsUrl);
        await page.Locator("main a[href='/docs/releases/README.md.html']").First.ClickAsync();
        await WaitForPathAsync(page, "/docs/releases/README.md.html");
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal("Releases", (await page.TextContentAsync("h1"))?.Trim());
        Assert.Contains("Release contract", await page.InnerTextAsync(".docs-trust-bar"), StringComparison.OrdinalIgnoreCase);

        await page.Locator(".docs-content a[href='/docs/releases/unreleased.md.html']").First.ClickAsync();
        await WaitForPathAsync(page, "/docs/releases/unreleased.md.html");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('h1')?.textContent?.trim() === 'Unreleased'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal("Unreleased", (await page.TextContentAsync("h1"))?.Trim());
        var trustText = await page.InnerTextAsync(".docs-trust-bar");
        Assert.Contains("provisional", trustText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Repository-wide", trustText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailsPage_RendersContributorProvenanceStrip_AboveTrustBar()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/releases/unreleased.md.html");
        await page.WaitForSelectorAsync(".docs-provenance-strip", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForFunctionAsync(
            "() => { const time = document.querySelector('.docs-provenance-strip time'); return Boolean(time?.textContent) && !time.textContent.includes('UTC'); }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Contains("Source of truth", await page.InnerTextAsync(".docs-provenance-strip"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "https://github.com/forge-trust/AppSurface/blob/main/releases/unreleased.md",
            await page.GetAttributeAsync(".docs-provenance-strip a.docs-provenance-link--primary", "href"));
        Assert.Equal(
            "https://github.com/forge-trust/AppSurface/edit/main/releases/unreleased.md",
            await page.GetAttributeAsync(".docs-provenance-strip a.docs-provenance-link--secondary", "href"));
        Assert.Equal(
            "relative",
            await page.GetAttributeAsync(".docs-provenance-strip time", "data-rw-time-display"));

        var datetime = await page.GetAttributeAsync(".docs-provenance-strip time", "datetime");
        Assert.NotNull(datetime);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", datetime!);

        var provenancePrecedesTrustBar = await page.EvaluateAsync<bool>(
            "() => { const provenance = document.querySelector('.docs-provenance-strip'); const trust = document.querySelector('.docs-trust-bar'); return Boolean(provenance && trust) && (provenance.compareDocumentPosition(trust) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0; }");
        Assert.True(provenancePrecedesTrustBar);
    }

    private static async Task AssertFeaturedCardAsync(
        IPage page,
        string href,
        string question,
        string title,
        string badge)
    {
        var card = page.Locator($"main a[href='{href}']").First;
        await card.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var cardText = await card.InnerTextAsync();
        Assert.Contains(question, cardText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(title, cardText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(badge, cardText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open page", cardText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}
