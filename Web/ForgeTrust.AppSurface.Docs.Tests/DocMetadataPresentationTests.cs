using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocMetadataPresentationTests
{
    [Theory]
    [InlineData("guide", "guide", "Guide", "guide")]
    [InlineData(" example ", "example", "Example", "example")]
    [InlineData("api_reference", "api-reference", "API Reference", "api-reference")]
    [InlineData("internals", "internals", "Internals", "internals")]
    [InlineData("how to", "how-to", "How-To", "how-to")]
    [InlineData("start-here", "start-here", "Start Here", "start-here")]
    [InlineData("troubleshooting", "troubleshooting", "Troubleshooting", "troubleshooting")]
    [InlineData("glossary", "glossary", "Glossary", "glossary")]
    [InlineData("faq", "faq", "FAQ", "faq")]
    public void ResolvePageTypeBadge_ShouldNormalizeKnownValues(
        string rawValue,
        string expectedValue,
        string expectedLabel,
        string expectedVariant)
    {
        var badge = DocMetadataPresentation.ResolvePageTypeBadge(rawValue);

        Assert.NotNull(badge);
        Assert.Equal(expectedValue, badge!.Value);
        Assert.Equal(expectedLabel, badge.Label);
        Assert.Equal(expectedVariant, badge.Variant);
    }

    [Theory]
    [InlineData("api_surface", "api-surface", "API Surface")]
    [InlineData("custom_reference", "custom-reference", "Custom Reference")]
    [InlineData("cli_sdk", "cli-sdk", "CLI SDK")]
    [InlineData("faq_overview", "faq-overview", "FAQ Overview")]
    [InlineData("ui_ux", "ui-ux", "UI UX")]
    public void ResolvePageTypeBadge_ShouldFallbackToNeutralTitleCase_ForUnknownValues(
        string rawValue,
        string expectedValue,
        string expectedLabel)
    {
        var badge = DocMetadataPresentation.ResolvePageTypeBadge(rawValue);

        Assert.NotNull(badge);
        Assert.Equal(expectedValue, badge!.Value);
        Assert.Equal(expectedLabel, badge.Label);
        Assert.Equal("neutral", badge.Variant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("__")]
    public void ResolvePageTypeBadge_ShouldReturnNull_ForBlankValues(string? rawValue)
    {
        var badge = DocMetadataPresentation.ResolvePageTypeBadge(rawValue);

        Assert.Null(badge);
    }

    [Fact]
    public void NormalizeToken_ShouldReturnNull_WhenValueHasOnlySeparators()
    {
        var normalized = DocMetadataPresentation.NormalizeToken(" - _ \t ");

        Assert.Null(normalized);
    }
}
