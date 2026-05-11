namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Defines stable hidden field names used by RazorWire-enhanced form submissions.
/// </summary>
public static class RazorWireFormFields
{
    /// <summary>
    /// Hidden field emitted by <c>form[rw-active]</c> so server-side adapters can
    /// recognize RazorWire form posts even when the client request header is absent.
    /// </summary>
    public const string FormMarker = "__RazorWireForm";

    /// <summary>
    /// Optional hidden field emitted when a form declares a local
    /// <c>data-rw-form-failure-target</c> element that server-side failure adapters can update.
    /// </summary>
    public const string FailureTarget = "__RazorWireFormFailureTarget";
}
