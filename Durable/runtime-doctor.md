# Diagnose one connected Durable runtime

`appsurface durable doctor` reads one connected store using a restricted runtime credential. It reports schema compatibility, configured versus active epoch, local heartbeat-retention structure, and optionally one selected heartbeat. A clean result says **store/runtime checks passed** at the captured database observation. Run your application's composition verifier afterward; the [external-activation reference and verifier](../examples/durable-external-activation/README.md) show that separate proof.

Doctor never applies migrations, initializes or rotates an epoch, drains a worker, retries Work, prunes heartbeat rows, registers a worker, or executes application code. The [complete runtime-set preflight](heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist) remains the deployment approval procedure, with its complete reviewed role manifest and independently reviewed migration-owner identity.

## Start with the store

Install the supporting coordinated [CLI preview](../Cli/ForgeTrust.AppSurface.Cli/README.md) on a .NET 10 host. This command is first available in the candidate containing issue #801; no published supporting version is claimed until its release evidence passes. Choose that same coordinated version for the CLI and the [PostgreSQL provider](ForgeTrust.AppSurface.Durable.PostgreSql/README.md). Follow the [disposable reference setup](../examples/durable-external-activation/README.md#executable-local-first-start) to provision schema 11, distinct roles and an initialized epoch, or use your existing reviewed deployment. The endpoint must preserve one physical session; transaction or statement pooling cannot supply this evidence.

Your existing secret tool or CI environment injection must supply the selected connection variable with the **runtime** credential and the selected epoch variable with the deployed host's nonempty epoch GUID. With the reference environment mapping already configured, run:

```bash
appsurface durable doctor \
  --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION \
  --runtime-epoch-env APPSURFACE_DURABLE_RUNTIME_EPOCH
```

Matching epoch, schema and retention structure produce exit 0. Credential, schema, epoch and retention checks are `passed`; worker is `not-requested`, and `worker` is null in JSON. There is no heartbeat claim. The final action requests the **consumer verifier command** without inventing it. Run the verifier owned by your application to test registration, authorization, routing and execution.

For a selected worker, first read the deployed host's effective `HeartbeatStaleAfter` from its options/configuration and check it exceeds `IdlePollingInterval`. Doctor cannot establish that host invariant because it has no polling input. The reference has no override of the provider's 15-second default; the following value is only a reference fixture value:

```bash
appsurface durable doctor \
  --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION \
  --runtime-epoch-env APPSURFACE_DURABLE_RUNTIME_EPOCH \
  --worker-id doctor-fixture-801 --stale-after 15s \
  --timeout 10s --format json
```

Use your actual host threshold and a non-secret operator-chosen worker label in a deployment. A current heartbeat is an observation compared with that supplied threshold. It proves neither process ownership nor completed Work, and does not promise future readiness.

## Inputs and exits

| Option | Default and constraints |
| --- | --- |
| `--connection-env` | `APPSURFACE_DURABLE_CONNECTION`; selected environment name, at most 200 characters, using the schema-command name grammar. Blank or missing selected value is invalid; no fallback. Connection must parse as PostgreSQL configuration. |
| `--runtime-epoch-env` | `APPSURFACE_DURABLE_RUNTIME_EPOCH`; same name constraints; selected value must be a nonempty GUID. |
| `--worker-id` | No default. Exactly 1–200 ASCII letters, digits, `-`, `_`, `.`, `:`; no trimming or normalization. Choose a label without personal or secret data. |
| `--stale-after` | No default; required exactly when worker ID is supplied. Inclusive 1 second–1 hour; must be the host's actual setting. |
| `--timeout` | `10s`; inclusive 1 second–2 minutes, including connection, fence wait, reads and cleanup. |
| `--format` | `text`; only `text` or `json`. |

Durations use positive decimal numbers with lowercase `ms`, `s`, `m`, `h`, invariant culture, exact TimeSpan ticks and at most 64 characters. Signs, exponents, overflow, sub-tick values, controls and nonfinite numbers fail. Duplicate or unknown flags, missing values and positional arguments fail before database access. There are no literal connection/epoch arguments, file fallbacks or prompts. Environment values are resolved once and are never printed. Custom names replace the defaults and remain in retry/status actions so the next command targets the same selected store.

| Exit | Meaning | Next step |
| --- | --- | --- |
| 0 | All requested checks passed | Run the application's composition verifier. |
| 2 | Complete actionable observation | Perform the fixed finding's operator prerequisite, then execute its next command. Schema incompatibility uses schema status with the selected connection name; other findings use the validated doctor retry. |
| 3 | Invalid input/environment | Read `appsurface durable doctor --help`, correct inputs and supply the restricted runtime credential. |
| 4 | Dependency/deadline/affinity/cleanup unavailable | Restore connectivity, read permissions and session affinity, or unblock the cooperative maintenance fence, then retry deliberately. |
| 1 | Caller canceled or unexpected CLI/package failure | Retry only when intended after cancellation; investigate matching package contracts and [canonical diagnostics](../troubleshooting/durable-diagnostics.md) for unexpected failure. |

Every finding contains canonical problem/cause/fix/documentation and its applicable next action. The command performs no automatic retry. A broken output sink remains exit 1. Input failure chooses JSON only when the format selection is uniquely valid; ambiguous or malformed format uses fixed text without echoing arguments.

## Version-one JSON contract

JSON is one envelope on stdout followed by one newline, with no progress or provider logger text. Consumers must require `schemaVersion == 1`, validate status/exit consistency and check requested-check statuses before interpreting nullable facts. A clean result requires credential, schema, epoch and retention to be requested and passed; worker may be unrequested, or requested and passed. Preserve nonzero process exit alongside the report. Unsupported versions require an explicit consumer upgrade; field shapes are versioned.

The [minimal automation consumer](https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/consume-doctor-report.py) validates those rules and preserves the captured command exit. Save JSON and exit together even when doctor finds a problem:

```bash
set +e
appsurface durable doctor \
  --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION \
  --runtime-epoch-env APPSURFACE_DURABLE_RUNTIME_EPOCH \
  --format json > doctor-report.json
doctor_exit=$?
set -e
python3 examples/durable-external-activation/consume-doctor-report.py doctor-report.json "$doctor_exit"
```

The consumer prints actions without executing them. An exit-0 report has a null command and requires the application's verifier; that handoff remains application-owned.

This complete store-only sample is generated by the production renderer from fixed disposable fixture facts; the captured time and identities are examples, not live readiness evidence.

```json
{
  "schemaVersion": 1,
  "status": "passed",
  "exitCode": 0,
  "requestedChecks": [
    {
      "name": "credential",
      "requested": true,
      "status": "passed"
    },
    {
      "name": "schema",
      "requested": true,
      "status": "passed"
    },
    {
      "name": "epoch",
      "requested": true,
      "status": "passed"
    },
    {
      "name": "retention",
      "requested": true,
      "status": "passed"
    },
    {
      "name": "worker",
      "requested": false,
      "status": "not-requested"
    }
  ],
  "observedAtUtc": "2026-10-03T12:00:00.0000000+00:00",
  "schema": {
    "compatibility": "compatible",
    "installedVersion": 11,
    "requiredVersion": 11,
    "minimumReaderVersion": 1,
    "maximumReaderVersion": 11,
    "minimumWriterVersion": 1,
    "maximumWriterVersion": 11,
    "appliedVersions": [
      1,
      2,
      3,
      4,
      5,
      6,
      7,
      8,
      9,
      10,
      11
    ],
    "pendingVersions": []
  },
  "storeId": "88164257-2a2f-42b4-9832-18a649888802",
  "configuredRuntimeEpoch": "88164257-2a2f-42b4-9832-18a649888801",
  "activeRuntimeEpoch": "88164257-2a2f-42b4-9832-18a649888801",
  "credential": {
    "restricted": true,
    "failedChecks": []
  },
  "retention": {
    "available": true,
    "failedChecks": []
  },
  "worker": null,
  "findings": [],
  "nextAction": {
    "kind": "application-verifier",
    "command": null,
    "requiredInputs": [
      "consumer verifier command"
    ],
    "documentationUrl": "https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/README.md"
  }
}
```

Top-level properties always appear in this order: `schemaVersion`, `status`, `exitCode`, `requestedChecks`, `observedAtUtc`, `schema`, `storeId`, `configuredRuntimeEpoch`, `activeRuntimeEpoch`, `credential`, `retention`, `worker`, `findings`, `nextAction`. Status is `passed`/0, `findings`/2, `invalid-input`/3, `unavailable`/4, `canceled`/1 or `failed`/1.

`requestedChecks` always contains credential, schema, epoch, retention and worker objects, each with `name`, `requested`, `status`. Status is `passed`, `finding`, `not-checked` or `not-requested`. The first four are requested for valid input. Invalid input requests none. Database failures discard every database fact and timestamp; only the validated configured epoch and check intent survive. An unsafe observed credential short-circuits other checks. An incompatible schema leaves epoch, retention and requested worker `not-checked`.

GUIDs use lowercase canonical `D` form. Timestamps use normalized UTC round-trip `O` form, captured by one materialized database-clock expression. Tick values are signed 64-bit JSON integers. Nulls are always serialized, qualified by check status; they are never independently interpreted as success. The bounded envelope contains at most 12 unique findings and at most 32 KiB.

| Object | Properties in exact order |
| --- | --- |
| Schema | `compatibility`, `installedVersion`, `requiredVersion`, `minimumReaderVersion`, `maximumReaderVersion`, `minimumWriterVersion`, `maximumWriterVersion`, `appliedVersions`, `pendingVersions` |
| Credential | `restricted`, `failedChecks` |
| Retention | `available`, `failedChecks` |
| Worker | `workerId`, `found`, `state`, `runtimeEpoch`, `lastHeartbeatAtUtc`, `ageTicks`, `staleAfterTicks`, `isDraining` |
| Finding | `code`, `problem`, `cause`, `fix`, `documentationUrl`, `failedChecks`, `nextAction` |
| Action | `kind`, `command`, `requiredInputs`, `documentationUrl` |
| Command | `executable`, `arguments` |

Schema compatibility is `compatible`, `missing`, `upgrade-required`, `store-too-new` or `inconsistent`. Required version is the executing package's fact. Missing schema has installed version 0, null reader/writer bounds, empty applied history and package pending migrations. Inconsistent schema retains only its verdict and required version; all other fields and store/active epoch are null. Other complete schema states retain validated metadata and ascending unique version arrays, each bounded to 64. A schema check failure does not authorize interpreting runtime tables.

Doctor rejects migration history outside its bounded observation contract before classifying compatibility. Every observed migration name must fit the executing catalog's maximum ASCII name length and grammar, and every digest must be exactly 64 lowercase hexadecimal bytes. This includes future migration rows: a newer store with a longer name can return `ASDUR415` rather than `ASDUR402`. Use the matching coordinated CLI/provider version and [schema status](ForgeTrust.AppSurface.Durable.PostgreSql/README.md) for further investigation; doctor never truncates oversized evidence into a compatibility result.

Worker states are `not-started`, `current`, `stale`, `draining`, `epoch-incompatible`. Store epoch mismatch overrides worker findings and emits `ASDUR108` once even when no row exists. With matching store epoch, evaluate missing row, row epoch mismatch, draining, age strictly greater than threshold, then current. Age is `max(0, observedAtUtc - lastHeartbeatAtUtc)`. A missing row has null row epoch/time/age/drain; a found row has all four. Missing retained heartbeat can mean no start, a different worker label, or a row removed by retention.

Findings sort credential, schema, epoch, retention, worker; terminal outcomes emit only their terminal finding. Categories are fixed and ordered:

| Family | Categories |
| --- | --- |
| Input/session | `input`, `session-affinity` |
| Credential | `caller-role`, `role-attributes`, `role-membership`, `role-ownership`, `role-grant-options`, `heartbeat-table-privileges` |
| Schema/epoch | `schema-compatibility`, `active-epoch` |
| Retention | `function-signature`, `function-owner`, `function-security-definer`, `function-search-path`, `function-execute`, `function-public-acl`, `function-grant-option`, `retention-index-presence`, `retention-index-shape` |
| Worker | `worker-heartbeat-missing`, `worker-heartbeat-epoch`, `worker-heartbeat-stale`, `worker-draining` |
| Terminal | `dependency`, `deadline`, `cleanup`, `caller-canceled`, `catalog-contract` |

Missing function or index reports its absence category alone. Present malformed structures can have multiple categories in one retention finding. Arbitrary/duplicate/oversized categories fail the contract; evidence is never truncated into success.

Finding actions have `kind: command`, executable `appsurface`, validated argv, no required inputs and a canonical HTTPS documentation URL. Clean action has `kind: application-verifier`, `command: null` and exactly `["consumer verifier command"]`. The top-level action is the first finding's action, or the clean verifier action. JSON argv avoids shell ambiguity; text displays the same action safely.

## Evidence and implementation boundaries

Credential safety requires the same session/current role, login without elevated attributes, no memberships in either direction, no database or Durable object ownership or effective grant options, and no heartbeat DELETE/TRUNCATE privilege. Permission denial preventing complete observation is unavailable, not evidence of elevation.

Retention inspects migration **0011**'s exact `prune_runtime_heartbeats(interval,integer,text,uuid)` function signature, owner shape, definer/search path, connected-role EXECUTE/ACL and exact named btree index shape. It does not invoke the function. Migration 0010 is the separate health-observation migration. Local owner equality cannot detect consistent owner reassignment against an independently reviewed identity; preflight owns that authority. Doctor also cannot prove function-body correctness, maintenance outcomes or actual pruning effectiveness.

Internal `IDurableDoctorService` separates private connection custody from printable `DurableDoctorRequest`; copied `DurableDoctorObservation` supplies pure classification and rendering. `DurableDoctorBudget` and stage observer seams permit monotonic/cancellation verification without reflection. The authoritative schema reader has a doctor-only bounded history seam; public status/preflight behavior remains compatible. Shared retention predicates are consumed by doctor and complete preflight, while their ACL/owner authorities remain distinct. The session fence precedes the repeatable-read snapshot and remains owned through transaction completion and explicit release.

The [canonical descriptor catalog](ForgeTrust.AppSurface.Durable/README.md) supplies the shared affected diagnostic meanings; correlation remains caller-owned on existing `DurableProblem`. Database timestamps do not depend on process clock or rendering time. No connection value, raw catalog text, payload, scope/aggregate ID, exception, trace context or process-instance ID is output.

Doctor does not prove Work definitions, physical target equivalence, provider-effect truth, application registration/authorization/activation routing, Activity listener/exporter delivery, complete role-set approval or live readiness. Retain [packed-candidate proof](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh) with matched CLI/provider bytes before release. Rollback reinstalls the prior compatible CLI and reruns schema status; doctor requires no database or epoch rollback.

The [operator workflow comparison](evidence/executable-contract-adoption.md#runtime-doctor-operator-comparison) records two same-store diagnosis/recovery probes and their existing-host timing limits. It does not claim measured adopter onboarding or a human comprehension study.
