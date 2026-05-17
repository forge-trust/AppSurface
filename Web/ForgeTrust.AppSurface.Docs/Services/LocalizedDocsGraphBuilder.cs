using System.Globalization;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed record LocalizedDocsGraph(
    bool Enabled,
    string? DefaultLocale,
    IReadOnlyList<LocalizedDocSet> DocSets,
    IReadOnlyDictionary<string, LocalizedDocVariant> VariantsBySourcePath,
    IReadOnlyList<DocHarvestDiagnostic> Diagnostics);

internal sealed record LocalizedDocSet(
    string TranslationKey,
    string? DefaultLocaleSourcePath,
    IReadOnlyList<LocalizedDocVariant> Variants,
    RazorDocsLocaleFallbackMode FallbackMode);

internal sealed record LocalizedDocVariant(
    string SourcePath,
    string Locale,
    string TranslationKey,
    string Title,
    string? PublicRoutePath,
    RazorDocsLocaleFallbackMode? LocaleFallback,
    bool LocaleWasInferred,
    bool TranslationKeyWasInferred);

/// <summary>
/// Builds the locale-aware document graph used by later route, navigation, fallback, and search projections.
/// </summary>
/// <remarks>
/// This Phase 1 builder is intentionally internal. It records document identity and locale facts without changing the
/// existing visible route or search behavior. Later slices should consume this graph instead of re-inferring locale state
/// in controllers, views, or JavaScript.
/// </remarks>
internal sealed class LocalizedDocsGraphBuilder
{
    private readonly RazorDocsLocalizationOptions _options;
    private readonly IReadOnlyDictionary<string, RazorDocsLocaleOptions> _localesByCode;
    private readonly IReadOnlyDictionary<string, RazorDocsLocaleOptions> _localesByRoutePrefix;

    internal LocalizedDocsGraphBuilder(RazorDocsLocalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _localesByCode = (options.Locales ?? [])
            .Where(locale => locale is not null && !string.IsNullOrWhiteSpace(locale.Code))
            .GroupBy(locale => locale.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _localesByRoutePrefix = (options.Locales ?? [])
            .Where(locale => locale is not null && !string.IsNullOrWhiteSpace(GetRoutePrefix(locale)))
            .GroupBy(GetRoutePrefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    internal LocalizedDocsGraph Build(IEnumerable<DocNode> docs, DocRouteIdentityCatalog routeIdentityCatalog)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(routeIdentityCatalog);

        var docList = docs.ToList();
        if (!_options.Enabled)
        {
            return new LocalizedDocsGraph(false, null, [], new Dictionary<string, LocalizedDocVariant>(), []);
        }

        var diagnostics = new List<DocHarvestDiagnostic>();
        var normalizedSourcePaths = docList
            .Select(doc => NormalizeSourcePath(doc.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variants = new List<LocalizedDocVariant>();
        foreach (var doc in docList)
        {
            var facts = ResolveFacts(doc, normalizedSourcePaths, diagnostics);
            if (facts is null)
            {
                continue;
            }

            string? publicRoutePath = null;
            if (routeIdentityCatalog.TryGetPublicRoutePath(facts.RouteSourcePath, out var resolvedRoutePath))
            {
                publicRoutePath = resolvedRoutePath;
            }
            else if (routeIdentityCatalog.TryGetPublicRoutePath(doc.Path, out resolvedRoutePath))
            {
                publicRoutePath = resolvedRoutePath;
            }

            variants.Add(
                new LocalizedDocVariant(
                    NormalizeSourcePath(doc.Path),
                    facts.Locale,
                    facts.TranslationKey,
                    ResolveTitle(doc),
                    publicRoutePath,
                    doc.Metadata?.Localization?.LocaleFallback,
                    facts.LocaleWasInferred,
                    facts.TranslationKeyWasInferred));
        }

        var docSets = new List<LocalizedDocSet>();
        foreach (var group in variants.GroupBy(variant => variant.TranslationKey, StringComparer.OrdinalIgnoreCase))
        {
            var groupVariants = group
                .OrderBy(variant => variant.Locale, StringComparer.OrdinalIgnoreCase)
                .ThenBy(variant => variant.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var duplicateGroup in groupVariants.GroupBy(variant => variant.Locale, StringComparer.OrdinalIgnoreCase))
            {
                var duplicates = duplicateGroup.ToList();
                if (duplicates.Count <= 1)
                {
                    continue;
                }

                diagnostics.Add(
                    CreateDiagnostic(
                        DocHarvestDiagnosticCodes.LocalizationDuplicateVariant,
                        $"Multiple docs use translation key '{group.Key}' for locale '{duplicateGroup.Key}'.",
                        "A language switch target must resolve to one document per locale.",
                        $"Keep only one '{duplicateGroup.Key}' variant for translation_key '{group.Key}' or give the extra document a different translation_key."));
            }

            var fallbackMode = ResolveDocSetFallbackMode(groupVariants);
            var defaultSourcePath = groupVariants
                .FirstOrDefault(variant => string.Equals(variant.Locale, _options.DefaultLocale, StringComparison.OrdinalIgnoreCase))
                ?.SourcePath;
            if (fallbackMode == RazorDocsLocaleFallbackMode.Disabled)
            {
                foreach (var missingLocale in _localesByCode.Keys.Where(
                             locale => groupVariants.All(variant => !string.Equals(variant.Locale, locale, StringComparison.OrdinalIgnoreCase))))
                {
                    diagnostics.Add(
                        CreateDiagnostic(
                            DocHarvestDiagnosticCodes.LocalizationFallbackDisabledMissingVariant,
                            $"Translation key '{group.Key}' disables fallback but has no '{missingLocale}' variant.",
                            "Readers switching to that locale would otherwise see a false page or a silent 404.",
                            $"Add a '{missingLocale}' variant for translation_key '{group.Key}' or allow fallback."));
                }
            }

            docSets.Add(
                new LocalizedDocSet(
                    group.Key,
                    defaultSourcePath,
                    groupVariants,
                    fallbackMode));
        }

        return new LocalizedDocsGraph(
            true,
            _options.DefaultLocale,
            docSets.OrderBy(set => set.TranslationKey, StringComparer.OrdinalIgnoreCase).ToArray(),
            variants
                .GroupBy(variant => variant.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase),
            diagnostics);
    }

    private ResolvedLocalizationFacts? ResolveFacts(
        DocNode doc,
        HashSet<string> normalizedSourcePaths,
        List<DocHarvestDiagnostic> diagnostics)
    {
        var sourcePath = NormalizeSourcePath(doc.Path);
        var explicitLocale = Normalize(doc.Metadata?.Localization?.Locale);
        var explicitTranslationKey = Normalize(doc.Metadata?.Localization?.TranslationKey);
        var suffixLocale = TryGetConfiguredLocaleSuffix(sourcePath, out var sourceWithoutSuffix, out var suffix)
            ? NormalizeConfiguredLocale(suffix)
            : null;
        if (ReportUnsupportedLocaleSuffix(sourcePath, sourceWithoutSuffix, suffixLocale, diagnostics))
        {
            return null;
        }

        var folderLocale = TryGetFolderLocale(sourcePath, explicitTranslationKey, out var inferredFolderLocale)
            ? inferredFolderLocale
            : null;

        if (explicitLocale is not null
            && folderLocale is not null
            && !string.Equals(explicitLocale, folderLocale, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                CreateDiagnostic(
                    DocHarvestDiagnosticCodes.LocalizationLocaleFolderConflict,
                    $"Doc '{sourcePath}' declares locale '{explicitLocale}' but its folder implies '{folderLocale}'.",
                    "Conflicting locale signals make language switching and fallback routing ambiguous.",
                    "Make the locale metadata match the configured locale folder or move the file out of that locale folder."));
        }

        var locale = explicitLocale ?? suffixLocale ?? folderLocale ?? _options.DefaultLocale;
        if (locale is null)
        {
            return null;
        }

        var normalizedLocale = NormalizeConfiguredLocale(locale);
        if (normalizedLocale is null)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale,
                    $"Doc '{sourcePath}' uses unsupported locale '{locale}'.",
                    "The locale is not configured for this RazorDocs host.",
                    "Add the locale under RazorDocs:Localization:Locales or update the document metadata."));
            return null;
        }

        var translationKey = explicitTranslationKey;
        var translationKeyWasInferred = false;
        if (translationKey is null)
        {
            translationKeyWasInferred = true;
            if (suffixLocale is not null && sourceWithoutSuffix is not null)
            {
                if (!normalizedSourcePaths.Contains(sourceWithoutSuffix))
                {
                    diagnostics.Add(
                        CreateDiagnostic(
                            DocHarvestDiagnosticCodes.LocalizationMissingBase,
                            $"Localized source '{sourcePath}' has no colocated base document '{sourceWithoutSuffix}'.",
                            "RazorDocs can still infer an identity, but language switching may be incomplete.",
                            $"Add '{sourceWithoutSuffix}' or author translation_key explicitly."));
                }

                translationKey = InferTranslationKey(sourceWithoutSuffix);
            }
            else
            {
                translationKey = InferTranslationKey(sourcePath);
            }
        }

        var routeSourcePath = !string.IsNullOrWhiteSpace(doc.Metadata?.CanonicalSlug)
            ? sourcePath
            : sourceWithoutSuffix ?? sourcePath;

        return new ResolvedLocalizationFacts(
            normalizedLocale,
            translationKey,
            routeSourcePath,
            LocaleWasInferred: explicitLocale is null,
            TranslationKeyWasInferred: translationKeyWasInferred);
    }

    private bool ReportUnsupportedLocaleSuffix(
        string sourcePath,
        string? sourceWithoutSuffix,
        string? configuredSuffixLocale,
        List<DocHarvestDiagnostic> diagnostics)
    {
        if (configuredSuffixLocale is not null || sourceWithoutSuffix is not null)
        {
            return false;
        }

        if (!TryGetPotentialLocaleSuffix(sourcePath, out var suffix))
        {
            return false;
        }

        diagnostics.Add(
            CreateDiagnostic(
                DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale,
                $"Doc '{sourcePath}' uses unsupported locale suffix '{suffix}'.",
                "The filename looks like a localized Markdown source, but the suffix is not one of the configured locales.",
                "Add the locale under RazorDocs:Localization:Locales or rename the file if the suffix is not a locale."));
        return true;
    }

    private bool TryGetConfiguredLocaleSuffix(string sourcePath, out string? sourceWithoutSuffix, out string suffix)
    {
        sourceWithoutSuffix = null;
        suffix = string.Empty;
        if (!IsMarkdownPath(sourcePath))
        {
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        var withoutExtension = sourcePath[..^extension.Length];
        var lastDot = withoutExtension.LastIndexOf('.');
        if (lastDot < 0 || lastDot == withoutExtension.Length - 1)
        {
            return false;
        }

        suffix = withoutExtension[(lastDot + 1)..];
        if (NormalizeConfiguredLocale(suffix) is null)
        {
            return false;
        }

        sourceWithoutSuffix = withoutExtension[..lastDot] + extension;
        return true;
    }

    private static bool TryGetPotentialLocaleSuffix(string sourcePath, out string suffix)
    {
        suffix = string.Empty;
        if (!IsMarkdownPath(sourcePath))
        {
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        var withoutExtension = sourcePath[..^extension.Length];
        var lastDot = withoutExtension.LastIndexOf('.');
        if (lastDot < 0 || lastDot == withoutExtension.Length - 1)
        {
            return false;
        }

        suffix = withoutExtension[(lastDot + 1)..];
        try
        {
            _ = CultureInfo.GetCultureInfo(suffix);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private bool TryGetFolderLocale(string sourcePath, string? explicitTranslationKey, out string locale)
    {
        locale = string.Empty;
        if (explicitTranslationKey is null)
        {
            return false;
        }

        var firstSegment = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstSegment is null || !_localesByRoutePrefix.TryGetValue(firstSegment, out var configuredLocale))
        {
            return false;
        }

        locale = configuredLocale.Code;
        return true;
    }

    private string? NormalizeConfiguredLocale(string value)
    {
        return _localesByCode.TryGetValue(value.Trim(), out var locale)
            ? locale.Code
            : null;
    }

    private static string GetRoutePrefix(RazorDocsLocaleOptions locale)
    {
        return string.IsNullOrWhiteSpace(locale.RoutePrefix)
            ? locale.Code.Trim()
            : locale.RoutePrefix!.Trim();
    }

    private static string InferTranslationKey(string sourcePath)
    {
        var normalized = NormalizeSourcePath(sourcePath);
        var extension = Path.GetExtension(normalized);
        var withoutExtension = extension.Length == 0 ? normalized : normalized[..^extension.Length];
        var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0
            && (segments[^1].Equals("README", StringComparison.OrdinalIgnoreCase)
                || segments[^1].Equals("index", StringComparison.OrdinalIgnoreCase)))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return segments.Count == 0 ? "README" : string.Join('/', segments);
    }

    private RazorDocsLocaleFallbackMode ResolveDocSetFallbackMode(IEnumerable<LocalizedDocVariant> variants)
    {
        foreach (var variant in variants)
        {
            if (variant.LocaleFallback is not null)
            {
                return variant.LocaleFallback.Value;
            }
        }

        return _options.FallbackMode;
    }

    private static string ResolveTitle(DocNode doc)
    {
        return Normalize(doc.Metadata?.Localization?.LocalizedTitle)
               ?? Normalize(doc.Metadata?.Title)
               ?? doc.Title;
    }

    private static string NormalizeSourcePath(string path)
    {
        return path.Trim().Replace('\\', '/').Trim('/');
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsMarkdownPath(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static DocHarvestDiagnostic CreateDiagnostic(string code, string problem, string cause, string fix)
    {
        return new DocHarvestDiagnostic(code, DocHarvestDiagnosticSeverity.Warning, HarvesterType: null, problem, cause, fix);
    }

    private sealed record ResolvedLocalizationFacts(
        string Locale,
        string TranslationKey,
        string RouteSourcePath,
        bool LocaleWasInferred,
        bool TranslationKeyWasInferred);
}
