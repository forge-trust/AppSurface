namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Represents the host-neutral severity used for Tailwind CLI output.
/// </summary>
internal enum TailwindOutputLevel
{
    /// <summary>
    /// Output that is useful only for verbose troubleshooting, such as empty progress lines.
    /// </summary>
    Debug,

    /// <summary>
    /// Normal operational output, such as Tailwind version banners and completion messages.
    /// </summary>
    Information,

    /// <summary>
    /// Output that represents warnings or failures requiring build or developer attention.
    /// </summary>
    Error
}
