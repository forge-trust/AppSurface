# ForgeTrust.AppSurface.Durable.Provider

> **Source-only public preview:** this package is intentionally excluded from all repository publish plans until the
> PostgreSQL provider milestone supplies conformance and restore evidence. It contains SPI contracts, not a runtime.

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

## Operational prerequisites

Before any provider can be published, it must supply storage and migration ownership, polling/schedule execution,
restore fencing, graceful drain, privacy-bounded diagnostics and telemetry, packed-consumer proof, and conformance tests
against this SPI. Slice 2 deliberately does not run downstream provider conformance because no provider exists yet.

See the [`ASDURxxx` diagnostics catalog](../../troubleshooting/durable-diagnostics.md) for currently available contract
codes and explicitly reserved provider codes.

From the repository root, `./Durable/verify-packed-consumers.sh` packs both held packages and their local dependencies,
then compiles and runs isolated adopter and provider consumers against only those packages.

## Release Guidance

Use the [package chooser](../../packages/README.md) for the machine-enforced publication hold. Versioned publication
evidence and policy live in the [release hub](../../releases/README.md).
