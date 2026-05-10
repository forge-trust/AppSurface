namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Identifies the HTML or CSS surface where an export reference was found.
/// </summary>
internal enum ExportReferenceKind
{
    /// <summary>An anchor <c>href</c> attribute.</summary>
    AnchorHref,

    /// <summary>A Turbo Frame <c>src</c> attribute.</summary>
    TurboFrameSrc,

    /// <summary>A script <c>src</c> attribute.</summary>
    ScriptSrc,

    /// <summary>A supported link <c>href</c> attribute such as a stylesheet, icon, preload, or prefetch.</summary>
    LinkHref,

    /// <summary>An image <c>src</c> attribute.</summary>
    ImgSrc,

    /// <summary>One URL candidate inside a <c>srcset</c> attribute.</summary>
    ImgSrcSet,

    /// <summary>A CSS <c>url(...)</c> reference from a stylesheet, style block, or style attribute.</summary>
    CssUrl
}
