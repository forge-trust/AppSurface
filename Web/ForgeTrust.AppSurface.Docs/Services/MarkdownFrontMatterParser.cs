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
            EntryPoints = NormalizeEntryPoints(document.EntryPoints, diagnostics),
            Trust = NormalizeTrust(document.Trust),
            Contributor = NormalizeContributor(document.Contributor),
            Localization = NormalizeLocalization(document, diagnostics),
            TitleIsDerived = document.Title is not null ? false : null,
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

    private static IReadOnlyList<DocNamespaceEntryPoint>? NormalizeEntryPoints(
        List<FrontMatterNamespaceEntryPoint?>? entries,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (entries is null)
        {
            return null;
        }

        var normalizedEntries = new List<DocNamespaceEntryPoint>();
        var labelsByTarget = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var fieldPath = $"entry_points[{index}]";
            if (entry is null)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "null-namespace-entry-point",
                        fieldPath,
                        "A namespace entry-point entry is null.",
                        "Null list items cannot render in the Common entry points panel.",
                        "Remove the empty list item or replace it with an entry-point object."));
                continue;
            }

            var label = NormalizeDecoded(entry.Label);
            if (label is null || label.Length > 80)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "invalid-namespace-entry-point-label",
                        $"{fieldPath}.label",
                        "A namespace entry point has no usable label.",
                        "Entry-point labels must be non-empty plain text no longer than 80 characters after HTML decoding.",
                        "Add a concise label such as AddRazorWire(...) or remove the entry."));
                continue;
            }

            var summary = NormalizeDecoded(entry.Summary);
            if (summary?.Length > 220)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "invalid-namespace-entry-point-summary",
                        $"{fieldPath}.summary",
                        "A namespace entry-point summary is too long.",
                        "Entry-point summaries must be 220 characters or fewer so the panel stays scannable.",
                        "Shorten the summary or move the detail into the README prose."));
                summary = null;
            }

            var target = NormalizeEntryPointTarget(entry.Target, fieldPath, diagnostics);
            var href = NormalizeEntryPointHref(entry.Href, target, fieldPath, diagnostics);
            var keywords = NormalizeEntryPointKeywords(entry.Keywords, fieldPath, diagnostics);
            var order = entry.Order;
            if (order < 0)
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "invalid-namespace-entry-point-order",
                        $"{fieldPath}.order",
                        "A namespace entry-point order is negative.",
                        "Entry-point order values must be zero or greater.",
                        "Use a non-negative order value or remove the field."));
                order = null;
            }

            var duplicateKey = $"{label}\n{target ?? href ?? string.Empty}";
            if (!labelsByTarget.Add(duplicateKey))
            {
                diagnostics.Add(
                    new AppSurfaceDocsMetadataDiagnostic(
                        "duplicate-namespace-entry-point",
                        fieldPath,
                        "A namespace entry point duplicates an earlier label and destination.",
                        "Duplicate labels are only useful when they point at different targets.",
                        "Remove the duplicate entry or point it at a different generated anchor."));
                continue;
            }

            normalizedEntries.Add(
                new DocNamespaceEntryPoint
                {
                    Label = label,
                    Summary = summary,
                    Target = target,
                    Href = href,
                    Keywords = keywords,
                    Order = order,
                    SourceIndex = index
                });
        }

        return normalizedEntries.Count == 0 ? null : normalizedEntries;
    }

    private static string? NormalizeDecoded(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(normalized);
        return Normalize(decoded);
    }

    private static string? NormalizeEntryPointTarget(
        string? rawTarget,
        string fieldPath,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        var target = NormalizeDecoded(rawTarget);
        if (target is null)
        {
            return null;
        }

        if (target.StartsWith('#'))
        {
            target = target[1..].Trim();
        }

        if (target.Length == 0)
        {
            return null;
        }

        if (!target.All(IsValidEntryPointTargetCharacter))
        {
            diagnostics.Add(
                new AppSurfaceDocsMetadataDiagnostic(
                    "invalid-namespace-entry-point-target",
                    $"{fieldPath}.target",
                    "A namespace entry-point target contains unsupported characters.",
                    "Targets must be generated namespace-page anchor IDs containing only letters, digits, underscores, hyphens, periods, or colons.",
                    "Use the generated anchor ID without a leading #, or remove target and use a valid href escape hatch."));
            return null;
        }

        return target;
    }

    private static bool IsValidEntryPointTargetCharacter(char ch)
    {
        return char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':';
    }

    private static string? NormalizeEntryPointHref(
        string? rawHref,
        string? target,
        string fieldPath,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        var href = NormalizeDecoded(rawHref);
        if (href is null)
        {
            return null;
        }

        if (target is not null)
        {
            return null;
        }

        var valid = href.StartsWith('#') && href.Length > 1 && !href.StartsWith("##", StringComparison.Ordinal)
                    || href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal);
        if (valid
            && !href.Contains('?', StringComparison.Ordinal)
            && !href.Any(char.IsWhiteSpace))
        {
            return href;
        }

        diagnostics.Add(
            new AppSurfaceDocsMetadataDiagnostic(
                "invalid-namespace-entry-point-href",
                $"{fieldPath}.href",
                "A namespace entry-point href is not supported.",
                "Entry-point href values must be a fragment such as #anchor or an app-relative URL under the active docs root.",
                "Use a generated target anchor when possible, or replace href with a valid app-relative docs URL."));
        return null;
    }

    private static IReadOnlyList<string>? NormalizeEntryPointKeywords(
        List<string>? values,
        string fieldPath,
        List<AppSurfaceDocsMetadataDiagnostic> diagnostics)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Select(NormalizeDecoded)
            .Where(value => value is not null)
            .Cast<string>()
            .Where(value => value.Length <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        if (values.Any(value => NormalizeDecoded(value)?.Length > 80))
        {
            diagnostics.Add(
                new AppSurfaceDocsMetadataDiagnostic(
                    "invalid-namespace-entry-point-keyword",
                    $"{fieldPath}.keywords",
                    "One or more namespace entry-point keywords are too long.",
                    "Entry-point keywords must be 80 characters or fewer.",
                    "Shorten long keywords so they stay useful as search terms."));
        }

        return normalized.Length == 0 ? null : normalized;
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

        public List<FrontMatterNamespaceEntryPoint?>? EntryPoints { get; init; }

        public FrontMatterTrustDocument? Trust { get; init; }

        public FrontMatterContributorDocument? Contributor { get; init; }

        public FrontMatterLocalizationDocument? Localization { get; init; }

        public string? Locale { get; init; }

        public string? TranslationKey { get; init; }

        public string? LocalizedTitle { get; init; }

        public string? LocaleFallback { get; init; }
    }

    private sealed class FrontMatterNamespaceEntryPoint
    {
        public string? Label { get; init; }

        public string? Summary { get; init; }

        public string? Target { get; init; }

        public string? Href { get; init; }

        public List<string>? Keywords { get; init; }

        public int? Order { get; init; }
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
