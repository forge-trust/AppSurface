# ForgeTrust.AppSurface.Durable.Provider

> **Source-only public preview:** the
> [`PostgreSQL source provider`](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md) supplies the current source-preview
> conformance path, but publication remains held pending coordinated release evidence. This package contains SPI
> contracts, not a runtime.

`ForgeTrust.AppSurface.Durable.Provider` is the runtime-provider and operator SPI for
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md). It depends on that adopter package; the
adopter package never depends on Provider. Production providers implement this public SPI without friend access.

## Choose this package when

- implementing a storage/runtime provider;
- hosting a bounded provider pump explicitly;
- exposing application-authorized health, drain, recovery, or operator operations; or
- adapting a provider claim to an adopter-registered Work executor.

Ordinary applications and reusable modules should reference only `ForgeTrust.AppSurface.Durable`. This package does not
provide PostgreSQL storage, migrations, polling, schedule execution, hosted services, endpoints, metrics, or tracing.

## Slice 7 discovery boundary

This SPI remains part of the source-only public preview; publication is held pending coordinated release evidence.
Provider contracts describe activation and operator boundaries, but storage registration is passive and does not imply
worker hosting. The PostgreSQL provider requires an explicit
[`AddWorkerHost()` opt-in](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host) for continuous
processing.

An opted-in host validates schema compatibility and the active runtime epoch during startup, then fails closed when
they are incompatible. Startup never applies DDL or rewrites migration history. The ordered schema and reconciliation
flow is documented in the [Slice 7 Durable guide](../README.md#slice-7-discovery-and-reconciliation).

## Activation and broker evolution

`IDurableRuntimePump` is the common bounded activation primitive for a continuously hosted loop, scheduled job,
function, HTTP wake-up, or broker notification. A wake-up is advisory: implementations must recover eligible work from
their authoritative state even when notifications are lost, duplicated, delayed, or reordered.

Do not translate broker receipt into a claim, effect permit, or terminal fact. A wake-only adapter must call the pump
without carrying application payloads. A future targeted-dispatch or broker-native provider must revalidate its opaque
reference against authoritative scope, revision, lease, runtime-epoch, and provider-effect state before invoking work.
Slice 2 leaves that adapter shape open until a concrete broker topology proves the required routing and acknowledgement
contract.

## Public API by audience

Every public type in this package belongs to one of these provider-facing families. The
[member-level API snapshot](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.Provider/PublicAPI.Shipped.txt) is the canonical inventory.

| Audience | Public types | Contract role |
|---|---|---|
| Runtime implementers | `DurableRuntimeSurface`, `DurableRuntimePumpRequest`, `DurableRuntimePumpResult`, `IDurableRuntimePump` | Run one bounded, externally activated pass |
| Health and host implementers | `DurableRuntimeHealthState`, `DurableRuntimeHealthSnapshot`, `IDurableRuntimeHealth`, `IDurableRuntimeDrainControl` | Report low-cardinality health and coordinate graceful drain |
| Work-store implementers | `DurableClaimedWork`, `DurablePreparedWorkInvocation`, `DurableProviderWorkAdapter` | Validate a claim, derive immutable execution identity, and invoke the adopter registry |
| Application-authorized control implementers | Work get/cancel/list/snapshot types and `IDurableWorkControlClient`; scope disable types and `IDurableScopeControlClient` | Expose bounded, scoped, payload-free operational control |
| Application-authorized operator implementers | Operator outcome/resolution/result/request types and `IDurableWorkOperatorClient` | Reconcile, resolve, safely retry, or recovery-release suspended Work |
| Application-authorized retention implementers | `IDurableFlowRetentionClient`, bounded assessment/manifest/package/receipt/hold/purge types | Prove one exact terminal Flow source set before a separately authorized purge |

The SPI accepts and returns public Durable identifiers and command fingerprints. Collection results defensively copy
inputs, default identifiers are rejected, timestamps normalize to UTC, page sizes are bounded, and every mutation uses
revision/generation fencing. Provider worker ids, terminal/problem codes, and registered Work names and versions use
the Durable package's canonical [identifier alphabet and bounds](../ForgeTrust.AppSurface.Durable/README.md#durable-identifier-alphabet-and-bounds).

## Provider work adaptation

A provider constructs `DurableClaimedWork` only after it owns a validated claim. `Prepare` maps that claim to the
adopter-facing `DurableWorkExecutionContext` and resolves the registered executor. The resulting
`DurablePreparedWorkInvocation` owns encoded input and exposes only the public invocation boundary.

The execution identity transition is enforceable: create the first identity from an activity id and current fences,
then call `Advance` for a later attempt/lease/scope/runtime epoch. The provider key remains exactly the activity id so
lease turnover cannot create a new external idempotency identity.

## Command fingerprints

Work reconcile, manual resolution, safe retry, and recovery release each use a distinct v1 fingerprint schema. A
provider persists the schema id and digest with command outcome truth. A repeated command id with `UnsupportedSchema` or
`Conflict` fails closed; it must never repeat reconciliation merely because the command id matches.

## Verified Flow retention

`IDurableFlowRetentionClient` is an evidence boundary, not a cleanup scheduler. An application first assesses exactly
one Flow and receives `Safe`, `Blocked`, or `Indeterminate` with a typed reason. Only a still-matching safe assessment
can create an immutable manifest. The provider then builds a reproducible `DFA1` package, records an adopter-supplied
archive receipt, verifies source correspondence, permits an application-owned hold, and accepts a separate
compare-and-swap purge command.

### Canonical retention API reference

| Operation | Request type | Result payload | Required state | Next sequence |
|---|---|---|---|---|
| `AssessAsync` | `DurableRetentionAssessmentRequest` | `DurableRetentionAssessment` | Terminal Flow | N/A |
| `CreateManifestAsync` | `DurableRetentionManifestCreateRequest` | `DurableRetentionManifestCreateResult` | `Safe` assessment | 1 (`Frozen`) |
| `BuildArchivePackageAsync` | `(scopeId, manifestId)` | `DurableArchivePackageV1` | Any active manifest | Unchanged |
| `RecordArchiveReceiptAsync` | `DurableRetentionRecordArchiveReceiptRequest` | `DurableRetentionMutationResult` | `Frozen`, sequence 1 | 2 (`ArchiveReceiptRecorded`) |
| `VerifyArchiveAsync` | `DurableRetentionVerifyArchiveRequest` | `DurableRetentionMutationResult` | `ArchiveReceiptRecorded`, sequence 2 | 3 (`Verified`) |
| `SetHoldAsync` | `DurableRetentionHoldRequest` | `DurableRetentionMutationResult` | `Verified` or `Held` | Next monotonic sequence |
| `PurgeAsync` | `DurableRetentionPurgeRequest` | `DurableRetentionMutationResult` | `Verified`, no active hold | Next monotonic sequence (`Purged`) |

### Lifecycle state sequence and boundaries

The retention lifecycle uses monotonic sequence checks to prevent out-of-order or duplicate execution:

1. **Assessment & Manifest:** `AssessAsync` evaluates one Flow against boundary limits (maximum 10,000 closure items and 64 MiB package bytes). Non-safe outcomes (`Blocked` due to active child Work, nonterminal state, or repair required; `Indeterminate` due to unknown state) forbid manifest creation. `CreateManifestAsync` freezes source hashes and initializes `LifecycleSequence = 1` in `Frozen` state.
2. **Archive & Receipt:** `BuildArchivePackageAsync` constructs the canonical `DFA1` archive byte array. The adopter writes the archive package to external storage and calls `RecordArchiveReceiptAsync` with sequence 1.
3. **Verification:** `VerifyArchiveAsync` validates source SHA-256 correspondence against the frozen manifest, advances state to `Verified`, and increments `LifecycleSequence` to 3.
4. **Hold & Purge:** `SetHoldAsync` can place or release a legal hold (`PlaceHold = true/false`) and increments the sequence for every applied transition. `PurgeAsync` requires state `Verified`, no active hold, and the current sequence. It transitions state to `Purged`, clears terminal payloads, and deletes manifest-covered history rows.

### Common failures and pitfalls

- `ASDUR102` (Command Conflict): Reusing a command identity with different request parameters fails closed.
- `ASDUR214` (Manifest Not Found): Specified manifest ID does not exist in the authorized scope.
- `ASDUR215` (Source Changed): Live Flow source items changed after assessment or manifest creation. Caller must create a new assessment and manifest.
- `ASDUR216` (Sequence Conflict): Expected lifecycle sequence is stale. Reload manifest state before retrying.
- `ASDUR217` (Lifecycle Rejected): Operation attempted out of order (e.g. purging before verification or while a legal hold is active).

The application must authorize every call and owns archive transport, encryption, retention duration, availability,
and legal/compliance requirements. Receipt verification proves the package corresponds to the frozen PostgreSQL source;
it does not prove external bytes are present or adequate. No API accepts an archive URI, raw SQL, age-range deletion,
continuation token, or multi-Flow manifest. See the [PostgreSQL retention deployment guidance](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#verified-flow-retention).

## Operational prerequisites

Before any provider can be published, it must supply storage and migration ownership, polling/schedule execution,
restore fencing, graceful drain, privacy-bounded diagnostics and telemetry, packed-consumer proof, verified-retention
evidence when the retention SPI is implemented, and conformance tests against this SPI. The source-preview PostgreSQL
provider now supplies Work, Flow, Schedule, hosted activation, drain/recovery, and verified-retention conformance;
coordinated release evidence remains the publication gate.

See the [`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md) for currently available contract,
PostgreSQL Work, and hosted-runtime codes.

From the repository root, `./Durable/verify-packed-consumers.sh` packs both held packages and their local dependencies,
then compiles and runs isolated adopter and provider consumers against only those packages.

## Release Guidance

Use the [package chooser](../../packages/README.md) for the machine-enforced publication hold. Versioned publication
evidence and policy live in the [release hub](../../releases/README.md).
