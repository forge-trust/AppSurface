namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Controls how the AppSurface DevAuth marker renders inside a local or proof host page.
/// </summary>
/// <remarks>
/// The marker is an explicit opt-in snippet for pages where fake auth state should be visible. It never injects itself
/// into host HTML. Consumers can disable the default inline styles and provide their own classes when the marker needs to
/// match a local design system.
/// </remarks>
public sealed class AppSurfaceDevAuthMarkerOptions
{
    /// <summary>
    /// Gets or sets the CSS class prefix used for every marker element.
    /// </summary>
    public string CssClassPrefix { get; set; } = AppSurfaceDevAuthStaticExportMarkers.MarkerCssClass;

    /// <summary>
    /// Gets or sets an extra CSS class appended to the marker root element.
    /// </summary>
    public string? AdditionalCssClass { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the marker includes package-provided default styles.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false" /> when the host wants to skin the marker entirely with its own CSS.
    /// </remarks>
    public bool IncludeDefaultStyles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether persona select and clear controls are rendered in the marker.
    /// </summary>
    /// <remarks>
    /// When enabled, controls use POST-only DevAuth endpoints and include a safe local return URL so the browser returns
    /// to the host page after changing personas.
    /// </remarks>
    public bool ShowPersonaControls { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the marker starts expanded.
    /// </summary>
    /// <remarks>
    /// The default is collapsed so the DevAuth state remains visible without covering local app content. Set this to
    /// <see langword="true" /> when a proof page should show the persona controls immediately.
    /// </remarks>
    public bool StartExpanded { get; set; }

    /// <summary>
    /// Gets or sets the local URL to return to after a marker persona mutation.
    /// </summary>
    /// <remarks>
    /// Leave this unset to return to the current request path and query. External URLs, protocol-relative URLs, and
    /// backslash-prefixed URLs are ignored by the DevAuth endpoints.
    /// </remarks>
    public string? ReturnUrl { get; set; }
}
