# Issue 891 provider evidence mapping

This worksheet catalog records a candidate, offline-only v1 Cloud Run Execution shape for the two neutral consumers. It is implementation evidence for parser and classifier tests, not a provider compatibility certification.

## Provenance boundary

The positive fixtures are independently authored synthetic JSON. They preserve the recovered representation reported in [the Skoolit evidence assessment](issue-891-skoolit-evidence.md): numeric `metadata.namespace`, `cloud.googleapis.com/location` and `run.googleapis.com/job` labels, a gen2 single container, Cloud SQL/network/egress annotations, explicit retry/timeout/service-account values, and no explicit volume or volume mount. The fixture values, UUIDs, timestamps and digests are neutral. No raw provider payload or private deployment value is copied here.

The source shape is combined with the documented [v1 Execution schema](https://docs.cloud.google.com/run/docs/reference/rest/v1/namespaces.executions) and [Cloud SQL Unix-socket configuration](https://docs.cloud.google.com/sql/docs/postgres/connect-run#connect_with_unix_sockets). The Cloud SQL annotation and the documented single-container projection are configuration evidence; they do not prove a socket was opened, IAM/DB connectivity, or application acceptance.

## Candidate reader

`compatibility.json` records the locally installed gcloud `576.0.0`, Darwin arm64, the canonical full execution resource argv plus separate `--project`, `--region`, `--format=json`, and `--quiet` flags, installed command-source hashes, and `providerReadRan: false`. The checked-in `offline-sdk-replay.py` uses the installed apitools v1 message and Cloud Run Execution wrapper/resource printer without network access. It confirms semantic equality for both fixtures; the SDK normalizes generation/observedGeneration to integers and timeoutSeconds to a string, so this is not byte or JSON-type equality. Its status remains `candidate-not-certified` until the named maintainer accepts the complete mapping and native parser tests. This repository artifact does not claim a live cloud read or a supported version range.

The v1 generated `ExecutionStatus` exposes five counter fields. The [Cloud Run v1 schema](https://docs.cloud.google.com/run/docs/reference/rest/v1/namespaces.executions) documents their meanings. The official [Google generated Go v1 client](https://raw.githubusercontent.com/googleapis/google-api-go-client/main/run/v1/run-gen.go#L1384-L1435) declares all five as nonnullable `int64` fields with `omitempty`; its generated JSON contract therefore decodes omitted values to Go zero and omits zero values on serialization. The fixed adapter admits omitted-as-zero for these five counters only, with null, negative, overflow, duplicate and unknown shapes rejected/Unknown. This is an adapter-scoped mapping and does not claim that ProtoJSON omission globally proves provider state.

The installed SDK source corroborates the field definitions locally: `lib/googlecloudsdk/generated_clients/apis/run/v1/run_v1_messages.py` defines all five as integer fields, and the installed printer renders omitted values as zero. The checked-in offline replay records omitted/explicit-zero parity and the generated Go source provides the exact nonnullable `omitempty` contract for `retriedCount`, even though the human printer does not display it. `ExecutionCompatibilityManifestTests` verifies every fixture, worksheet, replay script hash, candidate status, and no-network/no-credential replay flags; it also verifies that positive fixtures require `succeededCount: 1`. This is an executable, adapter-scoped omission rule, not a general provider assertion.

## Identity and negative control

The expected identity is explicitly declared as project ID `demo-project` plus project number `123456789012`, region `us-central1`, and the exact physical Job and Execution IDs. A numeric v1 namespace is accepted only when it equals the declared project number. The swapped parent-label fixture is expected to classify as `Unknown`; no Job lookup, alias discovery, service-account inference, or Cloud SQL inference is permitted.

See the two linked worksheets and `compatibility.json` under `Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests/Fixtures/ExecutionEvidence/v1/` for JSON pointers, R/U decisions, exact hashes, and unresolved facts.
