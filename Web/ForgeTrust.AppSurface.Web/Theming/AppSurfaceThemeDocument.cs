namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>
/// Represents the deterministic HTML fragments required to apply one AppSurface theme resolution.
/// </summary>
/// <remarks>
/// The document contains no request-specific values. In particular, <see cref="HeadContent"/> never contains a CSP
/// nonce; the live head TagHelper adds one only to its inline <c>style</c> element. Only the package serializer can
/// create a renderable instance, so adapters cannot accidentally bypass the validated neutral-theme boundary.
/// </remarks>
public sealed record AppSurfaceThemeDocument
{
    /// <summary>
    /// Gets the version of the stable HTML payload emitted by the AppSurface theme TagHelpers.
    /// </summary>
    /// <remarks>
    /// The value is emitted as <c>data-as-theme-schema</c> on the opted-in root. Consumers can use it to validate
    /// static output without inferring a contract from CSS token names.
    /// </remarks>
    public const string SchemaVersion = "1";

    /// <summary>Initializes a theme document from its serialized fragments.</summary>
    /// <param name="rootAttributes">Serialized attributes placed on the theme root.</param>
    /// <param name="rootStyle">The safe <c>color-scheme</c> declaration for the theme root.</param>
    /// <param name="headContent">Serialized head metadata and critical CSS without a nonce.</param>
    internal AppSurfaceThemeDocument(string rootAttributes, string rootStyle, string headContent)
    {
        RootAttributes = rootAttributes ?? throw new ArgumentNullException(nameof(rootAttributes));
        RootStyle = rootStyle ?? throw new ArgumentNullException(nameof(rootStyle));
        HeadContent = headContent ?? throw new ArgumentNullException(nameof(headContent));
        RootThemeId = GetAttributeValue(rootAttributes, "data-as-theme");
        RootThemeMode = GetAttributeValue(rootAttributes, "data-as-theme-mode");
        RootSchemaVersion = GetAttributeValue(rootAttributes, "data-as-theme-schema");
    }

    internal AppSurfaceThemeDocument(
        string rootThemeId,
        string rootThemeMode,
        string rootAttributes,
        string rootStyle,
        string headContent)
        : this(rootAttributes, rootStyle, headContent)
    {
        RootThemeId = rootThemeId ?? throw new ArgumentNullException(nameof(rootThemeId));
        RootThemeMode = rootThemeMode ?? throw new ArgumentNullException(nameof(rootThemeMode));
        RootSchemaVersion = SchemaVersion;
    }

    /// <summary>Gets serialized attributes placed on the theme root.</summary>
    public string RootAttributes { get; }

    /// <summary>Gets the theme identifier for the root metadata.</summary>
    public string RootThemeId { get; }

    /// <summary>Gets the rendered theme mode for the root metadata.</summary>
    public string RootThemeMode { get; }

    /// <summary>Gets the rendered payload schema version for the root metadata.</summary>
    public string RootSchemaVersion { get; }

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

    private static string GetAttributeValue(string attributes, string name)
    {
        var prefix = name + "=\"";
        var start = attributes.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = attributes.IndexOf('"', start);
        return end < 0 ? string.Empty : attributes[start..end];
    }
}
