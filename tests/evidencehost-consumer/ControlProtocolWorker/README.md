# Linux control protocol mechanism check

Build the fixture after the parent build gate is open, from the repository root:

```sh
dotnet restore tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj --locked-mode
dotnet build tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj -c Release \
  --no-restore -p:UseSharedCompilation=false
```

Run the independent protocol matrix as root inside a disposable Linux container or VM with .NET 10 and
`setpriv` available:

```sh
python3 -B tests/evidencehost-consumer/test_control_protocol.py \
  --worker-dll tests/evidencehost-consumer/ControlProtocolWorker/bin/Release/net10.0/EvidenceHost.ControlProtocolWorker.dll
```

The driver launches a root-owned fake Unix-socket broker and a .NET worker with UID 65534 and primary GID 65532,
using `setpriv --no-new-privs --clear-groups`. It covers the
real `EvidenceLinuxWorkerSupervisor.ConnectAsync` peer-credential handshake, descriptor identity and deadline
rejection, broker PID replacement, bounded/malformed responses, artifact-count limits, and multi-chunk reads.
The fake descriptor uses a `/system.slice/...` value because the client validates that shape; the fixture does
not create a systemd unit or cgroup and does not establish EvidenceHost admission or consumer acceptance.

Each scenario now uses the [closed worker descriptor grammar](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md):
`control-root/broker/control.sock` and a fresh `control-root/worker-control.json` snapshot, created after the
broker observes the peer PID/UID/GID. The normal control root is root/worker-group `0710`, the broker directory
is root/worker-group `0710`, the socket is `0660`, and the snapshot is root/worker-group `0440`.
The run ID has two segments, revisions are synthetic hexadecimal values, optional diff members are null,
and the synthetic output-parent owner matches the declared worker. Identity-negative cases keep those facts
internally consistent so the actual worker binding rejects them. The nonroot-broker negative alone permits
other-search on the root (`0711`), gives its broker directory to that nonroot UID with worker-group search,
and uses socket `0666`; this lets the worker reach the deliberately invalid peer without granting it root
credentials. A replacement broker reuses the socket path while the first broker PID remains alive.
Malformed/rejected ready responses normalize to `ASEVD402`; artifact failures after a valid connection retain
their artifact-budget diagnostics.

## V2 application protocol controls

The matrix preserves the **17 v1 controls** and adds **23 v2 controls** for **40 native protocol scenarios**.
The worker invokes the existing internal [application protocol](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#internal-restricted-application-protocol-v2-prerequisite)
through real `ConnectAsync`, `StartApplicationAsync` and `WaitForApplicationResourceAsync` calls. It has no
transport injection, reflected access, new friend declaration, admission context or proof factory.

| Controls | Observed operation requirement |
| --- | --- |
| Valid typed start followed by readiness | One `application-start`, then one `resource-wait` for the validated prior lease |
| Duplicate start; wrong lease/resource; closed wait | One first start, zero denied application operations before cleanup |
| Wait before start; closed start; wrong application ID/digest | Zero application operations before cleanup |
| Malformed start JSON; wrong UID; invalid lease; unowned start; wrong cgroup | Invalid synthetic start acknowledgement rejects with `ASEVD410` |
| Malformed readiness JSON; wrong UID; unchecked kernel peer; unhealthy response; 4097 received bytes | Invalid synthetic readiness acknowledgement rejects with `ASEVD410` |
| Blocked start or readiness acknowledgement | Broker observes the request, worker cancellation closes that connection, then fresh uncancelled stop/wait connections arrive |
| Root broker PID replaced before start or readiness | Replacement root process receives **zero request bytes**; the existing client rejects `ASEVD402` |

Closed-state, duplicate and invalid prior-lease/resource checks reject `ASEVD410`. An application ID or
entry-digest request mismatch rejects `ASEVD402` before I/O. A root-only `protocol-operations.json` receipt
records the exact observed sequence including `ready`, `stop` and `wait`; the driver checks that sequence
independently of the worker's result. Invalid JSON contains a canary; any echo on worker stdout/stderr fails.
Cancellation is triggered only after a root-created `control.sock.operation-seen` marker shows the actual
blocked request was received. The worker's stop/wait calls use `CancellationToken.None`, never the cancelled
operation token. Each worker and broker wait remains finite, and socket names are limited to 100 bytes.

The v2 descriptor adds exactly 14 `application` fields, complete resource/producer/artifact/gate declarations,
all nine closed bundle roles (including read-only `.deps.json`), and finite capabilities at the permitted
bounds. The actual worker UID/GID comes from Linux peer credentials. The other six positive values are
synthetic and distinct from both worker values: producer UID/GID 65533/65531, application UID/GID 65530/65529,
results GID 65528 and resource-access GID 65527. No supplementary group is granted.

**All application, AppHost PID, bundle digest, cgroup, kernel-peer, HTTP, readiness and owned-exit acknowledgement
contents are synthetic protocol data.** This fixture starts no AppHost, DCP or resource process and makes no
real HTTP probe or application-cgroup inspection. `start_ack`, `readiness_ack` and `cleanup_ack` report parsing
of those synthetic acknowledgements. Replacement controls assert identity rejection only. Native Linux
execution establishes broker/worker peer authentication and byte framing; portable controls establish neither.
No scenario creates a Passed consumer receipt, shared admission, Trusted eligibility or native Aspire acceptance.

The CLI still accepts `--worker-dll` for the full matrix, and hidden `--serve SOCKET SCENARIO DOTNET` is the
disposable broker process entry point. The worker takes `SOCKET MODE`; existing modes remain unchanged,
new regular modes use the scenario names in `APPLICATION_CASES`, and the replacement coordinator alone uses
`application-hold-start`/`application-hold-wait`. Build completion on macOS validates source only. The 40-case
root/nonroot matrix remains unverified until its separately bounded native Linux checkpoint runs.

Run the portable metadata/layout controls without .NET or root:

```sh
python3 -B tests/evidencehost-consumer/test_control_protocol_fixture.py
```

These controls use real temporary files and modes while mocking privileged ownership and peer credentials.
They do not establish Linux kernel authentication, namespace enforcement, or native consumer acceptance.

`EvidenceLinuxControlProtocolTests` separately exercises platform rejection on non-Linux hosts and invalid-path
preflight on Linux. It does not replace this root-only mechanism run. Run both only when the repository's parent
build owner authorizes the build and test steps.
