namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>Identifies how a resolved next-page recommendation was selected.</summary>
public enum DocRecommendationSource
{
    /// <summary>The page author selected this destination through related-page metadata.</summary>
    Authored = 0,
    /// <summary>The snapshot metadata policy selected this destination.</summary>
    Automatic = 1
}

/// <summary>A complete next-page row, with stable explanation codes and a directly usable destination.</summary>
/// <remarks>
/// <see cref="DocDetailsViewModel.RecommendedPages"/> contains an uncapped authored prefix followed by at most three
/// automatic rows. Legacy <see cref="DocDetailsViewModel.RelatedPages"/> remains authored-only. Rows and collections
/// returned by the aggregator are detached from its cached snapshot. Custom views can localize reason codes while
/// retaining the page title as the accessible link name. Unknown future codes should use a generic next-read caption.
/// See the <see href="https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#metadata-driven-wayfinding">metadata-driven wayfinding reference</see> for the full selection and compatibility contract.
/// </remarks>
public sealed record DocPageRecommendationViewModel
{
    /// <summary>Gets the resolved destination, including its surface URL, title, optional summary and badge.</summary>
    public DocPageLinkViewModel Page { get; init; } = new();

    /// <summary>Gets whether the row was authored or selected automatically.</summary>
    public DocRecommendationSource Source { get; init; }

    /// <summary>Gets the stable primary explanation code; empty or unknown codes require generic presentation.</summary>
    public string PrimaryReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the primary code once, followed by actual supporting evidence in the documented stable order.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>Gets the default readable caption for the primary explanation.</summary>
    public string ReasonText { get; init; } = string.Empty;
}
