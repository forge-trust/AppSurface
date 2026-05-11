using ForgeTrust.AppSurface.Docs.Services;
using YamlDotNet.Core;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class MarkdownFrontMatterParserTests
{
    [Fact]
    public void Extract_ShouldReturnNullMetadata_WhenMarkdownIsEmpty()
    {
        var (body, metadata) = MarkdownFrontMatterParser.Extract(string.Empty);

        Assert.Equal(string.Empty, body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldParseBlockScalars_AndQuotedInlineLists()
    {
        var markdown = """
            ---
            title: Quickstart
            summary: >
              Build forms, streams,
              and handlers together.
            aliases: ["forms, anti-forgery", "quickstart"]
            keywords:
              - forms
              - "agent docs"
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.Equal("Quickstart", metadata?.Title);
        Assert.Equal("Build forms, streams, and handlers together.", metadata?.Summary);
        Assert.Equal(["forms, anti-forgery", "quickstart"], metadata?.Aliases);
        Assert.Equal(["forms", "agent docs"], metadata?.Keywords);
    }

    [Fact]
    public void Extract_ShouldParseFeaturedPageGroups()
    {
        var markdown = """
            ---
            featured_page_groups:
              - intent: proof-path
                label: Proof path
                summary: Choose this when you need the spine.
                order: 5
                pages:
                  - question: How does composition work?
                    path: guides/composition.md
                    supporting_copy: Follow the service graph.
                    order: 20
                  - path: examples/hello-world.md
                    order: 30
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.NotNull(metadata);

        var group = Assert.Single(metadata!.FeaturedPageGroups!);
        Assert.Equal("proof-path", group.Intent);
        Assert.Equal("Proof path", group.Label);
        Assert.Equal("Choose this when you need the spine.", group.Summary);
        Assert.Equal(5, group.Order);
        Assert.Collection(
            group.Pages,
            first =>
            {
                Assert.Equal("How does composition work?", first.Question);
                Assert.Equal("guides/composition.md", first.Path);
                Assert.Equal("Follow the service graph.", first.SupportingCopy);
                Assert.Equal(20, first.Order);
            },
            second =>
            {
                Assert.Null(second.Question);
                Assert.Equal("examples/hello-world.md", second.Path);
                Assert.Null(second.SupportingCopy);
                Assert.Equal(30, second.Order);
            });
    }

    [Fact]
    public void Extract_ShouldIgnoreNullFeaturedPageGroupPageEntries()
    {
        var markdown = """
            ---
            featured_page_groups:
              - label: Start here
                pages:
                  - null
                  - question: Where do I start?
                    path: guides/intro.md
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        var group = Assert.Single(metadata!.FeaturedPageGroups!);
        Assert.Equal("start-here", group.Intent);
        Assert.Equal("Start here", group.Label);
        var featuredPage = Assert.Single(group.Pages);
        Assert.Equal("Where do I start?", featuredPage.Question);
        Assert.Equal("guides/intro.md", featuredPage.Path);
    }

    [Fact]
    public void Extract_ShouldIgnoreNullFeaturedPageGroupEntries()
    {
        var markdown = """
            ---
            featured_page_groups:
              - null
              - label: Start here
                pages:
                  - path: guides/intro.md
            ---
            # Hello
            """;

        var (_, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        var group = Assert.Single(metadata!.FeaturedPageGroups!);
        Assert.Equal("Start here", group.Label);
        Assert.Equal("guides/intro.md", Assert.Single(group.Pages).Path);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldIgnoreFeaturedPageGroupsWithEmptyPages_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - label: Empty group
                pages: []
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("empty-featured-group-pages", diagnostic.Code);
        Assert.Equal("featured_page_groups[0].pages", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldIgnoreNullFeaturedPageGroups_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - null
              - label: Start here
                pages:
                  - path: guides/intro.md
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        var group = Assert.Single(result.Metadata!.FeaturedPageGroups!);
        Assert.Equal("Start here", group.Label);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("null-featured-group", diagnostic.Code);
        Assert.Equal("featured_page_groups[0]", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldIgnoreFeaturedPageGroupsWhenAllPagesAreNull_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - label: Empty group
                pages:
                  - null
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("empty-featured-group-page-entries", diagnostic.Code);
        Assert.Equal("featured_page_groups[0].pages", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldIgnoreFeaturedPageGroupsWhenAllPagesNormalizeAway_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - label: Empty group
                pages:
                  - {}
                  - question: "   "
                    path: "   "
                    supporting_copy: "   "
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("empty-featured-group-page-entries", diagnostic.Code);
        Assert.Equal("featured_page_groups[0].pages", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldSkipFeaturedPageEntriesWithoutPath_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - label: Broken group
                pages:
                  - question: "Where should I go?"
                    supporting_copy: "This row has no destination."
                    order: 10
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        Assert.Collection(
            result.Diagnostics,
            missingPath =>
            {
                Assert.Equal("missing-featured-group-page-path", missingPath.Code);
                Assert.Equal("featured_page_groups[0].pages[0].path", missingPath.FieldPath);
            },
            emptyGroup =>
            {
                Assert.Equal("empty-featured-group-page-entries", emptyGroup.Code);
                Assert.Equal("featured_page_groups[0].pages", emptyGroup.FieldPath);
            });
    }

    [Theory]
    [InlineData("!!!", "featured", "!!!")]
    [InlineData("---", "featured", "---")]
    [InlineData("x", "x", "X")]
    [InlineData("agent_workflows", "agent-workflows", "Agent Workflows")]
    public void Extract_ShouldDeriveStableIntentAndLabel_WhenOnlyOneGroupIdentityIsAuthored(
        string authoredValue,
        string expectedIntent,
        string expectedLabel)
    {
        var markdown = $$"""
            ---
            featured_page_groups:
              - label: "{{authoredValue}}"
                pages:
                  - path: guides/intro.md
              - intent: "{{authoredValue}}"
                pages:
                  - path: guides/reference.md
            ---
            # Hello
            """;

        var (_, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Collection(
            metadata!.FeaturedPageGroups!,
            labelOnly =>
            {
                Assert.Equal(expectedIntent, labelOnly.Intent);
                Assert.Equal(authoredValue, labelOnly.Label);
            },
            intentOnly =>
            {
                Assert.Equal(authoredValue, intentOnly.Intent);
                Assert.Equal(expectedLabel, intentOnly.Label);
            });
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldIgnoreStaleFeaturedPages_AndWarn()
    {
        var markdown = """
            ---
            featured_pages:
              - question: Where do I start?
                path: guides/intro.md
            ---
            # Hello
            """;

        var (body, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Equal("# Hello", body);
        Assert.NotNull(result.Metadata);
        Assert.Null(result.Metadata!.FeaturedPageGroups);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("stale-featured-pages", diagnostic.Code);
        Assert.Equal("featured_pages", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldSkipGroupsWithoutIdentity_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - summary: Missing identity
                pages:
                  - path: guides/intro.md
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missing-featured-group-identity", diagnostic.Code);
        Assert.Equal("featured_page_groups[0]", diagnostic.FieldPath);
    }

    [Theory]
    [InlineData("              - label: Start here")]
    [InlineData("              - label: Start here\n                pages: null")]
    public void ExtractWithDiagnostics_ShouldSkipGroupsWithoutPages_AndWarn(string groupYaml)
    {
        var markdown = string.Join(
            "\n",
            "---",
            "featured_page_groups:",
            groupYaml,
            "---",
            "# Hello");

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missing-featured-group-pages", diagnostic.Code);
        Assert.Equal("featured_page_groups[0].pages", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldSkipFlatLookingGroups_AndWarn()
    {
        var markdown = """
            ---
            featured_page_groups:
              - question: Where do I start?
                path: guides/intro.md
            ---
            # Hello
            """;

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata!.FeaturedPageGroups!);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("flat-looking-featured-group", diagnostic.Code);
        Assert.Equal("featured_page_groups[0]", diagnostic.FieldPath);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldReturnInvalidYamlDiagnostic_WhenFrontMatterIsInvalid()
    {
        var markdown = """
            ---
            title: [
            summary: Broken
            ---
            # Hello
            """;

        var (body, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Equal(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), body);
        Assert.Null(result.Metadata);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("invalid-yaml", diagnostic.Code);
        Assert.Equal("$", diagnostic.FieldPath);
    }

    [Fact]
    public void ParseMetadataYamlWithDiagnostics_ShouldThrow_WhenYamlIsInvalid()
    {
        Assert.Throws<YamlException>(
            () => MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics("title: ["));
    }

    [Fact]
    public void Extract_ShouldParseTrustMetadata_AndPreserveExplicitEmptySourceLists()
    {
        var markdown = """
            ---
            trust:
              status: Unreleased
              summary: >
                This page is provisional until the tag is cut.
              freshness: Updated on main.
              change_scope: Repository-wide.
              migration:
                label: Read the upgrade policy
                href: /docs/releases/upgrade-policy.md.html
              archive: Tagged release notes keep the durable record.
              sources: []
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.NotNull(metadata);
        Assert.NotNull(metadata!.Trust);
        Assert.Equal("Unreleased", metadata.Trust!.Status);
        Assert.Equal("This page is provisional until the tag is cut.", metadata.Trust.Summary);
        Assert.Equal("Updated on main.", metadata.Trust.Freshness);
        Assert.Equal("Repository-wide.", metadata.Trust.ChangeScope);
        Assert.Equal("Read the upgrade policy", metadata.Trust.Migration?.Label);
        Assert.Equal("/docs/releases/upgrade-policy.md.html", metadata.Trust.Migration?.Href);
        Assert.Equal("Tagged release notes keep the durable record.", metadata.Trust.Archive);
        Assert.Empty(metadata.Trust.Sources!);
    }

    [Fact]
    public void Extract_ShouldParseContributorMetadata()
    {
        var markdown = """
            ---
            contributor:
              hide_contributor_info: false
              source_path_override: docs/releases/unreleased.md
              source_url_override: https://example.com/source
              edit_url_override: https://example.com/edit
              last_updated_override: 2026-04-22T23:19:00Z
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.NotNull(metadata?.Contributor);
        Assert.False(metadata!.Contributor!.HideContributorInfo);
        Assert.Equal("docs/releases/unreleased.md", metadata.Contributor.SourcePathOverride);
        Assert.Equal("https://example.com/source", metadata.Contributor.SourceUrlOverride);
        Assert.Equal("https://example.com/edit", metadata.Contributor.EditUrlOverride);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero),
            metadata.Contributor.LastUpdatedOverride);
    }

    [Fact]
    public void Extract_ShouldTreatEmptyContributorMetadataAsMissing()
    {
        var markdown = """
            ---
            contributor:
              source_path_override: "   "
              source_url_override: ""
              edit_url_override: "   "
            ---
            # Hello
            """;

        var (_, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.NotNull(metadata);
        Assert.Null(metadata!.Contributor);
    }

    [Fact]
    public void Extract_ShouldTreatBlankMigrationLinkFieldsAsMissing()
    {
        var markdown = """
            ---
            trust:
              status: Unreleased
              migration:
                label: "   "
                href: "   "
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.NotNull(metadata?.Trust);
        Assert.Equal("Unreleased", metadata!.Trust!.Status);
        Assert.Null(metadata.Trust.Migration);
    }

    [Fact]
    public void Extract_ShouldReturnOriginalMarkdown_WhenFrontMatterIsInvalid()
    {
        var markdown = """
            ---
            title: [
            summary: Broken
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldReturnOriginalMarkdown_WhenFrontMatterHasNoClosingMarker()
    {
        var markdown = """
            ---
            title: Quickstart
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldReturnNullMetadata_WhenFrontMatterDocumentIsYamlNull()
    {
        var markdown = """
            ---
            null
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal("# Hello", body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldReturnOriginalMarkdown_WhenOpeningMarkerIsNotFollowedByNewline()
    {
        var markdown = "---\rtitle: Quickstart\n---\n# Hello";

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldReturnNullMetadata_WhenFrontMatterDocumentIsEmpty()
    {
        var markdown = """
            ---
            ---
            # Hello
            """;

        var (body, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.Equal(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), body);
        Assert.Null(metadata);
    }

    [Fact]
    public void Extract_ShouldPreserveExplicitEmptyLists()
    {
        var markdown = """
            ---
            aliases: []
            breadcrumbs: []
            featured_page_groups: []
            related_pages: []
            ---
            # Hello
            """;

        var (_, metadata) = MarkdownFrontMatterParser.Extract(markdown);

        Assert.NotNull(metadata);
        Assert.Empty(metadata!.Aliases!);
        Assert.Empty(metadata.Breadcrumbs!);
        Assert.Empty(metadata.FeaturedPageGroups!);
        Assert.Empty(metadata.RelatedPages!);
    }

    [Fact]
    public void ParseMetadataYaml_ShouldThrowArgumentNullException_WhenYamlIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => MarkdownFrontMatterParser.ParseMetadataYaml(null!));
    }

    [Fact]
    public void ParseMetadataYaml_ShouldNormalizeSameSchema_AsInlineFrontMatter()
    {
        var yaml = """
            title: Quickstart
            summary: >
              Build forms, streams,
              and handlers together.
            sequence_key: onboarding
            featured_page_groups:
              - intent: start
                pages:
                  - question: Where do I start?
                    path: guides/intro.md
                    supporting_copy: Start with the intro guide.
                    order: 10
            """;

        var metadata = MarkdownFrontMatterParser.ParseMetadataYaml(yaml);

        Assert.NotNull(metadata);
        Assert.Equal("Quickstart", metadata!.Title);
        Assert.Equal("Build forms, streams, and handlers together.", metadata.Summary);
        Assert.Equal("onboarding", metadata.SequenceKey);
        var group = Assert.Single(metadata.FeaturedPageGroups!);
        Assert.Equal("start", group.Intent);
        Assert.Equal("Start", group.Label);
        var featuredPage = Assert.Single(group.Pages);
        Assert.Equal("Where do I start?", featuredPage.Question);
        Assert.Equal("guides/intro.md", featuredPage.Path);
        Assert.Equal("Start with the intro guide.", featuredPage.SupportingCopy);
        Assert.Equal(10, featuredPage.Order);
    }

    [Fact]
    public void ParseMetadataYaml_ShouldIgnoreEmptyTrustBlocks()
    {
        var metadata = MarkdownFrontMatterParser.ParseMetadataYaml(
            """
            trust: {}
            """);

        Assert.NotNull(metadata);
        Assert.Null(metadata!.Trust);
    }

    [Fact]
    public void ParseMetadataYaml_ShouldReturnNull_WhenDocumentIsYamlNull()
    {
        var metadata = MarkdownFrontMatterParser.ParseMetadataYaml("null");

        Assert.Null(metadata);
    }

    [Fact]
    public void ParseMetadataYaml_ShouldThrow_WhenYamlIsInvalid()
    {
        Assert.Throws<YamlException>(() => MarkdownFrontMatterParser.ParseMetadataYaml("title: ["));
    }
}
