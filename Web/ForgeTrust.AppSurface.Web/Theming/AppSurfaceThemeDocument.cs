namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>
/// Represents the deterministic HTML fragments required to apply one AppSurface theme resolution.
/// </summary>
/// <remarks>
/// The document contains no request-specific values. In particular, <see cref="HeadContent"/> never contains a CSP
/// nonce; the live head TagHelper adds one only to its inline <c>style</c> element.
/// </remarks>
public sealed record AppSurfaceThemeDocument
{
    /// <summary>Initializes a theme document from its serialized fragments.</summary>
    /// <param name="rootAttributes">Serialized attributes placed on the theme root.</param>
    /// <param name="rootStyle">The safe <c>color-scheme</c> declaration for the theme root.</param>
    /// <param name="headContent">Serialized head metadata and critical CSS without a nonce.</param>
    public AppSurfaceThemeDocument(string rootAttributes, string rootStyle, string headContent)
    {
        RootAttributes = rootAttributes ?? throw new ArgumentNullException(nameof(rootAttributes));
        RootStyle = rootStyle ?? throw new ArgumentNullException(nameof(rootStyle));
        HeadContent = headContent ?? throw new ArgumentNullException(nameof(headContent));
    }

    /// <summary>Gets serialized attributes placed on the theme root.</summary>
    public string RootAttributes { get; }

    /// <summary>Gets the safe <c>color-scheme</c> declaration for the theme root.</summary>
    public string RootStyle { get; }

    /// <summary>Gets serialized head metadata and critical CSS without a nonce.</summary>
    public string HeadContent { get; }

    /// <summary>Gets an empty document used when a resolver cannot produce a safe renderable snapshot.</summary>
    public static AppSurfaceThemeDocument Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>Gets a value indicating whether the document contains renderable theme fragments.</summary>
    public bool IsRenderable =>
        RootAttributes.Length > 0
        && RootStyle.Length > 0
        && HeadContent.Length > 0;
}
