# Diagnostics and recovery

Every public failure is a `KeycloakSecurityException`. Its `Code`, `Phase`, `OperationId`, `Effect`, `Recovery`, `NextAction`, `CorrelationId`, `DiagnosticReference`, `ProfileKey`, and `RetryMetadata` are safe structured diagnostics. The `DiagnosticReference` anchors this page by lowercase failure code.

## Failure codes

| Anchor | Meaning and next action |
| --- | --- |
| `#required` | A required JSON field or association is absent. Complete the reviewed input. |
| `#invalid` | A value, path, platform, or bound limit is malformed. Correct the declared input. |
| `#unsupported` | The profile, platform, host, or operation family is outside this package. Use a supported host/profile or coordinate an upgrade. |
| `#conflict` | IDs, destinations, operation order, or independently declared values disagree. Rebuild the complete reviewed JSON. |
| `#materialunavailable` | Approved material could not be acquired or read. Verify the owner-controlled source once; do not substitute a new source. |
| `#provenancerejected` | An origin, coordinate, signer, or association is not authorized. Escalate to the material owner. |
| `#digestmismatch` | Bytes differ from the approved digest or signed index. Preserve the receipt and recapture from the approved source. |
| `#captureinvalid` | Captured bytes, OCI metadata, or package structure are invalid. Do not reuse the snapshot. |
| `#archiveintegrity` | The reviewed archive transformation changed an undeclared byte or metadata role. Retain the evidence and stop. |
| `#archivelimit` | A format or resource bound was exceeded. Lower the input scope or request a reviewed profile change. |
| `#publicationfailed` | A context destination was not atomically published. Inspect the destination before any retry. |
| `#buildfailed` | Host Docker build failed. Inspect the exact context and host builder output; the package does not retry it. |
| `#inspectionfailed` | The actual image does not match the expected files or identity. Check the supplied immutable image identity. |
| `#probefailed` | The read-only runtime probe failed. Treat the image as unverified, inspect the exact image and host probe output, and correct the image or host condition before a fresh invocation. A retry is allowed only when the host has independently classified the read-only probe as transient and no effect was committed. |
| `#evidencenotpublished` | Passing evidence was not produced. Do not describe the run as successful. |
| `#cancelled` | Cancellation interrupted the operation. Inspect effects and start a separately authorized fresh invocation. |
| `#unexpected` | An unexpected safe-boundary failure occurred. Use the correlation ID and diagnostic reference; do not expose a raw cause. |

`KeycloakEffectState.None` permits correction of the declared input. `Staged` means owned temporary data may exist and should be cleaned or inspected according to host policy. `Committed` means an accepted result exists. `Unknown` requires inspection before retry. `KeycloakRetryMetadata` never authorizes automatic retry; at most one explicit unchanged transient acquisition/probe retry may be allowed by the host policy.

The sample's wrong-identity check deliberately changes only the supplied config digest to all zeroes and expects `inspectionfailed` or an earlier safe identity rejection. It does not manufacture a passing image or claim that the production fixture was inspected.
