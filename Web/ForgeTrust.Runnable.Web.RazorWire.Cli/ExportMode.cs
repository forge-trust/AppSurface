namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Selects how RazorWire export output should resolve internal application URLs.
/// </summary>
public enum ExportMode
{
    /// <summary>
    /// Emits self-contained static-host output and rewrites exporter-managed internal URLs to generated artifacts.
    /// </summary>
    Cdn,

    /// <summary>
    /// Preserves application-style internal URLs for deployments that still provide server routing behavior.
    /// </summary>
    Hybrid
}
