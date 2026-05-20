using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class NamespaceEntryPointPanelRendererTests
{
    [Fact]
    public void Render_ShouldReturnOriginalContent_WhenEntryPointsAreMissing()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-type'>Type body</section>",
            outline: null,
            entryPoints: null);

        Assert.Equal("<section class='doc-type'>Type body</section>", result.Content);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Render_ShouldReturnOriginalContent_WhenAllEntryPointLabelsAreBlank()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-type'>Type body</section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = " " }
            ]);

        Assert.Equal("<section class='doc-type'>Type body</section>", result.Content);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Render_ShouldResolveOutlineAnchors_AndEncodeEntryText()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-type'>Type body</section>",
            [
                new DocOutlineItem { Id = " Outline&amp;Anchor ", Title = "Outline" },
                new DocOutlineItem { Id = " " }
            ],
            [
                new DocNamespaceEntryPoint
                {
                    Label = "<Use outline>",
                    Summary = "Jump & learn.",
                    Target = "Outline&Anchor"
                }
            ]);

        Assert.Contains("href=\"#Outline&amp;Anchor\"", result.Content);
        Assert.Contains("&lt;Use outline&gt;", result.Content);
        Assert.Contains("Jump &amp; learn.", result.Content);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Render_ShouldUseNamespaceScopedHeadingId()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section id='Known' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);

        var ariaPrefix = "aria-labelledby=\"common-entry-points-ForgeTrust-Web-";
        var headingPrefix = "<h2 id=\"common-entry-points-ForgeTrust-Web-";
        Assert.Contains(ariaPrefix, result.Content, StringComparison.Ordinal);
        Assert.Contains(headingPrefix, result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-labelledby=\"common-entry-points\"", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2 id=\"common-entry-points\">", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ShouldResolveGeneratedApiAnchors_FromAllowedElementsAndClasses()
    {
        var content = """
            <details id="KnownDetails" class="doc-property other"></details>
            <article class="doc-enum" id="KnownArticle"></article>
            <section id="Ignored" class="doc-summary"></section>
            """;

        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            content,
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Property", Target = "KnownDetails" },
                new DocNamespaceEntryPoint { Label = "Enum", Target = "KnownArticle" },
                new DocNamespaceEntryPoint { Label = "Ignored", Target = "Ignored" }
            ]);

        Assert.Contains("href=\"#KnownDetails\"", result.Content);
        Assert.Contains("href=\"#KnownArticle\"", result.Content);
        Assert.DoesNotContain("href=\"#Ignored\"", result.Content);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.NamespaceEntryPointTargetUnresolved
                          && diagnostic.Problem.Contains("Ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ShouldInsertAfterNamespaceGroups_WhenIntroIsMissing()
    {
        var content = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section id='Known' class='doc-type'></section>";

        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            content,
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);

        var groupsIndex = result.Content.IndexOf("doc-namespace-groups", StringComparison.Ordinal);
        var panelIndex = result.Content.IndexOf("doc-namespace-entry-points", StringComparison.Ordinal);
        var typeIndex = result.Content.IndexOf("doc-type", StringComparison.Ordinal);
        Assert.True(panelIndex > groupsIndex);
        Assert.True(typeIndex > panelIndex);
    }

    [Fact]
    public void Render_ShouldFallBackToGeneratedApiInsertion_WhenPreferredSectionsAreMalformed()
    {
        var noSectionStart = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "doc-namespace-intro<section id='Known' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);
        var missingClose = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-namespace-intro'><p>Intro</p><section id='Known' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);
        var malformedNestedOpen = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-namespace-intro'><section class='inner'</section><section id='Known' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);
        var missingCloseOnly = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-namespace-intro'><p>Intro",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Guide", Href = "/docs/guide" }
            ]);

        AssertPanelPrecedesGeneratedApi(noSectionStart.Content);
        AssertPanelPrecedesGeneratedApi(missingClose.Content);
        AssertPanelPrecedesGeneratedApi(malformedNestedOpen.Content);
        Assert.Contains("doc-namespace-entry-points", missingCloseOnly.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/guide\"", missingCloseOnly.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ShouldInsertAfterNestedIntroSection()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section class='doc-namespace-intro'><section><p>Nested</p></section><p>Intro</p></section><section id='Known' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);

        var introCloseIndex = result.Content.IndexOf("</section><section class=\"doc-namespace-entry-points\"", StringComparison.Ordinal);
        var panelIndex = result.Content.IndexOf("doc-namespace-entry-points", StringComparison.Ordinal);
        var typeIndex = result.Content.IndexOf("doc-type", StringComparison.Ordinal);
        Assert.True(introCloseIndex >= 0);
        Assert.True(panelIndex < typeIndex);
    }

    [Fact]
    public void Render_ShouldInsertBeforeGeneratedApiDetail_WhenIntroAndGroupsAreMissing()
    {
        var content = "<p>Lead</p><section id='Known' class='doc-method-group'></section>";

        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            content,
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);

        Assert.True(
            result.Content.IndexOf("doc-namespace-entry-points", StringComparison.Ordinal)
            < result.Content.IndexOf("doc-method-group", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("section", "doc-property")]
    [InlineData("details", "doc-property")]
    [InlineData("article", "doc-enum")]
    public void Render_ShouldInsertBeforeGeneratedApiPropertyAndEnumElements_WhenIntroAndGroupsAreMissing(
        string tag,
        string cssClass)
    {
        var content = $"<p>Lead</p><{tag} id='Known' class='{cssClass}'></{tag}>";

        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            content,
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Known", Target = "Known" }
            ]);

        Assert.True(
            result.Content.IndexOf("doc-namespace-entry-points", StringComparison.Ordinal)
            < result.Content.IndexOf(cssClass, StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ShouldAppendPanel_WhenNoInsertionPointExists()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<p>Lead</p>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Guide", Href = "/docs/guide" }
            ]);

        Assert.StartsWith("<p>Lead</p><section class=\"doc-namespace-entry-points\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/guide\"", result.Content);
    }

    [Fact]
    public void Render_ShouldOrderAuthoredEntries_ByOrderThenSourceIndex()
    {
        var result = NamespaceEntryPointPanelRenderer.Render(
            "ForgeTrust.Web",
            "<section id='First' class='doc-type'></section><section id='Second' class='doc-type'></section><section id='Third' class='doc-type'></section>",
            outline: null,
            entryPoints:
            [
                new DocNamespaceEntryPoint { Label = "Third", Target = "Third", SourceIndex = 2 },
                new DocNamespaceEntryPoint { Label = "Second", Target = "Second", Order = 1, SourceIndex = 1 },
                new DocNamespaceEntryPoint { Label = "First", Target = "First", Order = 1, SourceIndex = 0 }
            ]);

        var firstIndex = result.Content.IndexOf(">First<", StringComparison.Ordinal);
        var secondIndex = result.Content.IndexOf(">Second<", StringComparison.Ordinal);
        var thirdIndex = result.Content.IndexOf(">Third<", StringComparison.Ordinal);
        Assert.True(firstIndex < secondIndex);
        Assert.True(secondIndex < thirdIndex);
    }

    private static void AssertPanelPrecedesGeneratedApi(string content)
    {
        Assert.True(
            content.IndexOf("doc-namespace-entry-points", StringComparison.Ordinal)
            < content.IndexOf("doc-type", StringComparison.Ordinal));
    }
}
