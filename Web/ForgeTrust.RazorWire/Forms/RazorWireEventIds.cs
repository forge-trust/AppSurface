using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Defines stable event identifiers for RazorWire form-related logs.
/// </summary>
internal static class RazorWireEventIds
{
    /// <summary>
    /// Event raised when a RazorWire form anti-forgery validation failure is rewritten into handled form UX.
    /// </summary>
    public static readonly EventId AntiforgeryValidationFailed = new(13600, nameof(AntiforgeryValidationFailed));
}
