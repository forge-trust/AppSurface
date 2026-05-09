using System.Text;

namespace ForgeTrust.Runnable.Web.RazorDocs.Models;

/// <summary>
/// Defines the canonical public-section labels, slugs, purpose copy, and alias lookup rules used by RazorDocs.
/// </summary>
internal static class DocPublicSectionCatalog
{
    /// <summary>
    /// Describes one built-in public section, including its canonical display label, stable route slug, and accepted aliases.
    /// </summary>
    private sealed record Definition(
        DocPublicSection Section,
        string Label,
        string Slug,
        string Purpose,
        params string[] Aliases);

    private static readonly Definition[] Definitions =
    [
        new(
            DocPublicSection.StartHere,
            "Start Here",
            "start-here",
            "Orient quickly, verify what the product is for, and follow the strongest proof path.",
            "starthere",
            "gettingstarted",
            "gettingstart",
            "quickstart"),
        new(
            DocPublicSection.Concepts,
            "Concepts",
            "concepts",
            "Build the mental model before you choose an implementation path.",
            "concept",
            "explanation",
            "explanations"),
        new(
            DocPublicSection.HowToGuides,
            "How-to Guides",
            "how-to-guides",
            "Follow task-oriented guides that turn intent into working implementation steps.",
            "howtoguides",
            "howtoguide",
            "howto",
            "guide",
            "guides"),
        new(
            DocPublicSection.Examples,
            "Examples",
            "examples",
            "Inspect concrete, working proof that shows the system behaving in practice.",
            "example"),
        new(
            DocPublicSection.ApiReference,
            "API Reference",
            "api-reference",
            "Dive into namespaces, APIs, and type-level detail once you know what you are looking for.",
            "apireference",
            "api",
            "reference"),
        new(
            DocPublicSection.Releases,
            "Releases",
            "releases",
            "Review changelogs, upgrade guidance, and release-facing project history.",
            "release",
            "changelog",
            "change-log",
            "release-notes",
            "releasenotes",
            "upgrades"),
        new(
            DocPublicSection.Troubleshooting,
            "Troubleshooting",
            "troubleshooting",
            "Recover from failures, debug edge cases, and understand what to check next.",
            "troubleshoot",
            "troubleshootingguide",
            "faq"),
        new(
            DocPublicSection.Internals,
            "Internals",
            "internals",
            "Review contributor-oriented, test, and implementation details that are only shown when explicitly made public.",
            "internal")
    ];

    private static readonly IReadOnlyDictionary<DocPublicSection, Definition> BySection = Definitions.ToDictionary(
        definition => definition.Section);

    private static readonly IReadOnlyDictionary<string, DocPublicSection> ByNormalizedValue = BuildLookup();

    private static readonly IReadOnlyDictionary<string, DocPublicSection> ByNormalizedSlug = BuildSlugLookup();

    private static readonly IReadOnlyList<DocPublicSection> OrderedSectionValues = Definitions
        .Select(definition => definition.Section)
        .ToArray();

    /// <summary>
    /// Gets the public sections in their intended presentation order for docs home and sidebar surfaces.
    /// </summary>
    internal static IReadOnlyList<DocPublicSection> OrderedSections => OrderedSectionValues;

    /// <summary>
    /// Gets the canonical display label for the specified public section.
    /// </summary>
    /// <param name="section">The section whose label should be returned.</param>
    /// <returns>The reader-facing label, such as <c>Start Here</c> or <c>API Reference</c>.</returns>
    internal static string GetLabel(DocPublicSection section)
    {
        return BySection[section].Label;
    }

    /// <summary>
    /// Gets the canonical stable route slug for the specified public section.
    /// </summary>
    /// <param name="section">The section whose slug should be returned.</param>
    /// <returns>A lower-case hyphenated slug suitable for <c>{DocsRootPath}/sections/{slug}</c> routes.</returns>
    internal static string GetSlug(DocPublicSection section)
    {
        return BySection[section].Slug;
    }

    /// <summary>
    /// Gets the short purpose statement used to explain why a public section exists.
    /// </summary>
    /// <param name="section">The section whose purpose copy should be returned.</param>
    /// <returns>The summary text shown in section-first navigation surfaces.</returns>
    internal static string GetPurpose(DocPublicSection section)
    {
        return BySection[section].Purpose;
    }

    /// <summary>
    /// Gets the default stable public route for the specified section.
    /// </summary>
    /// <param name="section">The section whose href should be returned.</param>
    /// <returns>The default <c>/docs/sections/{slug}</c> route for the section.</returns>
    /// <remarks>
    /// This overload always roots the returned href at the default <c>/docs</c> surface. Callers that are rendering
    /// links on the current live surface should prefer <see cref="GetHref(DocPublicSection, string)"/> so custom route
    /// roots and preview roots are honored. The route slug is always produced by
    /// <see cref="GetSlug(DocPublicSection)"/>.
    /// </remarks>
    internal static string GetHref(DocPublicSection section)
    {
        return GetHref(section, "/docs");
    }

    /// <summary>
    /// Gets the canonical public-section route rooted at the caller's current docs surface.
    /// </summary>
    /// <param name="section">The section whose href should be returned.</param>
    /// <param name="docsRootPath">
    /// The pre-normalized, app-relative docs root path to anchor the section route under. Callers are expected to pass
    /// the validated live docs root from <see cref="RazorDocsRoutingOptions.DocsRootPath"/> or
    /// <see cref="Services.DocsUrlBuilder.CurrentDocsRootPath"/>, not an arbitrary or unvalidated value.
    /// </param>
    /// <returns>
    /// The canonical <c>{docsRootPath}/sections/{slug}</c> route for the section, or <c>/sections/{slug}</c> when
    /// <paramref name="docsRootPath"/> is exactly <c>/</c>.
    /// </returns>
    /// <remarks>
    /// This overload exists for mounted live docs surfaces such as <c>/docs/next</c> or root-mounted docs at
    /// <c>/</c>. It special-cases <c>/</c> so root-mounted hosts emit <c>/sections/{slug}</c> instead of
    /// <c>//sections/{slug}</c>. The slug portion is always derived from <see cref="GetSlug(DocPublicSection)"/>.
    /// Passing a non-normalized value such as <c>/docs/</c> can produce incorrect hard-coded links, so callers should
    /// normalize and validate <paramref name="docsRootPath"/> before invoking this overload.
    /// </remarks>
    internal static string GetHref(DocPublicSection section, string docsRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRootPath);
        return string.Equals(docsRootPath, "/", StringComparison.Ordinal)
            ? $"/sections/{GetSlug(section)}"
            : $"{docsRootPath}/sections/{GetSlug(section)}";
    }

    /// <summary>
    /// Resolves an authored section value using canonical labels, canonical slugs, or known aliases.
    /// </summary>
    /// <param name="value">The authored value to resolve.</param>
    /// <param name="section">When this method returns <see langword="true"/>, contains the resolved public section.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> matches a known label, slug, or alias after normalization;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Resolution is case-insensitive and ignores surrounding whitespace plus non-alphanumeric separators so values such as
    /// <c>Start Here</c>, <c>start-here</c>, and <c>start_here</c> all resolve to the same section.
    /// </remarks>
    internal static bool TryResolve(string? value, out DocPublicSection section)
    {
        var normalized = NormalizeKey(value);
        if (normalized.Length == 0)
        {
            section = default;
            return false;
        }

        return ByNormalizedValue.TryGetValue(normalized, out section);
    }

    /// <summary>
    /// Resolves only canonical section-route slugs for <c>/docs/sections/{slug}</c> URLs.
    /// </summary>
    /// <param name="slug">The incoming route slug to resolve.</param>
    /// <param name="section">When this method returns <see langword="true"/>, contains the resolved public section.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="slug"/> matches one canonical section slug after trimming and
    /// case-normalization; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="TryResolve"/>, this method does not accept labels or aliases. Callers that want to support
    /// legacy or user-friendly alias inputs should resolve them separately and redirect to
    /// <see cref="GetHref(DocPublicSection)"/>.
    /// </remarks>
    internal static bool TryResolveSlug(string? slug, out DocPublicSection section)
    {
        var normalized = NormalizeSlug(slug);
        if (normalized.Length == 0)
        {
            section = default;
            return false;
        }

        return ByNormalizedSlug.TryGetValue(normalized, out section);
    }

    /// <summary>
    /// Builds the lookup table used for authored metadata values that may use labels, slugs, or aliases.
    /// </summary>
    /// <returns>A normalized dictionary that maps authored section identifiers to their canonical section enum.</returns>
    private static IReadOnlyDictionary<string, DocPublicSection> BuildLookup()
    {
        var lookup = new Dictionary<string, DocPublicSection>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in Definitions)
        {
            lookup[NormalizeKey(definition.Label)] = definition.Section;
            lookup[NormalizeKey(definition.Slug)] = definition.Section;
            foreach (var alias in definition.Aliases)
            {
                lookup[NormalizeKey(alias)] = definition.Section;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Builds the lookup table used for canonical section-route slugs.
    /// </summary>
    /// <returns>A normalized dictionary that accepts only canonical slugs for section-route resolution.</returns>
    private static IReadOnlyDictionary<string, DocPublicSection> BuildSlugLookup()
    {
        return Definitions.ToDictionary(
            definition => NormalizeSlug(definition.Slug),
            definition => definition.Section,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes authored section metadata so labels, slugs, and aliases can share one lookup table.
    /// </summary>
    /// <param name="value">The authored value to normalize.</param>
    /// <returns>A lowercase alphanumeric key, or an empty string when <paramref name="value"/> is blank.</returns>
    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes an incoming section-route slug while preserving the canonical hyphenated slug contract.
    /// </summary>
    /// <param name="value">The incoming route slug.</param>
    /// <returns>The trimmed lowercase slug, or an empty string when <paramref name="value"/> is blank.</returns>
    private static string NormalizeSlug(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }
}
