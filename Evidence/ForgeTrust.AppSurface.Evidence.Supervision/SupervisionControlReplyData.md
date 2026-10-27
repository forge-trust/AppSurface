# Root control reply data codec

[`SupervisionControlReplyData`](SupervisionControlReplyData.cs) owns only the JSON encoding of
the five existing root-work replies: application start, application readiness, run output,
retained-report inventory, and an artifact chunk. The control server still performs request
classification, admission and owner checks, calls the original operation, samples the existing
end-of-file result, and sends the bytes through its original connection. The codec has no
transport, delegate, token, filesystem handle, or execution capability.

## Wire reference

The codec preserves the existing JSON field names. As before, the
[canonical serializer](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs)
emits object properties in ordinal order; the lists below describe the fields, not their wire order:

- Application start: `ok`, `lease_id`, `apphost_pid`, `application_uid`, `application_gid`,
  `cgroup`, `owned`.
- Application readiness: `ok`, `lease_id`, `resource_id`, `application_uid`, `cgroup`,
  `kernel_peer_checked`, `http_status`, `healthy`, `received_bytes`.
- Run: `ok`, `exit_code`, `stdout`, `stderr`, `output_truncated`, `received_bytes`.
- Inventory: `ok` and `artifacts`, preserving the input list order and projecting only `path`
  and `length_bytes`.
- Artifact chunk: `ok`, `bytes_base64`, `end`. The caller base64-encodes the returned bytes
  before sampling the existing end flag, preserving the original evaluation order, then passes
  both values to the codec.

Application receipt meaning and validation remain with the existing [Linux application
protocol](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#internal-restricted-application-protocol-v2-prerequisite).
Run and artifact values are serialized exactly as supplied: the codec does not normalize Unicode,
bound data, validate a receipt, or infer success. The empty inventory remains an empty JSON array;
an empty chunk remains an empty base64 string.

## Why this boundary exists

The five replies shared a serialization-only responsibility inside the root control handler.
Keeping those exact data shapes in a small internal codec makes the wire contract directly
reviewable and unit-testable while the caller retains every decision and operation. Use this type
only from the existing control server path. It is not a way to construct an owner, authorize a
request, create an application lease, establish readiness, or bypass native retained-artifact
checks. For protocol framing and connection ordering, see the [supervision control request and
handler documentation](README.md#control-request-data).

## Pitfalls and constraints

- Do not move caller guards, cancellation checks, operation calls, reply ordering, or `IsEnd` sampling into the codec.
- Do not add validation or value rewriting here. The existing application receipt parsers and producer/report readers own those checks.
- Keep `stderr` and other strings as data. Canonical JSON encoding performs wire escaping; callers must not pre-escape them.
- Keep artifact base64 conversion before the retained report `IsEnd` query. The order is
  observable if either operation fails.
- Inventory is a projection of the caller's existing ordered report list, not a new filesystem
  read or authority check.
- A serialized reply is only protocol data. Its fields do not independently establish that any work was admitted or that any native observation occurred.
