using System.Globalization;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Presentation metadata for one normalized documentation page-type badge.
/// </summary>
public sealed record DocPageTypeBadgePresentation
{
    /// <summary>
    /// Gets the normalized machine-readable page-type value.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// Gets the human-readable badge label.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the badge variant suffix used by built-in RazorDocs CSS classes such as <c>docs-page-badge--guide</c>.
    /// </summary>
    public string Variant { get; init; } = "neutral";
}

/// <summary>
/// Converts raw documentation metadata values into consistent UI-facing labels and badge variants.
/// </summary>
/// <remarks>
/// Use this helper from Razor views, search payload generation, or custom UI surfaces when you want the built-in
/// RazorDocs page-type treatment to remain consistent across landing, detail, and search experiences.
/// </remarks>
public static class DocMetadataPresentation
{
    /// <summary>
    /// Resolves the built-in RazorDocs page-type badge presentation for a raw metadata value.
    /// </summary>
    /// <param name="pageType">The raw page-type metadata value, such as <c>guide</c> or <c>api-reference</c>.</param>
    /// <returns>
    /// A normalized badge presentation when <paramref name="pageType"/> is non-empty; otherwise, <see langword="null"/>.
    /// Unknown page types fall back to a neutral badge with a title-cased label.
    /// </returns>
    public static DocPageTypeBadgePresentation? ResolvePageTypeBadge(string? pageType)
    {
        var normalizedValue = NormalizeToken(pageType);
        if (normalizedValue is null)
        {
            return null;
        }

        var (label, variant) = normalizedValue switch
        {
            "guide" => ("Guide", "guide"),
            "example" => ("Example", "example"),
            "api-reference" => ("API Reference", "api-reference"),
            "internals" => ("Internals", "internals"),
            "how-to" => ("How-To", "how-to"),
            "start-here" => ("Start Here", "start-here"),
            "troubleshooting" => ("Troubleshooting", "troubleshooting"),
            "glossary" => ("Glossary", "glossary"),
            "faq" => ("FAQ", "faq"),
            _ => (BuildFallbackLabel(normalizedValue), "neutral")
        };

        return new DocPageTypeBadgePresentation
        {
            Value = normalizedValue,
            Label = label,
            Variant = variant
        };
    }

    /// <summary>
    /// Normalizes a raw metadata token into a lowercase dash-delimited value.
    /// </summary>
    /// <param name="value">Raw metadata token that may contain whitespace, underscores, dashes, or line breaks.</param>
    /// <returns>
    /// A normalized token, or <see langword="null"/> when <paramref name="value"/> is null, whitespace, or produces
    /// no non-delimiter segments after trimming and splitting.
    /// </returns>
    /// <remarks>
    /// RazorDocs trims the input, splits on spaces, tabs, carriage returns, line feeds, underscores, and hyphens,
    /// removes empty segments, lowercases each remaining segment, and rejoins them with <c>-</c>.
    /// </remarks>
    internal static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var segments = value
            .Trim()
            .Split([' ', '\t', '\r', '\n', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length == 0
            ? null
            : string.Join("-", segments.Select(segment => segment.ToLowerInvariant()));
    }

    private static string BuildFallbackLabel(string normalizedValue)
    {
        var textInfo = CultureInfo.InvariantCulture.TextInfo;

        static string FormatSegment(TextInfo textInfo, string segment)
        {
            return segment switch
            {
                "api" => "API",
                "cli" => "CLI",
                "faq" => "FAQ",
                "sdk" => "SDK",
                "ui" => "UI",
                "ux" => "UX",
                _ => textInfo.ToTitleCase(segment)
            };
        }

        return string.Join(
            " ",
            normalizedValue
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(segment => FormatSegment(textInfo, segment)));
    }
}
