namespace AuthWebRazorWireProofExample;

/// <summary>
/// Stable surface labels used by the proof sample response and page.
/// </summary>
/// <remarks>
/// The labels are display/test metadata only. They deliberately avoid adding a RazorWire auth adapter or
/// product API; both surfaces are projections of the same host policy result inside this sample.
/// </remarks>
internal static class AuthProofSurface
{
    /// <summary>
    /// Surface label for the JSON endpoint at <c>/api/auth-proof</c>.
    /// </summary>
    public const string MinimalApi = "Minimal API";

    /// <summary>
    /// Surface label for the rendered RazorWire-facing state on the browser proof console.
    /// </summary>
    public const string RazorWireState = "RazorWire-facing state";
}
