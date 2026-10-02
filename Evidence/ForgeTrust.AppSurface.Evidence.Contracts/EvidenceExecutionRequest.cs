namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Selects explicit execution mode and the protected launcher's control channel.</summary>
/// <param name="Mode">Trusted or dependency-free Observation; never inferred from environment or branch.</param>
/// <param name="ControlChannel">Absolute Unix socket supplied by the protected Linux launcher.</param>
/// <remarks>
/// Run inside the separately supervised protected worker. This request grants no authority: the execution entry
/// authenticates the root peer, exact worker identity, protected descriptor and registered consumer proof. Embedding
/// into an unrelated long-lived process is unsupported. Windows and macOS have no accepted provider mechanism.
/// Planning and structural verification do not need this request. See the linked EvidenceHost migration guide.
/// </remarks>
public sealed record EvidenceExecutionRequest(EvidenceExecutionMode Mode, string ControlChannel);
