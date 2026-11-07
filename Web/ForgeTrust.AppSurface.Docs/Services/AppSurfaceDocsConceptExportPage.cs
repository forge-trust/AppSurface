namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Authored concept metadata for one public canonical winner in the current Docs snapshot.</summary>
/// <param name="CanonicalRoutePath">The canonical route spelling selected by the final route identity catalog.</param>
/// <param name="Locale">Normalized configured locale, or null when the current Docs surface has no locale identity.</param>
/// <param name="ConceptId">Validated authored concept ID, or null when unspecified.</param>
/// <param name="Title">Resolved reader-facing page title.</param>
/// <param name="SectionLanding">Whether this exact page won a resolved public section landing selection.</param>
/// <param name="ConceptIdIsInvalid">Whether an authored concept ID was rejected and must fail authored export.</param>
internal sealed record AppSurfaceDocsConceptExportPage(
    string CanonicalRoutePath,
    string? Locale,
    string? ConceptId,
    string Title,
    bool SectionLanding,
    bool ConceptIdIsInvalid);
