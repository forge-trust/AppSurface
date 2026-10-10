using System.Collections.ObjectModel;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>One resolved canonical link, independent of its response mount.</summary>
internal sealed record DocRecommendationLink(string TargetId, DocPageLinkViewModel Page);

/// <summary>
/// Final-snapshot facts consumed by the pure recommendation policy. Callers resolve authored references and sequence
/// neighbors with the existing route resolver before constructing these facts; only canonical route winners belong here.
/// </summary>
internal sealed record DocRecommendationPageFacts(
    DocNode Document,
    string TargetId,
    DocPageLinkViewModel Page,
    DocPublicSection? Section = null,
    bool IsSectionLanding = false,
    string? PreviousTargetId = null,
    string? NextTargetId = null,
    IReadOnlyList<DocRecommendationLink>? Authored = null);

/// <summary>A snapshot-owned recommendation. Response projections must detach the page and reason collection.</summary>
internal sealed record DocRecommendation(
    string TargetId,
    DocPageLinkViewModel Page,
    bool IsAutomatic,
    string PrimaryReasonCode,
    IReadOnlyList<string> ReasonCodes,
    string ReasonText);

/// <summary>Diagnostic counts for the candidate union, before source-specific filtering; no runtime tuning surface.</summary>
internal sealed record DocRecommendationMeasurements(
    int EligibleTargets,
    long CandidatesEvaluated,
    int AutomaticResults,
    IReadOnlyDictionary<string, int> CandidateCounts);

/// <summary>Complete, immutable results and measurements from one policy build.</summary>
internal sealed record DocRecommendationPolicyResult(
    IReadOnlyDictionary<string, IReadOnlyList<DocRecommendation>> Pages,
    DocRecommendationMeasurements Measurements);

/// <summary>
/// Selects at most three automatic next reads after an uncapped authored prefix, using only final canonical snapshot
/// metadata. Inverted indexes generate candidates; section, audience and sequence alone never qualify an ordinary page.
/// </summary>
/// <remarks>
/// Terms are ordinal case-insensitive, trimmed and whitespace-collapsed, with no stemming or punctuation rewriting.
/// Common terms occur on at least five eligible targets and more than one quarter of that target corpus. Derived
/// component/audience values are ignored. Source opt-out does not change target membership or term frequencies.
/// All tie breaks are explicit; input order and current culture have no influence on ranking.
/// </remarks>
internal static class DocRecommendationPolicy
{
    private sealed record Facts(
        DocRecommendationPageFacts Input,
        string? Role,
        string? Component,
        string? Audience,
        string? Sequence,
        HashSet<string> Topics,
        bool IsInternal);

    private sealed record Candidate(
        Facts Target,
        int RolePriority,
        bool SharedComponent,
        int SharedTopics,
        bool ForwardSequence,
        bool SameAudience,
        bool SameSection,
        bool SameOrderedSequence);

    /// <summary>
    /// Builds a detached, read-only result for every canonical source. Authored entries retain exact fragment identities
    /// and order; automatic entries exclude the owning pages of the source, sequence neighbors and authored entries.
    /// </summary>
    internal static DocRecommendationPolicyResult Build(IReadOnlyList<DocRecommendationPageFacts> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var facts = pages.Select(CreateFacts).ToArray();
        var targets = facts.Where(IsTarget).ToArray();
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var term in target.Topics)
            {
                frequency[term] = frequency.GetValueOrDefault(term) + 1;
            }
        }

        var common = frequency.Where(pair => pair.Value >= 5 && pair.Value * 4 > targets.Length)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            fact.Topics.ExceptWith(common);
        }

        var components = Index(targets, target => target.Component is { } component ? [component] : []);
        var topics = Index(targets, target => target.Topics);
        var sequences = Index(targets, target => target.Sequence is { } sequence ? [sequence] : []);
        var landings = targets.Where(target => target.Input.IsSectionLanding && target.Input.Section is not null)
            .GroupBy(target => target.Input.Section!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var results = new Dictionary<string, IReadOnlyList<DocRecommendation>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long evaluated = 0;
        var resultCount = 0;
        foreach (var source in facts)
        {
            var rows = (source.Input.Authored ?? []).Select(link => CreateRow(link.TargetId, link.Page, false, "authored", []))
                .ToList();
            if (IsSource(source))
            {
                var union = new HashSet<Facts>(ReferenceEqualityComparer.Instance);
                AddMatches(components, source.Component, union);
                foreach (var topic in source.Topics)
                {
                    AddMatches(topics, topic, union);
                }

                AddMatches(sequences, source.Sequence, union);
                if (source.Input.Section is { } section && landings.TryGetValue(section, out var sameSectionLandings))
                {
                    union.UnionWith(sameSectionLandings);
                }

                evaluated += union.Count;
                counts[source.Input.TargetId] = union.Count;
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Owner(source.Input.TargetId) };
                foreach (var id in new[] { source.Input.PreviousTargetId, source.Input.NextTargetId })
                {
                    if (id is not null)
                    {
                        excluded.Add(Owner(id));
                    }
                }

                foreach (var row in rows)
                {
                    excluded.Add(Owner(row.TargetId));
                }

                var candidates = new List<Candidate>();
                foreach (var target in union)
                {
                    if (excluded.Contains(Owner(target.Input.TargetId)))
                    {
                        continue;
                    }

                    var sharedComponent = source.Component is not null &&
                                          StringComparer.OrdinalIgnoreCase.Equals(source.Component, target.Component);
                    var sharedTopics = source.Topics.Count(target.Topics.Contains);
                    var sameSection = source.Input.Section is not null && source.Input.Section == target.Input.Section;
                    if (!(sharedComponent || sharedTopics >= 2 || target.Input.IsSectionLanding && sameSection))
                    {
                        continue;
                    }

                    var sameSequence = source.Sequence is not null &&
                                       StringComparer.OrdinalIgnoreCase.Equals(source.Sequence, target.Sequence);
                    var sameOrderedSequence = sameSequence && source.Input.Document.Metadata?.Order is not null &&
                                              target.Input.Document.Metadata?.Order is not null;
                    if (sameOrderedSequence && target.Input.Document.Metadata!.Order <= source.Input.Document.Metadata!.Order)
                    {
                        continue;
                    }

                    candidates.Add(new Candidate(target, Priority(source, target), sharedComponent, Math.Min(sharedTopics, 3),
                        sameOrderedSequence, source.Audience is not null &&
                                             StringComparer.OrdinalIgnoreCase.Equals(source.Audience, target.Audience),
                        sameSection, sameOrderedSequence));
                }

                candidates.Sort(Compare);
                foreach (var candidate in candidates.Take(3))
                {
                    var target = candidate.Target;
                    var primary = target.Input.IsSectionLanding ? "section-overview" : target.Role switch
                    {
                        "example" => "try-example",
                        "troubleshooting" => "troubleshoot",
                        _ => "deepen-guide"
                    };
                    var evidence = new List<string>();
                    if (candidate.SharedComponent) evidence.Add("shared-component");
                    if (candidate.SharedTopics > 0) evidence.Add("shared-topics");
                    if (candidate.ForwardSequence) evidence.Add("shared-sequence");
                    if (candidate.SameAudience) evidence.Add("same-audience");
                    if (candidate.SameSection) evidence.Add("same-section");
                    rows.Add(CreateRow(target.Input.TargetId, target.Input.Page, true, primary, evidence));
                    resultCount++;
                }
            }

            results[source.Input.TargetId] = Array.AsReadOnly(rows.ToArray());
        }

        return new DocRecommendationPolicyResult(new ReadOnlyDictionary<string, IReadOnlyList<DocRecommendation>>(results),
            new DocRecommendationMeasurements(targets.Length, evaluated, resultCount,
                new ReadOnlyDictionary<string, int>(counts)));
    }

    /// <summary>Maps stable primary reason codes to the default built-in caption, including future-code fallback.</summary>
    internal static string ReasonText(string primaryReasonCode, bool sharedComponent = false) => primaryReasonCode switch
    {
        "authored" => "Selected by the author",
        "section-overview" => "Explore this section",
        "try-example" => sharedComponent ? "Try an example for this component" : "Try an example for this topic",
        "troubleshoot" => sharedComponent ? "Troubleshoot this component" : "Troubleshoot this topic",
        "deepen-guide" => sharedComponent ? "Go deeper on this component" : "Go deeper on this topic",
        _ => "Read this page next"
    };

    private static DocRecommendation CreateRow(string id, DocPageLinkViewModel page, bool automatic, string primary,
        IReadOnlyList<string> evidence) => new(id, page with { PageTypeBadge = page.PageTypeBadge is { } badge ? badge with { } : null },
        automatic, primary, Array.AsReadOnly(new[] { primary }.Concat(evidence).ToArray()), ReasonText(primary, evidence.Contains("shared-component", StringComparer.Ordinal)));

    private static Facts CreateFacts(DocRecommendationPageFacts input)
    {
        var metadata = input.Document.Metadata;
        var role = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType);
        return new Facts(input, role?.Variant == "api-reference" ? "api-reference" : role?.Value,
            metadata?.ComponentIsDerived == true ? null : Normalize(metadata?.Component),
            metadata?.AudienceIsDerived == true ? null : Normalize(metadata?.Audience),
            string.IsNullOrWhiteSpace(metadata?.SequenceKey) ? null : metadata.SequenceKey.Trim(),
            (metadata?.Aliases ?? []).Concat(metadata?.Keywords ?? []).Select(Normalize)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            role?.Value is "internal" or "internals" or "contributor" || input.Section == DocPublicSection.Internals);
    }

    private static bool IsReadable(Facts facts) => facts.Input.Document.HasReaderContent &&
        !facts.Input.TargetId.Contains('#', StringComparison.Ordinal) && !facts.IsInternal &&
        facts.Input.Document.Metadata?.HideFromSearch != true && facts.Input.Document.Metadata?.HideFromPublicNav != true;

    private static bool IsSource(Facts facts) => IsReadable(facts) && facts.Input.Document.Metadata?.AutoRelatedPages != false;
    private static bool IsTarget(Facts facts) => IsReadable(facts) &&
        (facts.Input.IsSectionLanding || facts.Role is "guide" or "example" or "troubleshooting");

    private static int Priority(Facts source, Facts target)
    {
        if (target.Input.IsSectionLanding) return 0;
        var role = source.Input.IsSectionLanding ? null : source.Role;
        return (role, target.Role) switch
        {
            ("guide" or "how-to", "example") or ("example", "troubleshooting") or
            ("troubleshooting", "guide") or ("api-reference", "example") => 3,
            ("guide" or "how-to", "troubleshooting") or ("example", "guide") or
            ("troubleshooting", "example") or ("api-reference", "guide") => 2,
            ("guide" or "how-to", "guide") or ("example", "example") or
            ("troubleshooting", "troubleshooting") or ("api-reference", "troubleshooting") => 1,
            (_, "guide") => 3,
            (_, "example") => 2,
            _ => 1
        };
    }

    private static int Compare(Candidate left, Candidate right)
    {
        var result = right.RolePriority.CompareTo(left.RolePriority);
        if (result == 0) result = right.SharedComponent.CompareTo(left.SharedComponent);
        if (result == 0) result = right.SharedTopics.CompareTo(left.SharedTopics);
        if (result == 0) result = right.ForwardSequence.CompareTo(left.ForwardSequence);
        if (result == 0) result = right.SameAudience.CompareTo(left.SameAudience);
        if (result == 0) result = right.SameSection.CompareTo(left.SameSection);
        if (result == 0 && left.SameOrderedSequence && right.SameOrderedSequence)
            result = left.Target.Input.Document.Metadata!.Order!.Value.CompareTo(right.Target.Input.Document.Metadata!.Order!.Value);
        if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(left.Target.Input.Page.Title, right.Target.Input.Page.Title);
        if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(left.Target.Input.TargetId, right.Target.Input.TargetId);
        return result == 0 ? StringComparer.Ordinal.Compare(left.Target.Input.TargetId, right.Target.Input.TargetId) : result;
    }

    private static Dictionary<string, List<Facts>> Index(IEnumerable<Facts> targets, Func<Facts, IEnumerable<string>> keys)
    {
        var index = new Dictionary<string, List<Facts>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var key in keys(target))
            {
                if (!index.TryGetValue(key, out var matches)) index[key] = matches = [];
                matches.Add(target);
            }
        }
        return index;
    }

    private static void AddMatches(Dictionary<string, List<Facts>> index, string? key, HashSet<Facts> union)
    {
        if (key is not null && index.TryGetValue(key, out var matches)) union.UnionWith(matches);
    }

    private static string Owner(string id) => id.Split('#', 2)[0];

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
