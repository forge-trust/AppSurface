namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Controls how the AppSurface DevAuth marker renders inside a local or proof host page.
/// </summary>
/// <remarks>
/// The marker is an explicit opt-in snippet for pages where fake auth state should be visible. It never injects itself
/// into host HTML. Consumers can disable the default inline styles and provide their own classes when the marker needs to
/// match a local design system. The default styles use a fixed desktop overlay above 640 CSS pixels and normal document
/// flow at widths up to and including 640 CSS pixels. The host remains responsible for viewport metadata, render
/// location, outer spacing, containing layout, and any CSS overrides.
/// </remarks>
public sealed class AppSurfaceDevAuthMarkerOptions
{
    /// <summary>
    /// Gets or sets the CSS class prefix used for every marker element.
    /// </summary>
    /// <remarks>
    /// The prefix replaces every package-emitted marker selector, including descendant selectors in the default inline
    /// styles. Changing it does not disable the package's responsive behavior. Use the new prefix for host overrides, or
    /// set <see cref="IncludeDefaultStyles" /> to <see langword="false" /> when the host will provide the complete skin.
    /// </remarks>
    public string CssClassPrefix { get; set; } = AppSurfaceDevAuthStaticExportMarkers.MarkerCssClass;

    /// <summary>
    /// Gets or sets an extra CSS class appended to the marker root element.
    /// </summary>
    /// <remarks>
    /// Use this host-owned class for outer spacing or a deliberate placement override. Package styles are emitted with
    /// the configured <see cref="CssClassPrefix" />, so placement overrides may need higher specificity or later source
    /// order. The host owns overlap prevention whenever it overrides the package placement rules.
    /// </remarks>
    public string? AdditionalCssClass { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the marker includes package-provided default styles.
    /// </summary>
    /// <remarks>
    /// When enabled, the package renders a fixed bottom-right overlay above 640 CSS pixels and an in-flow marker at
    /// widths up to and including 640 CSS pixels. Set this to <see langword="false" /> when the host wants to own all
    /// marker styling and responsive placement. The host must supply its own styles in that case.
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
    /// The default is collapsed so the DevAuth state remains visible with a smaller desktop footprint. Set this to
    /// <see langword="true" /> when a proof page should show the persona controls immediately. At narrow widths the
    /// default-styled expanded marker remains in normal flow and pushes following content instead of overlaying it.
    /// </remarks>
    public bool StartExpanded { get; set; }

    /// <summary>
    /// Gets or sets the local URL to return to after a marker persona mutation.
    /// </summary>
    /// <remarks>
    /// Leave this unset to return to the current request path and query. The marker normalizes blank, non-rooted,
    /// external, protocol-relative, backslash-containing, and control-character values to the site root before
    /// constructing its mutation actions.
    /// </remarks>
    public string? ReturnUrl { get; set; }
}
