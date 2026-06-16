namespace AuthWebRazorWireProofExample.Models;

/// <summary>
/// Browser proof console model that shows the API and RazorWire-facing projections side by side.
/// </summary>
public sealed record AuthProofPageModel(
    string Persona,
    AuthProofState Api,
    AuthProofState RazorWire);
