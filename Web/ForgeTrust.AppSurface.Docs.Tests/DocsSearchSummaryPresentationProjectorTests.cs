using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class DocsSearchSummaryPresentationProjectorTests
{
    [Fact]
    public void Project_ShouldKeepOnlySafeInlineFormatting_AndFlattenBlocks()
    {
        var presentation = DocsSearchSummaryPresentationProjector.Project(
            "Use **strong** _emphasis_ and `code`. [Link text](https://example.test) ![Alt text](image.png)\n\n```csharp\nvar answer = 42;\n```");

        var children = Assert.IsAssignableFrom<IReadOnlyList<DocsSearchSummaryPresentationNode>>(presentation);
        Assert.Equal("Use strong emphasis and code. Link text Alt text var answer = 42;", string.Concat(children.SelectMany(Flatten)));
        Assert.Equal("strong", Assert.Single(children, node => node.Kind == "strong").Children![0].Text);
        Assert.Equal("emphasis", Assert.Single(children, node => node.Kind == "emphasis").Children![0].Text!.Trim());
        Assert.Equal("code", Assert.Single(children, node => node.Kind == "code").Text);
    }

    [Fact]
    public void Project_ShouldDropRawHtml_AndKeepTheLegacyTextSeparate()
    {
        var presentation = DocsSearchSummaryPresentationProjector.Project("Visible <script>alert('no')</script> <style>.hidden {}</style> <b>text</b>.");

        var json = JsonSerializer.Serialize(presentation);

        Assert.DoesNotContain("script", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visible", json, StringComparison.Ordinal);
        Assert.Contains("text", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ShouldDropUrlText_FromInlineAndCodeContent()
    {
        var presentation = DocsSearchSummaryPresentationProjector.Project(
            "Visit https://example.test and [documentation](https://docs.example.test). Use `https://code.example.test` and `token`.");

        var renderedText = string.Concat(Assert.IsAssignableFrom<IReadOnlyList<DocsSearchSummaryPresentationNode>>(presentation).SelectMany(Flatten));

        Assert.DoesNotContain("https://", renderedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visit", renderedText, StringComparison.Ordinal);
        Assert.Contains("documentation", renderedText, StringComparison.Ordinal);
        Assert.Contains("token", renderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ShouldBoundDepthNodesAndUnicodeScalars_Deterministically()
    {
        var nested = string.Concat(Enumerable.Repeat("**", 10)) + "deep" + string.Concat(Enumerable.Repeat("**", 10));
        var longSummary = nested + " " + new string('a', DocsSearchSummaryPresentationProjector.MaxScalars);

        var presentation = Assert.IsAssignableFrom<IReadOnlyList<DocsSearchSummaryPresentationNode>>(DocsSearchSummaryPresentationProjector.Project(longSummary));
        var flattened = presentation.SelectMany(Flatten).ToArray();

        Assert.True(presentation.Max(node => MaxDepth(node, depth: 1)) <= DocsSearchSummaryPresentationProjector.MaxDepth);
        Assert.True(presentation.Sum(CountNodes) <= DocsSearchSummaryPresentationProjector.MaxNodes);
        Assert.Equal(DocsSearchSummaryPresentationProjector.MaxScalars, flattened.Sum(text => text.EnumerateRunes().Count()));
        Assert.EndsWith("…", flattened.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ShouldBoundNodeCount_Deterministically()
    {
        var summary = string.Join(" ", Enumerable.Range(0, DocsSearchSummaryPresentationProjector.MaxNodes + 1).Select(index => $"`node-{index}`"));

        var presentation = Assert.IsAssignableFrom<IReadOnlyList<DocsSearchSummaryPresentationNode>>(DocsSearchSummaryPresentationProjector.Project(summary));

        Assert.True(presentation.Sum(CountNodes) <= DocsSearchSummaryPresentationProjector.MaxNodes);
        Assert.Contains(presentation.SelectMany(Flatten), text => text.Contains("node-0", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Flatten(DocsSearchSummaryPresentationNode node)
    {
        if (node.Text is not null)
        {
            yield return node.Text;
        }

        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                foreach (var text in Flatten(child))
                {
                    yield return text;
                }
            }
        }
    }

    private static int CountNodes(DocsSearchSummaryPresentationNode node)
    {
        return 1 + (node.Children?.Sum(CountNodes) ?? 0);
    }

    private static int MaxDepth(DocsSearchSummaryPresentationNode node, int depth)
    {
        return node.Children is null || node.Children.Count == 0
            ? depth
            : node.Children.Max(child => MaxDepth(child, depth + 1));
    }
}
