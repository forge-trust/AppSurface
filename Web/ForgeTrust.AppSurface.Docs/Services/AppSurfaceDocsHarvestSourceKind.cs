namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Identifies the content source type being evaluated by the harvest path policy.
/// </summary>
internal enum AppSurfaceDocsHarvestSourceKind
{
    /// <summary>
    /// Markdown documents and repository text pages.
    /// </summary>
    Markdown,

    /// <summary>
    /// C# source files used to generate API reference content.
    /// </summary>
    CSharp
}
