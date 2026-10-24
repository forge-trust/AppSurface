# Diagnostics and recovery

SDK operations report `KeycloakSecurityException`; the strict `KeycloakCanonicalJson` readers report malformed JSON as `JsonException`, which the compiled sample converts to a safe input-error message. Its `Code`, `Phase`, `OperationId`, `Effect`, `Recovery`, `NextAction`, `CorrelationId`, `DiagnosticReference`, `ProfileKey`, and `RetryMetadata` are safe structured diagnostics. The `DiagnosticReference` anchors this page by lowercase failure code.

## Failure codes

| Anchor | Meaning and next action |
| --- | --- |
| <a id="required"></a>`#required` | A required JSON field or association is absent. Complete the reviewed input. |
| <a id="invalid"></a>`#invalid` | A value, path, platform, or bound limit is malformed. Correct the declared input. |
| <a id="unsupported"></a>`#unsupported` | The profile, platform, host, or operation family is outside this package. Use a supported host/profile or coordinate an upgrade. |
| <a id="conflict"></a>`#conflict` | IDs, destinations, operation order, or independently declared values disagree. Rebuild the complete reviewed JSON. |
| <a id="materialunavailable"></a>`#materialunavailable` | Approved material could not be acquired or read. Verify the owner-controlled source once; do not substitute a new source. |
| <a id="provenancerejected"></a>`#provenancerejected` | An origin, coordinate, signer, or association is not authorized. Escalate to the material owner. |
| <a id="digestmismatch"></a>`#digestmismatch` | Bytes differ from the approved digest or signed index. Preserve the receipt and recapture from the approved source. |
| <a id="captureinvalid"></a>`#captureinvalid` | Captured bytes, OCI metadata, or package structure are invalid. Do not reuse the snapshot. |
| <a id="archiveintegrity"></a>`#archiveintegrity` | The reviewed archive transformation changed an undeclared byte or metadata role. Retain the evidence and stop. |
| <a id="archivelimit"></a>`#archivelimit` | A format or resource bound was exceeded. Lower the input scope or request a reviewed profile change. |
| <a id="publicationfailed"></a>`#publicationfailed` | A context destination was not atomically published. Inspect the destination before any retry. |
| <a id="buildfailed"></a>`#buildfailed` | Host Docker build failed. Inspect the exact context and host builder output; the package does not retry it. |
| <a id="inspectionfailed"></a>`#inspectionfailed` | The actual image does not match the expected files or identity. Check the supplied immutable image identity. |
| <a id="probefailed"></a>`#probefailed` | The read-only runtime probe failed. Treat the image as unverified, inspect the exact image and host probe output, and correct the image or host condition before a fresh invocation. A retry is allowed only when the host has independently classified the read-only probe as transient and no effect was committed. |
| <a id="evidencenotpublished"></a>`#evidencenotpublished` | Passing evidence was not produced. Do not describe the run as successful. |
| <a id="cancelled"></a>`#cancelled` | Cancellation interrupted the operation. Inspect effects and start a separately authorized fresh invocation. |
| <a id="unexpected"></a>`#unexpected` | An unexpected safe-boundary failure occurred. Use the correlation ID and diagnostic reference; do not expose a raw cause. |

`KeycloakEffectState.None` permits correction of the declared input. `Staged` means owned temporary data may exist and should be cleaned or inspected according to host policy. `Committed` means an accepted result exists. `Unknown` requires inspection before retry. `KeycloakRetryMetadata` never authorizes automatic retry; at most one explicit unchanged transient acquisition/probe retry may be allowed by the host policy.

The sample's wrong-identity check deliberately changes only the supplied config digest to all zeroes and expects `inspectionfailed` or an earlier safe identity rejection. It does not manufacture a passing image or claim that the production fixture was inspected.


## Four recovery walkthroughs

These are recovery procedures for actual caller outcomes, not claims that every environmental failure has been executed in this checkout. Follow the actual typed code/effect when it is available; a host process failure can occur outside the SDK.

| Scenario | What failed and effects | Safe identity to inspect | Next action |
| --- | --- | --- | --- |
| Pre-capture rejection | A duplicate `artifact-alignment` destination yields `Conflict` in Validation, effects `None`; no accepted snapshot exists. | Operation ID, duplicate image-internal destination, canonical recipe digest and independently approved policy digest. | Correct the complete declared group or obtain policy-owner approval for an actual authority change. Recompute `reviewedRecipeId`; capture into a fresh absent directory. Never retry an unchanged invalid declaration. |
| Host-build failure | Docker cannot start or exits nonzero after context creation. The context is committed; the host must classify the image outcome, which may be absent, partial, or unknown. | Context digest and `Verify()` result; explicit local tag resolved to immutable manifest/config/platform if an image exists. | Correct Docker/store/capacity prerequisites. Inspect the local tag before deciding whether a fresh build is safe. Revalidate the same owned context immediately before building; a changed context requires rematerialization. No evidence is minted by a successful build alone. |
| Inspection mismatch | For example, the supplied config digest is wrong: `InspectionFailed` or an earlier safe identity rejection; no passing evidence is returned. | Actual manifest/config/platform/diff IDs, plan/recipe/policy digests, and the failing file/check when reported. | Resolve the actual image again. If the image differs, correct the source/handler/composition, rebuild, and inspect that final digest. Never edit observed evidence or replace an observed hash with the expected value. |
| Unknown commit acknowledgement | The caller loses acknowledgement around snapshot/context/evidence publication. Treat effects as `Unknown` until inspected, even if a subsequent invocation cannot see the earlier result. | Exact requested output role/path in private host logs, expected plan/context/image digests, complete committed inventory and evidence schema. | Stop retries. Inspect the actual destination: accept only a complete verified result matching the intended invocation; otherwise preserve it for diagnosis and choose a new absent destination after classifying effects. Never overwrite or delete another invocation's output to force success. |

The compiled [sample entry point](../../../examples/auth-keycloak-security-recipes/Program.cs) handles safe input failures and the documented wrong-identity control. [Sample instructions](../../../examples/auth-keycloak-security-recipes/README.md) define exit codes and fresh output paths. An existing output is a conflict to preserve, not permission to overwrite it. See [material recovery](material-retention.md) for unavailable or withdrawn inputs.

## Safe support reproduction

The profile/security-response owner is listed in the [ownership guide](profile-lifecycle.md). For a non-sensitive defect use [GitHub issues](https://github.com/forge-trust/AppSurface/issues/new/choose) and the [contribution guide](../../../CONTRIBUTING.md). Suspected vulnerabilities, credentials, exploit details, or sensitive deployment facts belong in the [private security reporting flow](../../../SECURITY.md), not a public issue. The repository uses the [PolyForm Small Business license](../../../LICENSE); consult its actual terms and dependency notices before redistribution.

Copy this template into the appropriate report and replace fields with safe observations. Omit a field if it cannot be shared safely; say `unavailable`, never invent a value.

```text
Package/coherent version or reviewed commit:
Recipe schema / source profile / runtime and archive profile versions:
.NET SDK/runtime / host OS+architecture / Docker version+store:
Target image platform / inspector+transformer versions:
Stable failure code / DiagnosticReference:
Phase / operation ID / check ID if reported:
Effect / recovery / RetryMetadata:
CorrelationId (from the structured exception):
Safe plan / recipe / policy / manifest / config digests:
Expected vs observed safe identity (no source-machine path):
Smallest synthetic reproduction and exact bounded action:
Prior output preserved? Owned cleanup outcome? Attempts already made?:
What is unavailable or unverified?:
```

Never attach credentials, realm exports, environment dumps, private material URLs, home-directory paths, raw nested exception messages, or unbounded Docker output. Keep restricted host logs private. Public reproduction inputs must be synthetic or explicitly approved for disclosure. A correlation ID routes a report; it does not grant access to private artifacts.
