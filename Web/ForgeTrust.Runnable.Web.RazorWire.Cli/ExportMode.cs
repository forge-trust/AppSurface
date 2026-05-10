namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Selects how RazorWire export output should resolve internal application URLs.
/// </summary>
/// <remarks>
/// The default selection is <see cref="Cdn"/>. Choose <see cref="Cdn"/> for output that will be served directly by a static host or CDN.
/// Choose <see cref="Hybrid"/> only when the exported files will still be hosted behind infrastructure that preserves application routing
/// and dynamic server behavior. New values must only be appended so existing serialized or logged enum values remain stable.
/// </remarks>
public enum ExportMode
{
    /// <summary>
    /// Emits self-contained static-host output and rewrites exporter-managed internal URLs to generated artifacts.
    /// </summary>
    /// <remarks>
    /// This mode enforces static-safety validation for exporter-managed internal URLs and fails when required managed dependencies
    /// cannot be represented as emitted artifacts. It may rewrite extensionless routes to <c>.html</c> artifacts, so use
    /// <see cref="Hybrid"/> instead for routes that depend on runtime server handling.
    /// </remarks>
    Cdn = 0,

    /// <summary>
    /// Preserves application-style internal URLs for deployments that still provide server routing behavior.
    /// </summary>
    /// <remarks>
    /// This mode is for server-backed deployments that can resolve extensionless or dynamic paths. It does not provide the same
    /// CDN static-safety guarantees as <see cref="Cdn"/> for exporter-managed internal URLs.
    /// </remarks>
    Hybrid = 1
}
