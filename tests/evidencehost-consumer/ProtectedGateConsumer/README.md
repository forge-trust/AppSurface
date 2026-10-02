# Protected gate consumer executable

This standalone .NET 10 executable calls the public
[`EvidenceProtectedGate.Allows`](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#runtime-admission-and-gate-evaluation)
API. It adds a bounded stdin transport and fixed process decisions. It does not issue an
admission capability, register a provider, authenticate GitHub, run subject work, or verify a sandbox.
The [consumer acceptance requirements](../../../docs/evidence/issue779-consumer-acceptance.md)
remain unfulfilled by this fixture.

## Protected parent responsibilities

The base-owned parent must pin and protect this executable and its Contracts assembly. It must supply:

- An **expected plan** independently resolved from protected policy and the exact protected diff.
- Current run/attempt, workflow/base/subject revisions, registered verifier, reviewed proof and capability facts.
- Root facts independently captured by the launcher and the actual allocated output identity.
- A collected manifest only after owned work, descendants and pumps have exited and cleanup has completed.
  The parent verifies the actual artifact bytes, sizes and hashes through its protected collector before invoking
  this executable. Artifact metadata in JSON is not a substitute for that verification.
- A protected stdin channel and an independent process deadline. No subject-controlled process may select or
  replace the envelope, executable, assembly or expected facts.

Stdin avoids expected-file path selection; it provides no authentication on its own. An uploaded envelope can be
structurally coherent and return zero. That result has no protected authority unless the parent established the
channel and all prerequisites independently. Never derive expected facts from the received manifest or uploaded
plan. The parent must treat every nonzero exit, missing decision or abnormal termination as an unsuccessful gate.

## Input and decisions

No command-line arguments or input file paths are accepted. Supply exactly one UTF-8 JSON document on stdin
and close the stream. All four properties are required:

```json
{
  "Schema": "evidence-protected-gate-input-v1",
  "Plan": { "...": "the protected expected EvidencePlan" },
  "Manifest": null,
  "Expected": { "...": "the protected EvidenceProtectedGateExpectation" }
}
```

The placeholders illustrate the transport shape; they are not a runnable contract. The complete nested shapes,
including all 16 required expectation strings and the release opt-in that defaults false, are in the
[Contracts reference](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#runtime-admission-and-gate-evaluation).
`Manifest` is explicitly null for missing or quarantined output. Null plans or expectations, missing properties,
unknown transport schemas, duplicate/case-colliding keys, unsupported nested contract versions, invalid enums,
malformed JSON and trailing documents fail closed. Unknown additive properties follow the canonical reader's
compatibility rules and grant no authority.

The public counted reader caps the **whole envelope** at 20 MiB, with JSON depth 64. It observes actual bytes,
rejects the first byte above the limit, and does not trust stream length. Reading has a 30-second cancellation
deadline. The parent must enforce an outer process deadline even if stdin or the operating system does not settle
promptly. This executable never echoes input, exception messages, subject diagnostics or paths.

| Exit | Channel and exact diagnostic | Meaning |
| --- | --- | --- |
| 0 | stdout: `EVIDENCE_GATE_ALLOWED` | The public API allowed the supplied values; provenance is the parent's responsibility. |
| 1 | stderr: `EVIDENCE_GATE_DENIED` | A valid envelope did not satisfy the gate, including Observation or a null manifest. |
| 2 | stderr: `EVIDENCE_GATE_INPUT_REJECTED` | Arguments, input shape, byte limit, schema, JSON or read cancellation rejected. |
| 3 | stderr: `EVIDENCE_GATE_FAILED` | A nonfatal consumer failure; no input details are disclosed. |

Each diagnostic ends with one newline. Runtime-fatal failures are not caught and remain unsuccessful.
`ValidatedNotAttested` describes the registered procedure; it never means independent attestation. A release gate
requires the parent's explicit `AllowReleaseValidatedNotAttested = true` in addition to all other gate checks.

## Build and bounded process controls

From the repository root, with .NET 10 and Python 3:

```sh
dotnet restore tests/evidencehost-consumer/ProtectedGateConsumer/ProtectedGateConsumer.csproj --locked-mode
dotnet build tests/evidencehost-consumer/ProtectedGateConsumer/ProtectedGateConsumer.csproj --no-restore -p:UseSharedCompilation=false
python3 -B tests/evidencehost-consumer/test_protected_gate_consumer.py --consumer tests/evidencehost-consumer/ProtectedGateConsumer/bin/Debug/net10.0/EvidenceHost.ProtectedGateConsumer.dll
```

The project uses a unique non-friend assembly name, references Contracts only, and has no direct package
dependencies. Its checked-in lock records that project reference; NuGet audit is not disabled. It is not included
in the solution or an existing workflow by this preparation.

The Python controls invoke real external executable processes with finite timeouts. Their coherent Trusted-shaped
positive is authored as synthetic public contract JSON and hashes; it obtains no runtime admission and uses no
reflection or internal API. It proves only public API/process behavior. Neighboring wrong facts, structural drift,
Observation, missing manifests and hostile input must reject. The fixture is portable and its macOS results are
not Linux, systemd, protected-positive, coverage-gate or release acceptance evidence.
