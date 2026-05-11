namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Defines stable HTTP header names used by RazorWire-enhanced form submissions.
/// </summary>
public static class RazorWireFormHeaders
{
    /// <summary>
    /// Request header set by the RazorWire runtime for <c>rw-active</c> form submissions.
    /// </summary>
    public const string FormRequest = "X-RazorWire-Form";

    /// <summary>
    /// Response header set when the server has already rendered form-local failure UI.
    /// </summary>
    public const string FormHandled = "X-RazorWire-Form-Handled";
}
