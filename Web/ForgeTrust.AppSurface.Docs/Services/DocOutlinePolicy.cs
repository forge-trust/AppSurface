using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Applies Markdown display-outline rules after metadata defaults and author overrides have been resolved.
/// </summary>
internal static class DocOutlinePolicy
{
    private const int MinHeadingLevel = 2;
    private const int MaxHeadingLevel = 3;
    private const string AutoPolicy = "auto";
    private const string IncludePolicy = "include";
    private const string H2OnlyPolicy = "h2_only";
    private const string TroubleshootingPageType = "troubleshooting";
    private const int TroubleshootingMinimumH3Count = 4;
    private const double TroubleshootingRepeatedRatio = 0.50;
    private const int TroubleshootingMinimumH2Count = 2;
    private const int GeneralMinimumH3Count = 8;
    private const double GeneralRepeatedRatio = 0.60;
    private const int GeneralMinimumH2Count = 3;
    private const int MinimumDistinctRepeatedH3Titles = 2;
    private const int MinimumRepeatedHeadingParentCount = 2;

    /// <summary>
    /// Applies explicit outline metadata and automatic repeated-heading suppression to a harvested Markdown outline.
    /// </summary>
    /// <param name="outline">The source-ordered harvested Markdown outline entries.</param>
    /// <param name="metadata">The resolved document metadata, including derived defaults.</param>
    /// <returns>The outline entries to expose to readers and search heading metadata.</returns>
    /// <remarks>
    /// This method filters the display outline only. The rendered Markdown HTML remains unchanged, so fragment links to
    /// hidden outline headings can still resolve when readers or search results point directly at the heading.
    /// </remarks>
    internal static IReadOnlyList<DocOutlineItem> Apply(
        IReadOnlyList<DocOutlineItem> outline,
        DocMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(outline);

        if (outline.Count == 0)
        {
            return outline;
        }

        var configuredMaxHeadingLevel = metadata?.Outline?.MaxHeadingLevel;
        if (configuredMaxHeadingLevel is MinHeadingLevel or MaxHeadingLevel)
        {
            return FilterByMaxHeadingLevel(outline, configuredMaxHeadingLevel.Value);
        }

        var repeatedHeadingPolicy = metadata?.Outline?.RepeatedHeadingPolicy;
        if (string.Equals(repeatedHeadingPolicy, IncludePolicy, StringComparison.Ordinal))
        {
            return FilterByMaxHeadingLevel(outline, MaxHeadingLevel);
        }

        if (string.Equals(repeatedHeadingPolicy, H2OnlyPolicy, StringComparison.Ordinal))
        {
            return FilterByMaxHeadingLevel(outline, MinHeadingLevel);
        }

        if (repeatedHeadingPolicy is not null
            && !string.Equals(repeatedHeadingPolicy, AutoPolicy, StringComparison.Ordinal))
        {
            return outline;
        }

        return ShouldSuppressRepeatedH3Headings(outline, metadata)
            ? FilterByMaxHeadingLevel(outline, MinHeadingLevel)
            : outline;
    }

    private static IReadOnlyList<DocOutlineItem> FilterByMaxHeadingLevel(
        IReadOnlyList<DocOutlineItem> outline,
        int maxHeadingLevel)
    {
        if (outline.All(item => item.Level <= maxHeadingLevel))
        {
            return outline;
        }

        return outline.Where(item => item.Level <= maxHeadingLevel).ToArray();
    }

    private static bool ShouldSuppressRepeatedH3Headings(
        IReadOnlyList<DocOutlineItem> outline,
        DocMetadata? metadata)
    {
        var h2Count = outline.Count(item => item.Level == MinHeadingLevel);
        var h3Items = outline.Where(item => item.Level == MaxHeadingLevel).ToArray();
        if (h3Items.Length == 0)
        {
            return false;
        }

        var repeatedTitleCounts = h3Items
            .Select(item => NormalizeHeadingTitle(item.Title))
            .Where(title => title is not null)
            .Cast<string>()
            .GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        if (repeatedTitleCounts.Count < MinimumDistinctRepeatedH3Titles)
        {
            return false;
        }

        var parentSummary = SummarizeRepeatedHeadingParents(outline, repeatedTitleCounts);
        if (parentSummary.DistinctRepeatedTitlesAcrossParents < MinimumDistinctRepeatedH3Titles)
        {
            return false;
        }

        var repeatedH3Count = h3Items.Count(item =>
        {
            var title = NormalizeHeadingTitle(item.Title);
            return title is not null && parentSummary.TitlesAcrossParents.Contains(title);
        });
        var repeatedRatio = repeatedH3Count / (double)h3Items.Length;
        var thresholds = IsTroubleshootingLike(metadata)
            ? new OutlineThresholds(
                TroubleshootingMinimumH3Count,
                TroubleshootingRepeatedRatio,
                TroubleshootingMinimumH2Count)
            : new OutlineThresholds(GeneralMinimumH3Count, GeneralRepeatedRatio, GeneralMinimumH2Count);

        return h3Items.Length >= thresholds.MinimumH3Count
               && repeatedRatio >= thresholds.RepeatedRatio
               && h2Count >= thresholds.MinimumH2Count
               && parentSummary.DistinctParentCount >= MinimumRepeatedHeadingParentCount;
    }

    private static RepeatedHeadingParentSummary SummarizeRepeatedHeadingParents(
        IReadOnlyList<DocOutlineItem> outline,
        IReadOnlyDictionary<string, int> repeatedTitleCounts)
    {
        var parentKeysByTitle = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentParentKey = null;
        var parentIndex = 0;

        foreach (var item in outline)
        {
            if (item.Level == MinHeadingLevel)
            {
                parentIndex++;
                currentParentKey = BuildParentKey(item, parentIndex);
                continue;
            }

            if (item.Level != MaxHeadingLevel || currentParentKey is null)
            {
                continue;
            }

            var title = NormalizeHeadingTitle(item.Title);
            if (title is not null && repeatedTitleCounts.ContainsKey(title))
            {
                if (!parentKeysByTitle.TryGetValue(title, out var parentKeys))
                {
                    parentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    parentKeysByTitle.Add(title, parentKeys);
                }

                parentKeys.Add(currentParentKey);
            }
        }

        var titlesAcrossParents = parentKeysByTitle
            .Where(entry => entry.Value.Count >= MinimumRepeatedHeadingParentCount)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parentCount = parentKeysByTitle
            .Where(entry => titlesAcrossParents.Contains(entry.Key))
            .SelectMany(entry => entry.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new RepeatedHeadingParentSummary(
            titlesAcrossParents,
            titlesAcrossParents.Count,
            parentCount);
    }

    private static string BuildParentKey(DocOutlineItem item, int parentIndex)
    {
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            return item.Id.Trim();
        }

        var title = NormalizeHeadingTitle(item.Title);
        return title is null
            ? $"h2:{parentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"h2:{title}:{parentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static bool IsTroubleshootingLike(DocMetadata? metadata)
    {
        return string.Equals(
                   DocMetadataPresentation.NormalizeToken(metadata?.PageType),
                   TroubleshootingPageType,
                   StringComparison.Ordinal)
               || string.Equals(
                   DocMetadataPresentation.NormalizeToken(metadata?.NavGroup),
                   TroubleshootingPageType,
                   StringComparison.Ordinal);
    }

    private static string? NormalizeHeadingTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var segments = value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? null : string.Join(" ", segments);
    }

    private readonly record struct OutlineThresholds(
        int MinimumH3Count,
        double RepeatedRatio,
        int MinimumH2Count);

    private sealed record RepeatedHeadingParentSummary(
        IReadOnlySet<string> TitlesAcrossParents,
        int DistinctRepeatedTitlesAcrossParents,
        int DistinctParentCount);
}
