namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Defines the conventional paths used by AppSurface's built-in browser-friendly production 500 handling.
/// </summary>
public static class ConventionalExceptionPageDefaults
{
    /// <summary>
    /// Gets the conventional application or shared-library override path for the production exception view.
    /// </summary>
    public const string AppViewPath = "~/Views/Shared/500.cshtml";

    internal const string FrameworkFallbackViewPath = "~/Views/_AppSurface/Errors/500.cshtml";
}
