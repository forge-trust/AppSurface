using ForgeTrust.AppSurface.Docs.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Docs.Services;

internal static class MarkdownFrontMatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Extracts inline Markdown front matter and returns the remaining Markdown with parsed metadata.
    /// </summary>
    /// <param name="markdown">The Markdown source that may begin with YAML front matter.</param>
    /// <returns>A tuple containing the Markdown body and parsed <see cref="DocMetadata"/> when present and valid.</returns>
    /// <remarks>
    /// This compatibility wrapper discards parser diagnostics. Invalid inline YAML returns the original Markdown with
    /// <see langword="null"/> metadata, and non-fatal authoring warnings such as invalid curation YAML or migration
    /// metadata are intentionally not surfaced. Call <see cref="ExtractWithDiagnostics"/> when callers need warnings.
    /// </remarks>
    internal static (string Markdown, DocMetadata? Metadata) Extract(string markdown)
    {
        var (body, result) = ExtractWithDiagnostics(markdown);
        return (body, result.Metadata);
    }

    /// <summary>
    /// Extracts inline Markdown front matter and returns the remaining Markdown with diagnostics-aware metadata.
    /// </summary>
    /// <param name="markdown">The Markdown source that may begin with YAML front matter.</param>
    /// <returns>
    /// A tuple containing the Markdown body and a <see cref="MarkdownMetadataParseResult"/> whose
    /// <see cref="MarkdownMetadataParseResult.Metadata"/> contains parsed <see cref="DocMetadata"/> when present.
    /// </returns>
    /// <remarks>
    /// This is the authoritative internal entry point for inline metadata parsing. Missing front matter returns the
    /// original Markdown and an empty diagnostic list. Invalid inline YAML returns a <see cref="AppSurfaceDocsMetadataDiagnostic"/>
    /// instead of throwing, and deliberately preserves the original Markdown so a malformed header remains visible to the
    /// reader. Callers should inspect <see cref="MarkdownMetadataParseResult.Diagnostics"/> for authoring warnings instead
    /// of relying on exceptions for inline metadata failures.
    /// </remarks>
    internal static (string Markdown, MarkdownMetadataParseResult Result) ExtractWithDiagnostics(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return (markdown, new MarkdownMetadataParseResult(null, []));
        }

        if (!TrySplitFrontMatter(markdown, out var frontMatter, out var body))
        {
            return (markdown, new MarkdownMetadataParseResult(null, []));
        }

        try
        {
            return (body, ParseMetadataYamlWithDiagnostics(frontMatter));
        }
        catch (YamlException ex)
        {
            // Preserve the original markdown when front matter is invalid so the
            // malformed header remains visible instead of silently changing meaning.
            return (
                markdown,
                new MarkdownMetadataParseResult(
                    null,
                    [
                        new AppSurfaceDocsMetadataDiagnostic(
                            "invalid-yaml",
                            "$",
                            "Inline front matter could not be parsed as YAML.",
                            ex.Message,
                            "Fix the YAML syntax or remove the front matter block.")
                    ]));
        }
    }

    /// <summary>
    /// Parses a YAML metadata document into normalized documentation metadata.
    /// </summary>
    /// <param name="yaml">The raw YAML content to deserialize.</param>
    /// <returns>The normalized metadata model, or <c>null</c> when the YAML document is empty or explicitly null.</returns>
    /// <remarks>
    /// This compatibility wrapper is shared by inline Markdown front matter and paired sidecar metadata files so both
    /// authoring styles normalize through the same schema, defaults, and empty-list handling. It returns only the
    /// <see cref="MarkdownMetadataParseResult.Metadata"/> value from
    /// <see cref="ParseMetadataYamlWithDiagnostics(string)"/> and intentionally discards schema, migration, and authoring
    /// diagnostics. Call <see cref="ParseMetadataYamlWithDiagnostics(string)"/> when callers need those warnings in addition
    /// to normalized <see cref="DocMetadata"/>.
    /// </remarks>
    /// <exception cref="YamlException">Thrown when <paramref name="yaml"/> cannot be parsed as YAML.</exception>
    internal static DocMetadata? ParseMetadataYaml(string yaml)
    {
        return ParseMetadataYamlWithDiagnostics(yaml).Metadata;
    }

    /// <summary>
    /// Parses a YAML metadata document into a diagnostics-aware metadata result.
    /// </summary>
    /// <param name="yaml">The raw YAML metadata document to deserialize.</param>
    /// <returns>
    /// A <see cref="MarkdownMetadataParseResult"/> containing optional normalized <see cref="DocMetadata"/> plus any
    /// <see cref="AppSurfaceDocsMetadataDiagnostic"/> warnings produced while normalizing supported metadata fields.
    /// </returns>
    /// <remarks>
    /// This is the authoritative internal entry point for metadata documents that are already known to be YAML, including
    /// sidecar files. Empty documents and explicit YAML <c>null</c> values return <c>null</c> metadata and no diagnostics.
    /// An empty mapping literal such as <c>{}</c> still produces a normalized <see cref="DocMetadata"/> instance whose
    /// fields may all be <c>null</c>. YAML syntax errors still throw <see cref="YamlException"/> so sidecar callers can
    /// report the sidecar file failure through their existing error path; schema and migration warnings are returned through
    /// <see cref="MarkdownMetadataParseResult.Diagnostics"/>.
    /// </remarks>
    internal static MarkdownMetadataParseResult ParseMetadataYamlWithDiagnostics(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var diagnostics = new List<AppSurfaceDocsMetadataDiagnostic>();
        var document = Deserializer.Deserialize<FrontMatterDocument>(yaml);
        if (document is null)
        {
            return new MarkdownMetadataParseResult(null, diagnostics);
        }

        var metadata = new DocMetadata
        {
            Title = Normalize(document.Title),
            Summary = Normalize(document.Summary),
            PageType = Normalize(document.PageType),
            Audience = Normalize(document.Audience),
            Component = Normalize(document.Component),
            Aliases = NormalizeList(document.Aliases),
            RedirectAliases = NormalizeList(document.RedirectAliases),
            Keywords = NormalizeList(document.Keywords),
            Status = Normalize(document.Status),
            NavGroup = Normalize(document.NavGroup),
            Order = document.Order,
            SequenceKey = Normalize(document.SequenceKey),
            SectionLanding = document.SectionLanding,
            HideFromPublicNav = document.HideFromPublicNav,
            HideFromSearch = document.HideFromSearch,
            RelatedPages = NormalizeList(document.RelatedPages),
            CanonicalSlug = Normalize(document.CanonicalSlug) ?? Normalize(document.Slug),
            Breadcrumbs = NormalizeList(document.Breadcrumbs),
            FeaturedPageGroups = NormalizeFeaturedPageGroups(
                document.FeaturedPageGroups,
                document.FeaturedPages,
                diagnostics),
            Outline = NormalizeOutline(document.Outline, diagnostics),
            Trust = NormalizeTrust(document.Trust),
            Contributor = NormalizeContributor(document.Contributor),
            Localization = NormalizeLocalization(document, diagnostics),
            PageTypeIsDerived = document.PageType is not null ? false : null,
            AudienceIsDerived = document.Audience is not null ? false : null,
            ComponentIsDerived = document.Component is not null ? false : null,
            NavGroupIsDerived = document.NavGroup is not null ? false : null
        };

        return new MarkdownMetadataParseResult(metadata, diagnostics);
    }

    private static bool TrySplitFrontMatter(string markdown, out string frontMatter, out string body)
    {
        frontMatter = string.Empty;
        body = markdown;

        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        var endMarkerIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var alternativeMarkerIndex = normalized.IndexOf("\n...\n", 4, StringComparison.Ordinal);

        var markerIndex = endMarkerIndex >= 0
            ? endMarkerIndex
            : alternativeMarkerIndex;
        if (markerIndex < 0)
        {
            return false;
        }

        frontMatter = normalized[4..markerIndex];
        body = normalized[(markerIndex + 5)..];
        return true;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<string>? NormalizeList(List<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Select(Normalize)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        return normalized;
    }

    private static IReadOnlyList<DocFeaturedPageGroupDefinition>? NormalizeFeaturedPageGroups(
        List<FrontMatterFeaturedPageGroupDefinition?>? groups,
        List<FrontMatterFeaturedPageDefinition?>? stalePages,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (stalePages is not null)
        {
            diagnostics.Add(
                new AppSurfaceDocsMetadataDiagnostic(
                    "stale-featured-pages",
                    "featured_pages",
                    "The flat featured_pages field is no longer rendered.",
                    "AppSurface Docs now groups landing curation by reader intent with featured_page_groups.",
                    "Move each entry under featured_page_groups[].pages and give each group a label or intent."));
        }

        if (groups is null)
        {
            return null;
        }

        var normalizedGroups = new List<DocFeaturedPageGroupDefinition>();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var groupPath = $"featured_page_groups[{groupIndex}]";
            if (group is null)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "null-featured-group",
                        groupPath,
                        "A featured page group entry is null.",
                        "Null list items cannot be normalized into landing curation.",
                        "Remove the empty list item or replace it with a featured page group object."));
                continue;
            }

            if (group.HasFlatFeaturedPageShape())
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "flat-looking-featured-group",
                        groupPath,
                        "A featured_page_groups entry looks like an old flat featured page.",
                        "The entry has page fields such as path or question directly on the group instead of under pages.",
                        "Wrap page entries under pages and add a group label or intent."));
                continue;
            }

            var intent = Normalize(group.Intent);
            var label = Normalize(group.Label);
            if (intent is null && label is null)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "missing-featured-group-identity",
                        groupPath,
                        "A featured page group has no label or intent.",
                        "AppSurface Docs needs one stable identity field for rendering and diagnostics.",
                        "Add label for reader-facing text or intent for a stable slug."));
                continue;
            }

            if (group.Pages is null)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "missing-featured-group-pages",
                        $"{groupPath}.pages",
                        "A featured page group has no pages list.",
                        "Groups without pages cannot resolve any landing rows.",
                        "Add pages with at least one path, or remove the empty group."));
                continue;
            }

            if (group.Pages.Count == 0)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "empty-featured-group-pages",
                        $"{groupPath}.pages",
                        "A featured page group has an empty pages list.",
                        "Groups without page entries cannot resolve any landing rows.",
                        "Add at least one page with a path, or remove the empty group."));
                continue;
            }

            intent ??= NormalizeIntent(label!);
            label ??= TitleCaseIntent(intent);
            var pages = new List<DocFeaturedPageDefinition>();
            for (var pageIndex = 0; pageIndex < group.Pages.Count; pageIndex++)
            {
                var page = group.Pages[pageIndex];
                if (page is null)
                {
                    continue;
                }

                var question = Normalize(page.Question);
                var path = Normalize(page.Path);
                var supportingCopy = Normalize(page.SupportingCopy);
                var pagePath = $"{groupPath}.pages[{pageIndex}]";
                if (path is null)
                {
                    if (question is not null || supportingCopy is not null || page.Order is not null)
                    {
                        diagnostics.Add(
                            new AppSurfaceDocsMetadataDiagnostic(
                                "missing-featured-group-page-path",
                                $"{pagePath}.path",
                                "A featured page entry has no path.",
                                "Featured landing rows need a destination page to resolve.",
                                "Add a non-empty path, or remove the page entry."));
                    }

                    continue;
                }

                pages.Add(
                    new DocFeaturedPageDefinition
                    {
                        Question = question,
                        Path = path,
                        SupportingCopy = supportingCopy,
                        Order = page.Order,
                        SourceFieldPath = pagePath
                    });
            }

            if (pages.Count == 0)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "empty-featured-group-page-entries",
                        $"{groupPath}.pages",
                        "A featured page group has no usable page entries.",
                        "Every page entry was null or normalized away, so AppSurface Docs cannot resolve any landing rows.",
                        "Add at least one page entry with a path, or remove the empty group."));
                continue;
            }

            normalizedGroups.Add(
                new DocFeaturedPageGroupDefinition
                {
                    Intent = intent,
                    Label = label,
                    Summary = Normalize(group.Summary),
                    Order = group.Order,
                    Pages = pages,
                    SourceFieldPath = groupPath
                });
        }

        return normalizedGroups;
    }

    private static DocOutlineMetadata? NormalizeOutline(
        object? value,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not IDictionary<object, object> mapping)
        {
            diagnostics.Add(
                new AppSurfaceDocsMetadataDiagnostic(
                    "invalid-outline-metadata",
                    "outline",
                    "The outline metadata value is not an object.",
                    "AppSurface Docs expects outline metadata to be a mapping with supported child fields.",
                    "Use outline.max_heading_level or outline.repeated_heading_policy, or remove the outline field."));
            return null;
        }

        var maxHeadingLevel = NormalizeOutlineMaxHeadingLevel(mapping, diagnostics);
        var repeatedHeadingPolicy = NormalizeOutlineRepeatedHeadingPolicy(mapping, diagnostics);

        return maxHeadingLevel is null && repeatedHeadingPolicy is null
            ? null
            : new DocOutlineMetadata
            {
                MaxHeadingLevel = maxHeadingLevel,
                RepeatedHeadingPolicy = repeatedHeadingPolicy
            };
    }

    private static int? NormalizeOutlineMaxHeadingLevel(
        IDictionary<object, object> mapping,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (!TryGetMappingValue(mapping, "max_heading_level", out var rawValue)
            || rawValue is null)
        {
            return null;
        }

        if (TryConvertOutlineMaxHeadingLevel(rawValue, out var maxHeadingLevel)
            && maxHeadingLevel is 2 or 3)
        {
            return maxHeadingLevel;
        }

        diagnostics.Add(
            new AppSurfaceDocsMetadataDiagnostic(
                "invalid-outline-max-heading-level",
                "outline.max_heading_level",
                "The outline max_heading_level value is not supported.",
                "AppSurface Docs only supports display outline heading depths of 2 or 3.",
                "Use max_heading_level: 2, max_heading_level: 3, or remove the field to use automatic behavior."));
        return null;
    }

    internal static bool TryConvertOutlineMaxHeadingLevel(object rawValue, out int maxHeadingLevel)
    {
        switch (rawValue)
        {
            case int intValue:
                maxHeadingLevel = intValue;
                return true;
            case long longValue
                when longValue >= int.MinValue && longValue <= int.MaxValue:
                maxHeadingLevel = (int)longValue;
                return true;
            case string textValue
                when int.TryParse(textValue.Trim(), out var parsedValue):
                maxHeadingLevel = parsedValue;
                return true;
            default:
                maxHeadingLevel = 0;
                return false;
        }
    }

    private static string? NormalizeOutlineRepeatedHeadingPolicy(
        IDictionary<object, object> mapping,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (!TryGetMappingValue(mapping, "repeated_heading_policy", out var rawValue)
            || rawValue is null)
        {
            return null;
        }

        if (rawValue is string textValue)
        {
            var normalized = NormalizeOutlinePolicyToken(textValue);
            if (normalized is "auto" or "include" or "h2_only")
            {
                return normalized;
            }
        }

        diagnostics.Add(
            new AppSurfaceDocsMetadataDiagnostic(
                "invalid-outline-repeated-heading-policy",
                "outline.repeated_heading_policy",
                "The outline repeated_heading_policy value is not supported.",
                "AppSurface Docs only supports auto, include, or h2_only for repeated heading behavior.",
                "Use repeated_heading_policy: auto, repeated_heading_policy: include, repeated_heading_policy: h2_only, or remove the field."));
        return null;
    }

    private static string? NormalizeOutlinePolicyToken(string value)
    {
        var normalized = Normalize(value);
        return normalized?.ToLowerInvariant().Replace('-', '_');
    }

    private static bool TryGetMappingValue(
        IDictionary<object, object> mapping,
        string key,
        out object? value)
    {
        foreach (var entry in mapping.Where(
                     entry => entry.Key is string candidate
                              && string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static string NormalizeIntent(string label)
    {
        var slug = new string(
            label
                .Trim()
                .ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
                .ToArray());
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "featured" : string.Join('-', parts);
    }

    private static string TitleCaseIntent(string intent)
    {
        var words = intent
            .Replace('_', '-')
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return intent;
        }

        return string.Join(
            " ",
            words.Select(
                word => word.Length == 1
                    ? word.ToUpperInvariant()
                    : string.Concat(char.ToUpperInvariant(word[0]), word[1..].ToLowerInvariant())));
    }

    private static DocTrustMetadata? NormalizeTrust(FrontMatterTrustDocument? value)
    {
        if (value is null)
        {
            return null;
        }

        var trust = new DocTrustMetadata
        {
            Status = Normalize(value.Status),
            Summary = Normalize(value.Summary),
            Freshness = Normalize(value.Freshness),
            ChangeScope = Normalize(value.ChangeScope),
            Migration = NormalizeTrustLink(value.Migration),
            Archive = Normalize(value.Archive),
            Sources = NormalizeList(value.Sources)
        };

        return trust.Status is null
               && trust.Summary is null
               && trust.Freshness is null
               && trust.ChangeScope is null
               && trust.Migration is null
               && trust.Archive is null
               && trust.Sources is null
            ? null
            : trust;
    }

    private static DocTrustLink? NormalizeTrustLink(FrontMatterTrustLinkDocument? value)
    {
        if (value is null)
        {
            return null;
        }

        var link = new DocTrustLink
        {
            Label = Normalize(value.Label),
            Href = Normalize(value.Href)
        };

        return link.Label is null && link.Href is null
            ? null
            : link;
    }

    private static DocContributorMetadata? NormalizeContributor(FrontMatterContributorDocument? value)
    {
        if (value is null)
        {
            return null;
        }

        var contributor = new DocContributorMetadata
        {
            HideContributorInfo = value.HideContributorInfo,
            SourcePathOverride = Normalize(value.SourcePathOverride),
            SourceUrlOverride = Normalize(value.SourceUrlOverride),
            EditUrlOverride = Normalize(value.EditUrlOverride),
            LastUpdatedOverride = value.LastUpdatedOverride?.ToUniversalTime()
        };

        return contributor.HideContributorInfo is null
               && contributor.SourcePathOverride is null
               && contributor.SourceUrlOverride is null
               && contributor.EditUrlOverride is null
               && contributor.LastUpdatedOverride is null
            ? null
            : contributor;
    }

    private static DocLocalizationMetadata? NormalizeLocalization(
        FrontMatterDocument document,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        var nested = document.Localization;
        var flatLocale = Normalize(document.Locale);
        var nestedLocale = Normalize(nested?.Locale);
        var flatTranslationKey = Normalize(document.TranslationKey);
        var nestedTranslationKey = Normalize(nested?.TranslationKey);
        var flatLocalizedTitle = Normalize(document.LocalizedTitle);
        var nestedLocalizedTitle = Normalize(nested?.LocalizedTitle);
        var flatFallbackText = Normalize(document.LocaleFallback);
        var nestedFallbackText = Normalize(nested?.LocaleFallback);
        AddLocalizationConflictDiagnosticIfNeeded("locale", "locale", "localization.locale", flatLocale, nestedLocale, diagnostics);
        AddLocalizationConflictDiagnosticIfNeeded(
            "translation key",
            "translation_key",
            "localization.translation_key",
            flatTranslationKey,
            nestedTranslationKey,
            diagnostics);
        AddLocalizationConflictDiagnosticIfNeeded(
            "localized title",
            "localized_title",
            "localization.localized_title",
            flatLocalizedTitle,
            nestedLocalizedTitle,
            diagnostics);
        AddLocalizationConflictDiagnosticIfNeeded(
            "locale fallback",
            "locale_fallback",
            "localization.locale_fallback",
            flatFallbackText,
            nestedFallbackText,
            diagnostics);

        var locale = flatLocale ?? nestedLocale;
        var translationKey = flatTranslationKey ?? nestedTranslationKey;
        var localizedTitle = flatLocalizedTitle ?? nestedLocalizedTitle;
        var fallbackText = flatFallbackText ?? nestedFallbackText;
        AppSurfaceDocsLocaleFallbackMode? fallback = null;
        if (fallbackText is not null)
        {
            var fallbackFieldPath = flatFallbackText is not null
                ? "locale_fallback"
                : "localization.locale_fallback";
            if (Enum.TryParse<AppSurfaceDocsLocaleFallbackMode>(fallbackText, ignoreCase: true, out var parsedFallback)
                && Enum.IsDefined(parsedFallback))
            {
                fallback = parsedFallback;
            }
            else
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "invalid-locale-fallback",
                        fallbackFieldPath,
                        "The locale fallback value is not supported.",
                        "AppSurface Docs only supports DefaultLocaleWithNotice or Disabled for page-level localization fallback.",
                        "Use locale_fallback: Disabled or remove the field to inherit the global fallback mode."));
            }
        }

        return locale is null
               && translationKey is null
               && localizedTitle is null
               && fallback is null
            ? null
            : new DocLocalizationMetadata
            {
                Locale = locale,
                TranslationKey = translationKey,
                LocalizedTitle = localizedTitle,
                LocaleFallback = fallback
            };
    }

    private static void AddLocalizationConflictDiagnosticIfNeeded(
        string fieldName,
        string flatFieldPath,
        string nestedFieldPath,
        string? flatValue,
        string? nestedValue,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        var comparison = GetLocalizationConflictComparison(flatFieldPath);
        if (flatValue is null
            || nestedValue is null
            || string.Equals(flatValue, nestedValue, comparison))
        {
            return;
        }

        diagnostics.Add(
            new AppSurfaceDocsMetadataDiagnostic(
                "localization-field-conflict",
                flatFieldPath,
                $"The flat localization {fieldName} conflicts with {nestedFieldPath}.",
                "AppSurface Docs prefers the flat front matter value and ignores the nested value for that field.",
                $"Keep only {flatFieldPath} or {nestedFieldPath}, or make both values match."));
    }

    private static StringComparison GetLocalizationConflictComparison(string flatFieldPath)
    {
        return flatFieldPath is "locale" or "translation_key" or "locale_fallback"
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class FrontMatterDocument
    {
        public string? Title { get; init; }

        public string? Summary { get; init; }

        public string? PageType { get; init; }

        public string? Audience { get; init; }

        public string? Component { get; init; }

        public List<string>? Aliases { get; init; }

        public List<string>? RedirectAliases { get; init; }

        public List<string>? Keywords { get; init; }

        public string? Status { get; init; }

        public string? NavGroup { get; init; }

        public int? Order { get; init; }

        public string? SequenceKey { get; init; }

        public bool? SectionLanding { get; init; }

        public bool? HideFromPublicNav { get; init; }

        public bool? HideFromSearch { get; init; }

        public List<string>? RelatedPages { get; init; }

        public string? CanonicalSlug { get; init; }

        public string? Slug { get; init; }

        public List<string>? Breadcrumbs { get; init; }

        public List<FrontMatterFeaturedPageGroupDefinition?>? FeaturedPageGroups { get; init; }

        public List<FrontMatterFeaturedPageDefinition?>? FeaturedPages { get; init; }

        public FrontMatterTrustDocument? Trust { get; init; }

        public FrontMatterContributorDocument? Contributor { get; init; }

        public FrontMatterLocalizationDocument? Localization { get; init; }

        public object? Outline { get; init; }

        public string? Locale { get; init; }

        public string? TranslationKey { get; init; }

        public string? LocalizedTitle { get; init; }

        public string? LocaleFallback { get; init; }
    }

    private sealed class FrontMatterFeaturedPageDefinition
    {
        public string? Question { get; init; }

        public string? Path { get; init; }

        public string? SupportingCopy { get; init; }

        public int? Order { get; init; }
    }

    private sealed class FrontMatterFeaturedPageGroupDefinition
    {
        public string? Intent { get; init; }

        public string? Label { get; init; }

        public string? Summary { get; init; }

        public int? Order { get; init; }

        public List<FrontMatterFeaturedPageDefinition?>? Pages { get; init; }

        public string? Question { get; init; }

        public string? Path { get; init; }

        public string? SupportingCopy { get; init; }

        public bool HasFlatFeaturedPageShape()
        {
            return !string.IsNullOrWhiteSpace(Question)
                   || !string.IsNullOrWhiteSpace(Path)
                   || !string.IsNullOrWhiteSpace(SupportingCopy);
        }
    }

    private sealed class FrontMatterTrustDocument
    {
        public string? Status { get; init; }

        public string? Summary { get; init; }

        public string? Freshness { get; init; }

        public string? ChangeScope { get; init; }

        public FrontMatterTrustLinkDocument? Migration { get; init; }

        public string? Archive { get; init; }

        public List<string>? Sources { get; init; }
    }

    private sealed class FrontMatterContributorDocument
    {
        public bool? HideContributorInfo { get; init; }

        public string? SourcePathOverride { get; init; }

        public string? SourceUrlOverride { get; init; }

        public string? EditUrlOverride { get; init; }

        public DateTimeOffset? LastUpdatedOverride { get; init; }
    }

    private sealed class FrontMatterLocalizationDocument
    {
        public string? Locale { get; init; }

        public string? TranslationKey { get; init; }

        public string? LocalizedTitle { get; init; }

        public string? LocaleFallback { get; init; }
    }

    private sealed class FrontMatterTrustLinkDocument
    {
        public string? Label { get; init; }

        public string? Href { get; init; }
    }
}
