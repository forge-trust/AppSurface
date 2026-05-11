using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocPublicSectionCatalogTests
{
    [Fact]
    public void DocPublicSection_ShouldPreservePublicNumericContract()
    {
        Assert.Equal(0, (int)DocPublicSection.StartHere);
        Assert.Equal(1, (int)DocPublicSection.Concepts);
        Assert.Equal(2, (int)DocPublicSection.HowToGuides);
        Assert.Equal(3, (int)DocPublicSection.Examples);
        Assert.Equal(4, (int)DocPublicSection.ApiReference);
        Assert.Equal(5, (int)DocPublicSection.Troubleshooting);
        Assert.Equal(6, (int)DocPublicSection.Internals);
        Assert.Equal(7, (int)DocPublicSection.Releases);
    }

    [Theory]
    [InlineData("API Reference", DocPublicSection.ApiReference)]
    [InlineData("api", DocPublicSection.ApiReference)]
    [InlineData("reference", DocPublicSection.ApiReference)]
    [InlineData("release-notes", DocPublicSection.Releases)]
    [InlineData("changelog", DocPublicSection.Releases)]
    [InlineData("start_here", DocPublicSection.StartHere)]
    public void TryResolve_ShouldAcceptCanonicalLabelsSlugsAndAliases(string value, DocPublicSection expectedSection)
    {
        var resolved = DocPublicSectionCatalog.TryResolve(value, out var section);

        Assert.True(resolved);
        Assert.Equal(expectedSection, section);
    }

    [Theory]
    [InlineData("api-reference", true, DocPublicSection.ApiReference)]
    [InlineData("API-REFERENCE", true, DocPublicSection.ApiReference)]
    [InlineData("releases", true, DocPublicSection.Releases)]
    [InlineData("api", false, default)]
    [InlineData("reference", false, default)]
    [InlineData("release-notes", false, default)]
    [InlineData("API Reference", false, default)]
    [InlineData("api_reference", false, default)]
    public void TryResolveSlug_ShouldAcceptOnlyCanonicalSectionSlugs(
        string slug,
        bool expectedResolved,
        DocPublicSection? expectedSection)
    {
        var resolved = DocPublicSectionCatalog.TryResolveSlug(slug, out var section);

        Assert.Equal(expectedResolved, resolved);
        Assert.Equal(expectedSection ?? default, section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveSlug_ShouldRejectBlankValues(string? slug)
    {
        var resolved = DocPublicSectionCatalog.TryResolveSlug(slug, out var section);

        Assert.False(resolved);
        Assert.Equal(default, section);
    }
}
