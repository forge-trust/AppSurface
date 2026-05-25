namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Describes how Turbo Drive should apply a RazorWire visit stream command to browser history.
/// </summary>
/// <remarks>
/// Visit stream commands are one-shot navigation messages. Publish them only to live stream subscribers; do not retain
/// or replay them through a RazorWire stream hub replay buffer. Use normal links, redirects, or retained state streams
/// when late subscribers or JavaScript-disabled clients need a fallback.
/// </remarks>
public enum RazorWireVisitAction
{
    /// <summary>
    /// Pushes a new history entry for the visited URL, matching Turbo Drive's default <c>advance</c> action.
    /// </summary>
    Advance = 0,

    /// <summary>
    /// Replaces the current history entry with the visited URL, matching Turbo Drive's <c>replace</c> action.
    /// </summary>
    Replace = 1
}
