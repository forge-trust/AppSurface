using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocOutlinePolicyTests
{
    [Fact]
    public void Apply_ShouldThrow_WhenOutlineIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => DocOutlinePolicy.Apply(null!, null));
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenOutlineIsEmpty()
    {
        var outline = Array.Empty<DocOutlineItem>();

        var result = DocOutlinePolicy.Apply(outline, null);

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenMaxHeadingLevelIncludesEveryEntry()
    {
        var outline = CreateOutline(
            H2("Install"),
            H3("Download"));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                Outline = new DocOutlineMetadata
                {
                    MaxHeadingLevel = 3
                }
            });

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldFilterToH2_WhenMaxHeadingLevelIsTwo()
    {
        var result = DocOutlinePolicy.Apply(
            CreateOutline(
                H2("Install"),
                H3("Download"),
                H2("Verify")),
            new DocMetadata
            {
                Outline = new DocOutlineMetadata
                {
                    MaxHeadingLevel = 2
                }
            });

        Assert.Equal(["Install", "Verify"], result.Select(item => item.Title));
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenRepeatedHeadingPolicyIncludes()
    {
        var outline = CreateOutline(
            H2("Install"),
            H3("Symptom"));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                Outline = new DocOutlineMetadata
                {
                    RepeatedHeadingPolicy = "include"
                }
            });

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldFilterToH2_WhenRepeatedHeadingPolicyIsH2Only()
    {
        var result = DocOutlinePolicy.Apply(
            CreateOutline(
                H2("Install"),
                H3("Symptom"),
                H2("Verify")),
            new DocMetadata
            {
                Outline = new DocOutlineMetadata
                {
                    RepeatedHeadingPolicy = "h2_only"
                }
            });

        Assert.Equal(["Install", "Verify"], result.Select(item => item.Title));
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenRepeatedHeadingPolicyIsUnknown()
    {
        var outline = CreateOutline(
            H2("Install"),
            H3("Symptom"));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                Outline = new DocOutlineMetadata
                {
                    RepeatedHeadingPolicy = "sometimes"
                }
            });

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenNoH3EntriesExist()
    {
        var outline = CreateOutline(
            H2("Install"),
            H2("Verify"));

        var result = DocOutlinePolicy.Apply(outline, null);

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenRepeatedTitlesDoNotMeetMinimumCount()
    {
        var outline = CreateOutline(
            H2("Login fails"),
            H3("Symptom"),
            H2("Build fails"),
            H3("Symptom"));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                PageType = "troubleshooting"
            });

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldIgnoreRepeatedH3BeforeFirstH2_WhenCountingParents()
    {
        var result = DocOutlinePolicy.Apply(
            CreateOutline(
                H3("Example"),
                H2("Login fails"),
                H3("Example"),
                H2("Build fails"),
                H3("Example"),
                H2("Deploy fails"),
                H3("Example"),
                H2("Rollback fails"),
                H3("Example")),
            new DocMetadata
            {
                PageType = "troubleshooting"
            });

        Assert.Equal(["Login fails", "Build fails", "Deploy fails", "Rollback fails"], result.Select(item => item.Title));
    }

    [Fact]
    public void Apply_ShouldIgnoreBlankH3Titles_WhenLookingForRepeatedHeadings()
    {
        var outline = CreateOutline(
            H2("Login fails"),
            H3(" "),
            H2("Build fails"),
            H3(" "));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                PageType = "troubleshooting"
            });

        Assert.Same(outline, result);
    }

    [Fact]
    public void Apply_ShouldSuppressSingleRepeatedTitle_WhenItRepeatsAcrossEnoughParents()
    {
        var result = DocOutlinePolicy.Apply(
            CreateOutline(
                H2("Login fails"),
                H3("Example"),
                H2("Build fails"),
                H3("Example"),
                H2("Deploy fails"),
                H3("Example"),
                H2("Rollback fails"),
                H3("Example")),
            new DocMetadata
            {
                PageType = "troubleshooting"
            });

        Assert.Equal(["Login fails", "Build fails", "Deploy fails", "Rollback fails"], result.Select(item => item.Title));
    }

    [Fact]
    public void Apply_ShouldUseParentIndexFallback_WhenHeadingIdsAreMissing()
    {
        var result = DocOutlinePolicy.Apply(
            CreateOutline(
                H2("Login fails", id: string.Empty),
                H3("Example"),
                H2("Build fails", id: string.Empty),
                H3("Example"),
                H2("Deploy fails", id: string.Empty),
                H3("Example"),
                H2("Rollback fails", id: string.Empty),
                H3("Example")),
            new DocMetadata
            {
                NavGroup = "Troubleshooting"
            });

        Assert.Equal(["Login fails", "Build fails", "Deploy fails", "Rollback fails"], result.Select(item => item.Title));
    }

    [Fact]
    public void Apply_ShouldReturnSameOutline_WhenRepeatedTitlesStayUnderOneParent()
    {
        var outline = CreateOutline(
            H2("Login fails"),
            H3("Symptom"),
            H3("Symptom"),
            H3("Cause"),
            H3("Cause"),
            H2("Build fails"));

        var result = DocOutlinePolicy.Apply(
            outline,
            new DocMetadata
            {
                PageType = "troubleshooting"
            });

        Assert.Same(outline, result);
    }

    private static IReadOnlyList<DocOutlineItem> CreateOutline(params DocOutlineItem[] items)
    {
        return items;
    }

    private static DocOutlineItem H2(string title, string? id = null)
    {
        return Heading(title, id, level: 2);
    }

    private static DocOutlineItem H3(string title, string? id = null)
    {
        return Heading(title, id, level: 3);
    }

    private static DocOutlineItem Heading(string title, string? id, int level)
    {
        return new DocOutlineItem
        {
            Title = title,
            Id = id ?? title.ToLowerInvariant().Replace(' ', '-'),
            Level = level
        };
    }
}
