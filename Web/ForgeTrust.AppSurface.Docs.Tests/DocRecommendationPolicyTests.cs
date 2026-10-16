using System.Globalization;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocRecommendationPolicyTests
{
    [Theory]
    [InlineData("glossary", false, "task-guide,task-example,task-troubleshooting")]
    [InlineData("start-here", true, "task-guide,task-example,task-troubleshooting")]
    [InlineData("guide", false, "task-example,task-troubleshooting,task-guide")]
    [InlineData("how-to", false, "task-example,task-troubleshooting,task-guide")]
    [InlineData("example", false, "task-troubleshooting,task-guide,task-example")]
    [InlineData("troubleshooting", false, "task-guide,task-example,task-troubleshooting")]
    [InlineData("api-reference", false, "task-example,task-guide,task-troubleshooting")]
    [InlineData("javascript-method", false, "task-example,task-guide,task-troubleshooting")]
    public void Build_OrdersQualifiedTargetsByTheApprovedSourceRole(
        string sourceRole,
        bool sourceIsLanding,
        string expectedTargetIds)
    {
        var source = Page("source", sourceRole, component: "runtime",
            section: sourceIsLanding ? DocPublicSection.StartHere : null,
            isSectionLanding: sourceIsLanding);
        var pages = new[]
        {
            source,
            Page("task-guide", "guide", component: "runtime", autoRelatedPages: false),
            Page("task-example", "example", component: "runtime", autoRelatedPages: false),
            Page("task-troubleshooting", "troubleshooting", component: "runtime", autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(expectedTargetIds.Split(','), AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_UsesResolvedLandingAsTheLowestPriorityOrientationTarget()
    {
        var source = Page("source", "glossary", component: "runtime", section: DocPublicSection.Concepts);
        var guide = Page("guide-task", "guide", component: "runtime", autoRelatedPages: false);
        var example = Page("example-task", "example", component: "runtime", autoRelatedPages: false);
        var resolvedLanding = Page("resolved-landing", "troubleshooting", section: DocPublicSection.Concepts,
            isSectionLanding: true, metadataSectionLanding: false, autoRelatedPages: false);
        var losingLanding = Page("losing-landing", "troubleshooting", section: DocPublicSection.Concepts,
            metadataSectionLanding: true, autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, guide, example, resolvedLanding, losingLanding]);
        var rows = result.Pages["source"];

        Assert.Equal(new[] { "guide-task", "example-task", "resolved-landing" }, AutomaticIds(result, "source"));
        Assert.DoesNotContain(rows, row => row.TargetId == "losing-landing");
        Assert.Equal("section-overview", rows.Single(row => row.TargetId == "resolved-landing").PrimaryReasonCode);
    }

    [Fact]
    public void Build_OnlyAllowsExactAutomaticTargetRolesOrTheResolvedLanding()
    {
        var source = Page("source", "guide", component: "runtime");
        var pages = new[]
        {
            source,
            Page("valid-guide", "guide", component: "runtime", autoRelatedPages: false),
            Page("valid-example", "example", component: "runtime", autoRelatedPages: false),
            Page("valid-troubleshooting", "troubleshooting", component: "runtime", autoRelatedPages: false),
            Page("api-page", "api-reference", component: "runtime", autoRelatedPages: false),
            Page("how-to-page", "how-to", component: "runtime", autoRelatedPages: false),
            Page("glossary-page", "glossary", component: "runtime", autoRelatedPages: false),
            Page("unknown-page", "future-page-type", component: "runtime", autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(new[] { "valid-example", "valid-troubleshooting", "valid-guide" },
            AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_PreservesEveryAuthoredRowBeforeAtMostThreeAutomaticRows()
    {
        var authored = Enumerable.Range(0, 12).Select(index => Link($"curated-{index:D2}#part")).ToArray();
        var source = Page("source", "guide", component: "runtime", authored: authored);
        var pages = new[]
        {
            source,
            Page("automatic-a", "example", component: "runtime", autoRelatedPages: false),
            Page("automatic-b", "example", component: "runtime", autoRelatedPages: false),
            Page("automatic-c", "example", component: "runtime", autoRelatedPages: false),
            Page("automatic-d", "example", component: "runtime", autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);
        var rows = result.Pages["source"];

        Assert.Equal(15, rows.Count);
        Assert.Equal(authored.Select(link => link.TargetId), rows.Take(12).Select(row => row.TargetId));
        Assert.All(rows.Take(12), row =>
        {
            Assert.False(row.IsAutomatic);
            Assert.Equal("authored", row.PrimaryReasonCode);
            Assert.Equal(new[] { "authored" }, row.ReasonCodes);
            Assert.Equal("Selected by the author", row.ReasonText);
        });
        Assert.Equal(new[] { "automatic-a", "automatic-b", "automatic-c" },
            rows.Skip(12).Select(row => row.TargetId));
        Assert.Equal(3, result.Measurements.AutomaticResults);
    }

    [Fact]
    public void Build_SourceOptOutKeepsAuthoredRowsAndRemainsAnEligibleTarget()
    {
        var optedOut = Page("opted-out", "example", component: "runtime", autoRelatedPages: false,
            authored: [Link("curated#details")]);
        var consumer = Page("consumer", "guide", component: "runtime");

        var result = DocRecommendationPolicy.Build([optedOut, consumer]);

        Assert.Equal(new[] { "curated#details" }, result.Pages["opted-out"].Select(row => row.TargetId));
        Assert.Equal(new[] { "opted-out" }, AutomaticIds(result, "consumer"));
        Assert.Equal(2, result.Measurements.EligibleTargets);
        Assert.DoesNotContain("opted-out", result.Measurements.CandidateCounts.Keys);
    }

    [Theory]
    [InlineData("hide-search")]
    [InlineData("hide-navigation")]
    [InlineData("internal-role")]
    [InlineData("internals-role")]
    [InlineData("contributor-role")]
    [InlineData("internals-section")]
    [InlineData("fragment")]
    [InlineData("no-reader-content")]
    public void Build_ExcludesIneligibleTargetsFromAutomaticSuggestions(string exclusion)
    {
        var source = Page("source", "guide", component: "runtime");
        var candidate = exclusion switch
        {
            "hide-search" => Page("candidate", "guide", component: "runtime", hideFromSearch: true,
                autoRelatedPages: false),
            "hide-navigation" => Page("candidate", "guide", component: "runtime", hideFromPublicNav: true,
                autoRelatedPages: false),
            "internal-role" => Page("candidate", "internal", component: "runtime", autoRelatedPages: false),
            "internals-role" => Page("candidate", "internals", component: "runtime", autoRelatedPages: false),
            "contributor-role" => Page("candidate", "contributor", component: "runtime", autoRelatedPages: false),
            "internals-section" => Page("candidate", "guide", component: "runtime",
                section: DocPublicSection.Internals, autoRelatedPages: false),
            "fragment" => Page("candidate#member", "guide", component: "runtime", autoRelatedPages: false),
            "no-reader-content" => Page("candidate", "guide", component: "runtime", hasReaderContent: false,
                autoRelatedPages: false),
            _ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, "Unknown exclusion case.")
        };

        var result = DocRecommendationPolicy.Build([source, candidate]);

        Assert.Empty(AutomaticIds(result, "source"));
    }

    [Theory]
    [InlineData("hide-search")]
    [InlineData("hide-navigation")]
    [InlineData("internal-role")]
    [InlineData("internals-role")]
    [InlineData("contributor-role")]
    [InlineData("internals-section")]
    [InlineData("fragment")]
    [InlineData("no-reader-content")]
    [InlineData("source-opt-out")]
    public void Build_IneligibleSourcesRetainAuthoredWayfindingOnly(string exclusion)
    {
        var source = exclusion switch
        {
            "hide-search" => Page("source", "guide", component: "runtime", hideFromSearch: true),
            "hide-navigation" => Page("source", "guide", component: "runtime", hideFromPublicNav: true),
            "internal-role" => Page("source", "internal", component: "runtime"),
            "internals-role" => Page("source", "internals", component: "runtime"),
            "contributor-role" => Page("source", "contributor", component: "runtime"),
            "internals-section" => Page("source", "guide", component: "runtime",
                section: DocPublicSection.Internals),
            "fragment" => Page("source#details", "guide", component: "runtime"),
            "no-reader-content" => Page("source", "guide", component: "runtime", hasReaderContent: false),
            "source-opt-out" => Page("source", "guide", component: "runtime", autoRelatedPages: false),
            _ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, "Unknown exclusion case.")
        } with
        { Authored = [Link("curated#section")] };
        var candidate = Page("candidate", "example", component: "runtime", autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, candidate]);
        var rows = result.Pages[source.TargetId];

        Assert.Single(rows);
        Assert.Equal("curated#section", rows[0].TargetId);
        Assert.False(rows[0].IsAutomatic);
    }

    [Fact]
    public void Build_DoesNotTreatContributorProvenanceAloneAsInternalContent()
    {
        var source = Page("source", "guide", component: "runtime");
        var documentedContributor = Page("candidate", "example", component: "runtime", autoRelatedPages: false,
            contributor: new DocContributorMetadata { SourcePathOverride = "docs/examples/candidate.md" });

        var result = DocRecommendationPolicy.Build([source, documentedContributor]);

        Assert.Equal(new[] { "candidate" }, AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_NormalizesAndUnionsAliasesAndKeywordsWithoutLosingPunctuation()
    {
        var source = Page("source", "guide",
            aliases: ["  C#  ", "Acme.Widget", "  installer\t  flow ", "C#", " ", "Acme.Widget"],
            keywords: ["c#", "Installer flow"]);
        var target = Page("target", "example", autoRelatedPages: false,
            aliases: [" c# ", "Acme.Widget"], keywords: ["installer   flow", "C#"]);
        var oneSharedTerm = Page("one-term", "example", autoRelatedPages: false,
            aliases: ["c#", "unrelated"]);

        var result = DocRecommendationPolicy.Build([source, target, oneSharedTerm]);

        Assert.Equal(new[] { "target" }, AutomaticIds(result, "source"));
        Assert.Equal(new[] { "try-example", "shared-topics" },
            result.Pages["source"].Single(row => row.TargetId == "target").ReasonCodes);
    }

    [Fact]
    public void Build_DoesNotStemTermsOrRewritePunctuation()
    {
        var source = Page("source", "guide", aliases: ["deployment", "C#", "Acme.Widget"]);
        var target = Page("target", "example", autoRelatedPages: false,
            keywords: ["deployments", "C", "Acme.Widget.Method"]);

        var result = DocRecommendationPolicy.Build([source, target]);

        Assert.Empty(AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_IgnoresDerivedComponentsButRetainsFalseAndNullCustomMetadataMarkers()
    {
        var derivedSource = Page("derived-source", "guide", component: "runtime", componentIsDerived: true);
        var derivedTarget = Page("derived-target", "example", component: "runtime", componentIsDerived: false,
            autoRelatedPages: false);
        var ignored = DocRecommendationPolicy.Build([derivedSource, derivedTarget]);
        Assert.Empty(AutomaticIds(ignored, "derived-source"));

        var falseMarkerSource = Page("false-source", "guide", component: "runtime", componentIsDerived: false);
        var nullMarkerTarget = Page("null-target", "example", component: "runtime", componentIsDerived: null,
            autoRelatedPages: false);
        var authoredMarkers = DocRecommendationPolicy.Build([falseMarkerSource, nullMarkerTarget]);

        Assert.Equal(new[] { "null-target" }, AutomaticIds(authoredMarkers, "false-source"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(null, false, true)]
    public void Build_UsesOnlyNonDerivedAudienceAsSupportingEvidence(
        bool? sourceAudienceIsDerived,
        bool? targetAudienceIsDerived,
        bool expectsSameAudienceEvidence)
    {
        var source = Page("source", "guide", component: "runtime", audience: "Operators",
            audienceIsDerived: sourceAudienceIsDerived);
        var target = Page("target", "example", component: "runtime", audience: " operators ",
            audienceIsDerived: targetAudienceIsDerived, autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, target]);
        var row = Assert.Single(result.Pages["source"], item => item.TargetId == "target");

        Assert.Equal(expectsSameAudienceEvidence, row.ReasonCodes.Contains("same-audience", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(12, 4, true)]
    [InlineData(20, 5, true)]
    [InlineData(19, 5, false)]
    public void Build_AppliesBothCommonTermFrequencyBoundaries(
        int eligibleTargetCount,
        int termFrequency,
        bool remainsInformative)
    {
        var pages = TopicFrequencyCorpus(eligibleTargetCount, termFrequency, previousTarget: null);

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(eligibleTargetCount, result.Measurements.EligibleTargets);
        IEnumerable<string> expected = remainsInformative
            ? new[] { "candidate" }
                .Concat(Enumerable.Range(0, termFrequency - 2).Select(index => $"shared-{index}"))
                .Take(3)
            : Array.Empty<string>();
        Assert.Equal(expected, AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_ComputesCommonTermsFromAllEligibleTargetsBeforeOwnerExclusions()
    {
        var pages = TopicFrequencyCorpus(19, 5, previousTarget: "previous#part");

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(19, result.Measurements.EligibleTargets);
        Assert.Empty(AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_RequiresComponentOrTwoTopicsRatherThanAudienceSectionSequenceOrRoleAlone()
    {
        var source = Page("source", "guide", component: "runtime", audience: "operators",
            section: DocPublicSection.Concepts, sequence: "setup-path", order: 1, aliases: ["alpha", "beta"]);
        var pages = new[]
        {
            source,
            Page("one-topic-plus-context", "example", audience: "operators", section: DocPublicSection.Concepts,
                sequence: "setup-path", order: 2, aliases: ["alpha"], autoRelatedPages: false),
            Page("sequence-only", "example", audience: "operators", sequence: "setup-path", order: 3,
                autoRelatedPages: false),
            Page("same-section-only", "example", section: DocPublicSection.Concepts, autoRelatedPages: false),
            Page("same-role-only", "guide", autoRelatedPages: false),
            Page("component-match", "troubleshooting", component: "runtime", autoRelatedPages: false),
            Page("two-topics", "example", aliases: ["alpha", "beta"], autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(new[] { "two-topics", "component-match" }, AutomaticIds(result, "source"));
        Assert.DoesNotContain(AutomaticIds(result, "source"), id => id is "one-topic-plus-context" or "sequence-only"
            or "same-section-only" or "same-role-only");
    }

    [Fact]
    public void Build_RequiresStrictlyForwardOrderOnlyWhenBothPagesHaveTheSameExplicitSequence()
    {
        var source = Page("source", "guide", component: "runtime", sequence: "walkthrough", order: 2);
        var pages = new[]
        {
            source,
            Page("earlier", "example", component: "runtime", sequence: "walkthrough", order: 1,
                autoRelatedPages: false),
            Page("equal", "example", component: "runtime", sequence: "walkthrough", order: 2,
                autoRelatedPages: false),
            Page("later", "example", component: "runtime", sequence: "walkthrough", order: 3,
                autoRelatedPages: false),
            Page("missing-order", "example", component: "runtime", sequence: "walkthrough",
                autoRelatedPages: false),
            Page("different-sequence", "example", component: "runtime", sequence: "other", order: 99,
                autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(new[] { "later", "different-sequence", "missing-order" }, AutomaticIds(result, "source"));
        Assert.Contains("shared-sequence", result.Pages["source"].Single(row => row.TargetId == "later").ReasonCodes);
        Assert.DoesNotContain("shared-sequence",
            result.Pages["source"].Single(row => row.TargetId == "missing-order").ReasonCodes);
    }

    [Fact]
    public void Build_UnorderedSourceShouldNotExcludeOrderedTargetsInItsSequence()
    {
        var source = Page("source", "guide", component: "runtime", sequence: "walkthrough");
        var earlier = Page("earlier", "example", component: "runtime", sequence: "walkthrough", order: 1,
            autoRelatedPages: false);
        var later = Page("later", "example", component: "runtime", sequence: "walkthrough", order: 2,
            autoRelatedPages: false);

        var rows = DocRecommendationPolicy.Build([source, later, earlier]).Pages["source"];

        Assert.Equal(["earlier", "later"], rows.Select(row => row.TargetId));
        Assert.All(rows, row => Assert.DoesNotContain("shared-sequence", row.ReasonCodes));
    }

    [Fact]
    public void Build_UsesSharedComponentBeforeTopicCountAndCapsTopicEvidenceAtThree()
    {
        var componentSource = Page("component-source", "guide", component: "runtime",
            aliases: ["one", "two", "three"]);
        var componentTarget = Page("component-target", "example", component: "runtime", autoRelatedPages: false);
        var manyTopics = Page("many-topics", "example", component: "different",
            aliases: ["one", "two", "three"], autoRelatedPages: false);
        var pages = new[] { componentSource, componentTarget, manyTopics };

        var componentResult = DocRecommendationPolicy.Build(pages);

        Assert.Equal(new[] { "component-target", "many-topics" }, AutomaticIds(componentResult, "component-source"));

        var topicSource = Page("topic-source", "guide", aliases: ["one", "two", "three", "four"]);
        var fourTopics = Page("z-four-topics", "example", aliases: ["one", "two", "three", "four"],
            autoRelatedPages: false);
        var threeTopics = Page("a-three-topics", "example", aliases: ["one", "two", "three"],
            autoRelatedPages: false);
        var twoTopics = Page("m-two-topics", "example", aliases: ["one", "two"], autoRelatedPages: false);

        var cappedResult = DocRecommendationPolicy.Build([topicSource, fourTopics, threeTopics, twoTopics]);

        Assert.Equal(new[] { "a-three-topics", "z-four-topics", "m-two-topics" },
            AutomaticIds(cappedResult, "topic-source"));
    }

    [Fact]
    public void Build_OrdersSameAudienceAndSectionAfterEarlierEvidenceAndBeforeTitle()
    {
        var source = Page("source", "guide", component: "runtime", audience: "operators",
            section: DocPublicSection.Concepts);
        var sameAudience = Page("z-same-audience", "example", component: "runtime", audience: "OPERATORS",
            autoRelatedPages: false);
        var differentAudience = Page("a-different-audience", "example", component: "runtime", audience: "authors",
            autoRelatedPages: false);
        var audienceResult = DocRecommendationPolicy.Build([source, differentAudience, sameAudience]);
        Assert.Equal(new[] { "z-same-audience", "a-different-audience" }, AutomaticIds(audienceResult, "source"));

        var sameSection = Page("z-same-section", "example", component: "runtime",
            section: DocPublicSection.Concepts, autoRelatedPages: false);
        var differentSection = Page("a-different-section", "example", component: "runtime",
            section: DocPublicSection.Troubleshooting, autoRelatedPages: false);
        var sectionResult = DocRecommendationPolicy.Build([source, differentSection, sameSection]);
        Assert.Equal(new[] { "z-same-section", "a-different-section" }, AutomaticIds(sectionResult, "source"));
    }

    [Fact]
    public void Build_OrdersForwardSequencePeersBeforeTheTitleTieBreak()
    {
        var source = Page("source", "guide", component: "runtime", sequence: "proof", order: 1);
        var later = Page("z-later", "example", component: "runtime", sequence: "proof", order: 3,
            autoRelatedPages: false);
        var earlierForward = Page("a-earlier-forward", "example", component: "runtime", sequence: "proof", order: 2,
            autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, later, earlierForward]);

        Assert.Equal(new[] { "a-earlier-forward", "z-later" }, AutomaticIds(result, "source"));
    }

    [Fact]
    public void Build_UsesCultureIndependentTitleAndCanonicalIdentityTiesAcrossInputPermutations()
    {
        var source = Page("source", "guide", component: "runtime");
        var byTitle = Page("z-page", "example", component: "runtime", title: "Alpha", autoRelatedPages: false);
        var lowercaseIdentity = Page("a-page", "example", component: "runtime", title: "i", autoRelatedPages: false);
        var uppercaseIdentity = Page("B-page", "example", component: "runtime", title: "I", autoRelatedPages: false);
        var pages = new[] { source, uppercaseIdentity, byTitle, lowercaseIdentity };
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantOrder = AutomaticIds(DocRecommendationPolicy.Build(pages), "source");

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var permutedOrder = AutomaticIds(DocRecommendationPolicy.Build(pages.Reverse().ToArray()), "source");

            Assert.Equal(new[] { "z-page", "a-page", "B-page" }, invariantOrder);
            Assert.Equal(invariantOrder, permutedOrder);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Build_SuppressesCurrentPreviousNextAndAuthoredOwnersEvenWhenLinksHaveFragments()
    {
        var source = Page("source", "guide", component: "runtime", previousTargetId: "previous#part",
            nextTargetId: "next#part", authored: [Link("curated#section")]);
        var previous = Page("previous", "example", component: "runtime", autoRelatedPages: false);
        var next = Page("next", "example", component: "runtime", autoRelatedPages: false);
        var curatedOwner = Page("curated", "example", component: "runtime", autoRelatedPages: false);
        var recommendation = Page("recommendation", "example", component: "runtime", autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, previous, next, curatedOwner, recommendation]);

        Assert.Equal(new[] { "curated#section", "recommendation" },
            result.Pages["source"].Select(row => row.TargetId));
        Assert.Equal(new[] { "authored", "try-example" },
            result.Pages["source"].Select(row => row.PrimaryReasonCode));
    }

    [Fact]
    public void Build_ResetsOwnerExclusionsForEachSourceAndKeepsAuthoredRowsUncapped()
    {
        var authoredRows = Enumerable.Range(0, 5).Select(index => Link($"curated-{index:D2}#section")).ToArray();
        var pages = new[]
        {
            Page("source-previous", "guide", component: "runtime", previousTargetId: "alpha#part"),
            Page("source-next", "guide", component: "runtime", nextTargetId: "beta#part"),
            Page("source-authored", "guide", component: "runtime", authored: authoredRows),
            Page("alpha", "example", component: "runtime", autoRelatedPages: false),
            Page("beta", "example", component: "runtime", autoRelatedPages: false),
            Page("gamma", "example", component: "runtime", autoRelatedPages: false),
            Page("delta", "example", component: "runtime", autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Collection(result.Pages["source-previous"],
            row => AssertRecommendation(row, "beta"),
            row => AssertRecommendation(row, "delta"),
            row => AssertRecommendation(row, "gamma"));
        Assert.Collection(result.Pages["source-next"],
            row => AssertRecommendation(row, "alpha"),
            row => AssertRecommendation(row, "delta"),
            row => AssertRecommendation(row, "gamma"));
        Assert.Equal(authoredRows.Select(link => link.TargetId)
                .Concat(new[] { "alpha", "beta", "delta" }),
            result.Pages["source-authored"].Select(row => row.TargetId));
        Assert.All(result.Pages["source-authored"].Take(authoredRows.Length), row =>
        {
            Assert.False(row.IsAutomatic);
            Assert.Equal("authored", row.PrimaryReasonCode);
            Assert.Equal(new[] { "authored" }, row.ReasonCodes);
            Assert.Equal("Selected by the author", row.ReasonText);
        });
        Assert.Collection(result.Pages["source-authored"].Skip(authoredRows.Length),
            row => AssertRecommendation(row, "alpha"),
            row => AssertRecommendation(row, "beta"),
            row => AssertRecommendation(row, "delta"));
    }

    private static void AssertRecommendation(DocRecommendation row, string targetId)
    {
        Assert.Equal(targetId, row.TargetId);
        Assert.True(row.IsAutomatic);
        Assert.Equal("try-example", row.PrimaryReasonCode);
        Assert.Equal(new[] { "try-example", "shared-component" }, row.ReasonCodes);
        Assert.Equal("Try an example for this component", row.ReasonText);
    }

    [Fact]
    public void Build_ReportsCandidateMeasurementsBeforeSourceSpecificExclusions()
    {
        var source = Page("source", "guide", component: "runtime", previousTargetId: "previous#part",
            nextTargetId: "next#part", authored: [Link("curated#section")]);
        var pages = new[]
        {
            source,
            Page("previous", "example", component: "runtime", autoRelatedPages: false),
            Page("next", "example", component: "runtime", autoRelatedPages: false),
            Page("curated", "example", component: "runtime", autoRelatedPages: false),
            Page("recommendation", "example", component: "runtime", autoRelatedPages: false)
        };

        var result = DocRecommendationPolicy.Build(pages);

        Assert.Equal(5, result.Measurements.EligibleTargets);
        Assert.Equal(5, result.Measurements.CandidatesEvaluated);
        Assert.Equal(5, result.Measurements.CandidateCounts["source"]);
        Assert.Equal(1, result.Measurements.AutomaticResults);
        Assert.Equal(new[] { "recommendation" }, AutomaticIds(result, "source"));
    }

    [Theory]
    [InlineData("example", "try-example", false, "Try an example for this topic")]
    [InlineData("example", "try-example", true, "Try an example for this component")]
    [InlineData("troubleshooting", "troubleshoot", false, "Troubleshoot this topic")]
    [InlineData("troubleshooting", "troubleshoot", true, "Troubleshoot this component")]
    [InlineData("guide", "deepen-guide", false, "Go deeper on this topic")]
    [InlineData("guide", "deepen-guide", true, "Go deeper on this component")]
    public void Build_MapsPrimaryReasonAndCaptionFromTargetRoleAndEvidence(
        string targetRole,
        string expectedPrimaryReason,
        bool sharesComponent,
        string expectedCaption)
    {
        var component = sharesComponent ? "runtime" : null;
        var source = Page("source", "guide", component: component, aliases: ["first", "second"]);
        var target = Page("target", targetRole, component: component, aliases: ["first", "second"],
            autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, target]);
        var row = Assert.Single(result.Pages["source"], item => item.TargetId == "target");

        Assert.Equal(expectedPrimaryReason, row.PrimaryReasonCode);
        Assert.Equal(expectedCaption, row.ReasonText);
        Assert.Equal(sharesComponent
                ? new[] { expectedPrimaryReason, "shared-component", "shared-topics" }
                : new[] { expectedPrimaryReason, "shared-topics" },
            row.ReasonCodes);
    }

    [Fact]
    public void Build_EmitsPrimaryReasonOnceThenOnlyPresentEvidenceInTheFixedOrder()
    {
        var source = Page("source", "guide", component: "runtime", audience: "operators",
            section: DocPublicSection.Concepts, sequence: "proof", order: 1, aliases: ["one", "two"]);
        var target = Page("target", "example", component: "runtime", audience: "OPERATORS",
            section: DocPublicSection.Concepts, sequence: "proof", order: 2, aliases: ["ONE", "two"],
            autoRelatedPages: false);

        var result = DocRecommendationPolicy.Build([source, target]);
        var row = Assert.Single(result.Pages["source"], item => item.TargetId == "target");

        Assert.Equal("try-example", row.PrimaryReasonCode);
        Assert.Equal(new[]
        {
            "try-example", "shared-component", "shared-topics", "shared-sequence", "same-audience", "same-section"
        }, row.ReasonCodes);
        Assert.Equal(1, row.ReasonCodes.Count(code => code == row.PrimaryReasonCode));
        Assert.Equal("Try an example for this component", row.ReasonText);
    }

    [Fact]
    public void Build_UsesSectionCaptionForLandingAndGenericCaptionForUnknownReasonCodes()
    {
        var source = Page("source", "guide", section: DocPublicSection.Concepts);
        var landing = Page("landing", "example", section: DocPublicSection.Concepts, isSectionLanding: true,
            autoRelatedPages: false);
        var result = DocRecommendationPolicy.Build([source, landing]);
        var row = Assert.Single(result.Pages["source"], item => item.TargetId == "landing");

        Assert.Equal("section-overview", row.PrimaryReasonCode);
        Assert.Equal(new[] { "section-overview", "same-section" }, row.ReasonCodes);
        Assert.Equal("Explore this section", row.ReasonText);
        Assert.Equal("Read this page next", DocRecommendationPolicy.ReasonText("future-reason"));
    }

    [Fact]
    public void Build_ProducesNoAutomaticRowsForAnEmptyOrUnrelatedCorpus()
    {
        Assert.Empty(DocRecommendationPolicy.Build([]).Pages);

        var source = Page("source", "guide");
        var unrelated = Page("unrelated", "example", autoRelatedPages: false);
        var result = DocRecommendationPolicy.Build([source, unrelated]);

        Assert.Empty(AutomaticIds(result, "source"));
        Assert.Empty(result.Pages["source"]);
        Assert.Equal(2, result.Measurements.EligibleTargets);
        Assert.Equal(0, result.Measurements.CandidatesEvaluated);
        Assert.Equal(0, result.Measurements.AutomaticResults);
    }

    private static IReadOnlyList<DocRecommendationPageFacts> TopicFrequencyCorpus(
        int eligibleTargetCount,
        int sharedTermFrequency,
        string? previousTarget)
    {
        var source = Page("source", "guide", aliases: ["first-term", "second-term"],
            previousTargetId: previousTarget);
        var candidate = Page("candidate", "example", aliases: ["first-term", "second-term"],
            autoRelatedPages: false);
        var pages = new List<DocRecommendationPageFacts> { source, candidate };
        var sharedPages = sharedTermFrequency - pages.Count;

        if (previousTarget is not null)
        {
            pages.Add(Page("previous", "guide", aliases: ["first-term", "second-term"],
                autoRelatedPages: false));
            sharedPages--;
        }

        for (var index = 0; index < sharedPages; index++)
        {
            pages.Add(Page($"shared-{index}", "guide", aliases: ["first-term", "second-term"],
                autoRelatedPages: false));
        }

        while (pages.Count < eligibleTargetCount)
        {
            pages.Add(Page($"neutral-{pages.Count}", "guide", autoRelatedPages: false));
        }

        return pages;
    }

    private static IReadOnlyList<string> AutomaticIds(DocRecommendationPolicyResult result, string sourceId) =>
        result.Pages[sourceId].Where(row => row.IsAutomatic).Select(row => row.TargetId).ToArray();

    private static DocRecommendationPageFacts Page(
        string id,
        string? pageType = "guide",
        string? component = null,
        IReadOnlyList<string>? aliases = null,
        IReadOnlyList<string>? keywords = null,
        string? audience = null,
        DocPublicSection? section = null,
        bool isSectionLanding = false,
        bool? metadataSectionLanding = null,
        string? sequence = null,
        int? order = null,
        string? previousTargetId = null,
        string? nextTargetId = null,
        IReadOnlyList<DocRecommendationLink>? authored = null,
        bool? autoRelatedPages = null,
        bool hideFromSearch = false,
        bool hideFromPublicNav = false,
        bool hasReaderContent = true,
        bool? componentIsDerived = null,
        bool? audienceIsDerived = null,
        DocContributorMetadata? contributor = null,
        string? title = null)
    {
        var pageTitle = title ?? id;
        var metadata = new DocMetadata
        {
            PageType = pageType,
            Component = component,
            ComponentIsDerived = componentIsDerived,
            Audience = audience,
            AudienceIsDerived = audienceIsDerived,
            Aliases = aliases,
            Keywords = keywords,
            SectionLanding = metadataSectionLanding,
            SequenceKey = sequence,
            Order = order,
            AutoRelatedPages = autoRelatedPages,
            HideFromSearch = hideFromSearch,
            HideFromPublicNav = hideFromPublicNav,
            Contributor = contributor
        };
        var document = new DocNode(pageTitle, $"{id}.md", hasReaderContent ? "<p>Reader content.</p>" : string.Empty,
            Metadata: metadata);
        var link = new DocPageLinkViewModel
        {
            Title = pageTitle,
            Href = $"/docs/{id}",
            PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(pageType)
        };

        return new DocRecommendationPageFacts(document, id, link, section, isSectionLanding, previousTargetId,
            nextTargetId, authored);
    }

    private static DocRecommendationLink Link(string id) => new(id, new DocPageLinkViewModel
    {
        Title = id,
        Href = $"/docs/{id}"
    });
}
