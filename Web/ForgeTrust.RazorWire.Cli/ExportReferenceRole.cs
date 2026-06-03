namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Classifies how an exporter-managed reference participates in mode-specific validation policy.
/// </summary>
/// <remarks>
/// The role is internal CLI metadata derived from the discovered reference kind plus nearby markup evidence such as
/// <c>link</c> <c>rel</c> and <c>as</c> attributes. It separates reference facts from validation policy so hybrid export can
/// require browser-delivered static assets without treating page, live, or metadata references as CDN-owned assets.
/// </remarks>
internal enum ExportReferenceRole
{
    /// <summary>The reference points at a navigable page or route.</summary>
    PageRoute,

    /// <summary>The reference points at runtime behavior that hybrid infrastructure may serve live.</summary>
    LiveRoute,

    /// <summary>The reference points at a browser-delivered static asset that must materialize during export.</summary>
    StaticAsset,

    /// <summary>The reference is metadata and should not trigger hybrid static-asset validation.</summary>
    Metadata
}
