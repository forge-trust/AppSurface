namespace AuthWebRazorWireProofExample.Models;

/// <summary>
/// Browser proof console model that shows the API and RazorWire-facing projections side by side.
/// </summary>
/// <param name="Persona">
/// The normalized sample persona rendered by the browser console. Expected values are <c>anonymous</c>,
/// <c>viewer</c>, or <c>operator</c>; unknown proof inputs are normalized to <c>anonymous</c> before this
/// model is created.
/// </param>
/// <param name="Api">
/// The canonical state produced for the Minimal API proof surface. Use this when rendering or testing the
/// JSON-facing result rather than re-evaluating the policy in the view.
/// </param>
/// <param name="RazorWire">
/// The canonical state projected into the RazorWire-facing page. It should match <paramref name="Api"/>
/// for outcome, reason, subject, and status so the sample proves UI/API parity.
/// </param>
/// <remarks>
/// This model is sample-local glue for the proof console. Production apps should render their own state
/// from host-owned auth decisions instead of treating this record as a reusable UI adapter.
/// </remarks>
public sealed record AuthProofPageModel(
    string Persona,
    AuthProofState Api,
    AuthProofState RazorWire);
