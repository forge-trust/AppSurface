using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class DocModelsTests
{
    [Fact]
    public void DocNode_Properties_ShouldBeAccessible()
    {
        // Arrange
        var metadata = new DocMetadata
        {
            Title = "Metadata Title",
            Summary = "Summary",
            SummaryIsDerived = true,
            SequenceKey = "getting-started",
            Trust = new DocTrustMetadata
            {
                Status = "Unreleased",
                Migration = new DocTrustLink
                {
                    Label = "Read the upgrade policy",
                    Href = "/docs/releases/upgrade-policy.md.html"
                }
            },
            Contributor = new DocContributorMetadata
            {
                SourcePathOverride = "docs/guide.md",
                LastUpdatedOverride = new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero)
            },
            Localization = new DocLocalizationMetadata
            {
                Locale = "fr",
                TranslationKey = "guides/quickstart"
            },
            FeaturedPageGroups =
            [
                new DocFeaturedPageGroupDefinition
                {
                    Intent = "start",
                    Label = "Start",
                    Pages =
                    [
                        new DocFeaturedPageDefinition
                        {
                            Question = "What is this for?",
                            Path = "guides/intro.md"
                        }
                    ]
                }
            ],
            Aliases = ["alias-one"],
            RedirectAliases = ["legacy/alias"]
        };
        var node = new DocNode(
            "Title",
            "path/to/file",
            "content",
            Metadata: metadata,
            Outline:
            [
                new DocOutlineItem
                {
                    Title = "Install",
                    Id = "install",
                    Level = 2
                }
            ]);

        // Act & Assert
        Assert.Equal("Title", node.Title);
        Assert.Equal("path/to/file", node.Path);
        Assert.Equal("content", node.Content);
        Assert.False(node.IsDirectory);

        // This hits the ParentPath getter
        Assert.Null(node.ParentPath);
        Assert.Null(node.CanonicalPath);
        Assert.Equal("Metadata Title", node.Metadata?.Title);
        Assert.Equal("Summary", node.Metadata?.Summary);
        Assert.True(node.Metadata?.SummaryIsDerived);
        Assert.Equal("getting-started", node.Metadata?.SequenceKey);
        Assert.Equal("Unreleased", node.Metadata?.Trust?.Status);
        Assert.Equal("/docs/releases/upgrade-policy.md.html", node.Metadata?.Trust?.Migration?.Href);
        Assert.Equal("docs/guide.md", node.Metadata?.Contributor?.SourcePathOverride);
        Assert.Equal(new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero), node.Metadata?.Contributor?.LastUpdatedOverride);
        Assert.Equal("fr", node.Metadata?.Localization?.Locale);
        Assert.Equal("guides/quickstart", node.Metadata?.Localization?.TranslationKey);
        Assert.Single(node.Metadata?.FeaturedPageGroups!);
        Assert.Equal(["alias-one"], node.Metadata?.Aliases);
        Assert.Equal(["legacy/alias"], node.Metadata?.RedirectAliases);
        Assert.Equal("Install", Assert.Single(node.Outline!).Title);
    }

    [Fact]
    public void Merge_ShouldPreferPrimaryMetadataValues_AndFallbackWhenMissing()
    {
        var primary = new DocMetadata
        {
            Summary = "Primary",
            SummaryIsDerived = false,
            Aliases = ["alpha"],
            RedirectAliases = ["legacy/primary"],
            SequenceKey = "proof-path",
            HideFromSearch = true
        };
        var fallback = new DocMetadata
        {
            Title = "Fallback Title",
            Summary = "Fallback Summary",
            SummaryIsDerived = true,
            FeaturedPageGroups =
            [
                new DocFeaturedPageGroupDefinition
                {
                    Intent = "fallback",
                    Label = "Fallback",
                    Pages =
                    [
                        new DocFeaturedPageDefinition
                        {
                            Question = "Fallback question",
                            Path = "guides/fallback.md"
                        }
                    ]
                }
            ],
            Aliases = ["beta"],
            Keywords = ["keyword"],
            HideFromPublicNav = true
        };

        var merged = DocMetadata.Merge(primary, fallback);

        Assert.NotNull(merged);
        Assert.Equal("Fallback Title", merged!.Title);
        Assert.Equal("Primary", merged.Summary);
        Assert.False(merged.SummaryIsDerived);
        var featuredGroups = Assert.IsAssignableFrom<IReadOnlyList<DocFeaturedPageGroupDefinition>>(merged.FeaturedPageGroups);
        var featuredGroup = Assert.Single(featuredGroups);
        var featuredPage = Assert.Single(featuredGroup.Pages);
        Assert.Equal("Fallback question", featuredPage.Question);
        Assert.Equal(["alpha"], merged.Aliases);
        Assert.Equal(["legacy/primary"], merged.RedirectAliases);
        Assert.Equal("proof-path", merged.SequenceKey);
        Assert.Equal(["keyword"], merged.Keywords);
        Assert.True(merged.HideFromSearch);
        Assert.True(merged.HideFromPublicNav);
    }

    [Fact]
    public void Merge_ShouldTreatWhitespaceTitleAsMissing_AndKeepFallbackTitle()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Title = "   "
            },
            new DocMetadata
            {
                Title = "Fallback Title"
            });

        Assert.NotNull(merged);
        Assert.Equal("Fallback Title", merged!.Title);
    }

    [Fact]
    public void Merge_ShouldMergeTrustMetadata_FieldByField_AndPreserveExplicitEmptySources()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Status = "Unreleased",
                    Sources = Array.Empty<string>(),
                    Migration = new DocTrustLink
                    {
                        Label = "Upgrade guide"
                    }
                }
            },
            new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Summary = "This page is provisional until the tag is cut.",
                    Freshness = "Updated on main.",
                    ChangeScope = "Repository-wide.",
                    Archive = "Tagged release notes keep the durable record.",
                    Sources = ["CHANGELOG.md"],
                    Migration = new DocTrustLink
                    {
                        Label = "Read the upgrade policy",
                        Href = "/docs/releases/upgrade-policy.md.html"
                    }
                }
            });

        Assert.NotNull(merged);
        Assert.NotNull(merged!.Trust);
        Assert.Equal("Unreleased", merged.Trust!.Status);
        Assert.Equal("This page is provisional until the tag is cut.", merged.Trust.Summary);
        Assert.Equal("Updated on main.", merged.Trust.Freshness);
        Assert.Equal("Repository-wide.", merged.Trust.ChangeScope);
        Assert.Equal("Upgrade guide", merged.Trust.Migration?.Label);
        Assert.Equal("/docs/releases/upgrade-policy.md.html", merged.Trust.Migration?.Href);
        Assert.Equal("Tagged release notes keep the durable record.", merged.Trust.Archive);
        Assert.Empty(merged.Trust.Sources!);
    }

    [Fact]
    public void Merge_ShouldMergeContributorMetadata_FieldByField()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Contributor = new DocContributorMetadata
                {
                    HideContributorInfo = false,
                    SourceUrlOverride = "https://example.com/source"
                }
            },
            new DocMetadata
            {
                Contributor = new DocContributorMetadata
                {
                    SourcePathOverride = "docs/guide.md",
                    EditUrlOverride = "https://example.com/edit",
                    LastUpdatedOverride = new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero)
                }
            });

        Assert.NotNull(merged?.Contributor);
        Assert.False(merged!.Contributor!.HideContributorInfo);
        Assert.Equal("docs/guide.md", merged.Contributor.SourcePathOverride);
        Assert.Equal("https://example.com/source", merged.Contributor.SourceUrlOverride);
        Assert.Equal("https://example.com/edit", merged.Contributor.EditUrlOverride);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero),
            merged.Contributor.LastUpdatedOverride);
    }

    [Fact]
    public void Merge_ShouldMergeLocalizationMetadata_FieldByField()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Localization = new DocLocalizationMetadata
                {
                    Locale = "fr",
                    LocaleFallback = RazorDocsLocaleFallbackMode.Disabled
                }
            },
            new DocMetadata
            {
                Localization = new DocLocalizationMetadata
                {
                    TranslationKey = "guides/getting-started",
                    LocalizedTitle = "Getting started",
                    LocaleFallback = RazorDocsLocaleFallbackMode.DefaultLocaleWithNotice
                }
            });

        Assert.NotNull(merged?.Localization);
        Assert.Equal("fr", merged!.Localization!.Locale);
        Assert.Equal("guides/getting-started", merged.Localization.TranslationKey);
        Assert.Equal("Getting started", merged.Localization.LocalizedTitle);
        Assert.Equal(RazorDocsLocaleFallbackMode.Disabled, merged.Localization.LocaleFallback);
    }

    [Fact]
    public void Merge_ShouldReturnPrimaryContributorMetadata_WhenFallbackIsNull()
    {
        var primary = new DocContributorMetadata
        {
            HideContributorInfo = true,
            SourcePathOverride = "docs/guide.md"
        };

        var merged = DocContributorMetadata.Merge(primary, null);

        Assert.Same(primary, merged);
    }

    [Fact]
    public void Merge_ShouldKeepFlagStateAlignedWithTheSelectedValueSource()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Breadcrumbs = ["Start Here", "Quickstart"],
                PageTypeIsDerived = true
            },
            new DocMetadata
            {
                PageType = "api-reference",
                PageTypeIsDerived = false,
                Breadcrumbs = ["Namespaces", "Web"],
                BreadcrumbsMatchPathTargets = true
            });

        Assert.NotNull(merged);
        Assert.Equal("api-reference", merged!.PageType);
        Assert.False(merged.PageTypeIsDerived);
        Assert.Equal(["Start Here", "Quickstart"], merged.Breadcrumbs);
        Assert.Null(merged.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void Merge_ShouldTreatExplicitEmptyListsAsAuthoritative()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata
            {
                Aliases = Array.Empty<string>(),
                Breadcrumbs = Array.Empty<string>(),
                FeaturedPageGroups = Array.Empty<DocFeaturedPageGroupDefinition>()
            },
            new DocMetadata
            {
                Aliases = ["fallback-alias"],
                Breadcrumbs = ["Namespaces", "Web"],
                BreadcrumbsMatchPathTargets = true,
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "fallback",
                        Label = "Fallback",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Question = "Fallback question",
                                Path = "guides/fallback.md"
                            }
                        ]
                    }
                ]
            });

        Assert.NotNull(merged);
        Assert.Empty(merged!.Aliases!);
        Assert.Empty(merged.Breadcrumbs!);
        Assert.Empty(merged.FeaturedPageGroups!);
        Assert.Null(merged.BreadcrumbsMatchPathTargets);
    }

    [Fact]
    public void Merge_ShouldReturnPrimary_WhenFallbackIsNull()
    {
        var primary = new DocMetadata
        {
            Title = "Primary"
        };

        var merged = DocMetadata.Merge(primary, null);

        Assert.Same(primary, merged);
    }

    [Fact]
    public void DocTrustMetadataMerge_ShouldReturnFallback_WhenPrimaryIsNull()
    {
        var fallback = new DocTrustMetadata
        {
            Status = "Published"
        };

        var merged = DocTrustMetadata.Merge(null, fallback);

        Assert.Same(fallback, merged);
    }

    [Fact]
    public void DocTrustMetadataMerge_ShouldReturnPrimary_WhenFallbackIsNull()
    {
        var primary = new DocTrustMetadata
        {
            Status = "Published"
        };

        var merged = DocTrustMetadata.Merge(primary, null);

        Assert.Same(primary, merged);
    }

    [Fact]
    public void DocTrustMetadataMerge_ShouldTreatBlankFallbackValuesAsMissing()
    {
        var merged = DocTrustMetadata.Merge(
            new DocTrustMetadata
            {
                Summary = "   "
            },
            new DocTrustMetadata
            {
                Summary = "\t"
            });

        Assert.NotNull(merged);
        Assert.Null(merged!.Summary);
    }

    [Fact]
    public void DocTrustLinkMerge_ShouldReturnFallback_WhenPrimaryIsNull()
    {
        var fallback = new DocTrustLink
        {
            Label = "Upgrade guide"
        };

        var merged = DocTrustLink.Merge(null, fallback);

        Assert.Same(fallback, merged);
    }

    [Fact]
    public void DocTrustLinkMerge_ShouldReturnPrimary_WhenFallbackIsNull()
    {
        var primary = new DocTrustLink
        {
            Label = "Upgrade guide"
        };

        var merged = DocTrustLink.Merge(primary, null);

        Assert.Same(primary, merged);
    }

    [Fact]
    public void DocTrustLinkMerge_ShouldTreatBlankFallbackValuesAsMissing()
    {
        var merged = DocTrustLink.Merge(
            new DocTrustLink
            {
                Label = "   ",
                Href = " "
            },
            new DocTrustLink
            {
                Label = "\t",
                Href = "\n"
            });

        Assert.NotNull(merged);
        Assert.Null(merged!.Label);
        Assert.Null(merged.Href);
    }
}
