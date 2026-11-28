# Sealed-memory Keycloak client credential reference

This page describes the internal sealed-memory custody slice in [`ForgeTrust.AppSurface.Auth.Keycloak`](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/README.md). It complements the existing [client credential reference](client-credential-reference.md), whose private-file profile remains available. The private [packaged worker anchor](#private-packaged-worker-anchor) exercises this component; normal adapter registration and public API acceptance remain pending.

## Internal API and lifecycle

`KeycloakClientCredentialMemoryCapture.Create(runId, authority, clientId, value)` creates a five-minute, run-scoped capture and returns an `IDisposable` owner. `CreateForOwnerLifetime(runId, authority, clientId, value)` creates the same sealed, identity-bound capture without an arbitrary wall-clock expiry. Its validity ends with the exact holder process incarnation and descriptor: callers must keep the returned owner alive while workers or web readers may need the reference. Both APIs expose a metadata-only `KeycloakClientCredentialReference` through `Reference`. The reference has an empty `RunDirectory` and a non-null `MemoryIdentity` containing the holder process ID, its kernel process-start counter, the descriptor number, and the anonymous file inode. `KeycloakClientCredentialReference.Read()` dispatches a memory reference to this reader; it never falls back to the private-file path.

```csharp
using var capture = KeycloakClientCredentialMemoryCapture.CreateForOwnerLifetime(
    runId,
    new Uri("https://identity.example/realms/sample"),
    "web-client",
    credential);

var reference = capture.Reference;
// Resolve only at the existing private OIDC token callback:
var value = reference.Read();
```

Creation validates the GUID-N run ID, HTTPS realm authority, client ID, and opaque credential. Authority and client validation use the same internal validators as the file-backed reference. The credential must be strict UTF-8, 1–4096 bytes, non-whitespace, and free of control characters. It is not trimmed, normalized, hashed, or copied into the reference. The bounded header carries an explicit `expiring` or `owner` lifetime discriminator. The former requires a five-minute UTC expiry; the latter has no expiry value and is accepted only while the exact holder descriptor still names the captured sealed inode. Unknown, duplicate, or contradictory lifetime fields fail closed.

`Dispose()` closes the capture owner's descriptor. Keep the owner alive for as long as any worker or web reader may need the reference. A read opens the holder's `/proc/<pid>/fd/<fd>` entry, verifies the holder's start counter and file identity before and after reading, validates the frame's lifetime and bindings, and returns the credential only to its caller. The reader reopens and verifies the owner's descriptor at the observation points; disposal observed by either check fails the read even if its first reader handle remains open. These are sampled checks: owner exit after the final check cannot revoke an already returned value or independently retained reader handle. Read failures use generic messages and do not include the secret or a secret-derived value.

The internal `IMemoryNative` and `TimeProvider` overloads exist as test seams. They let the test suite model partial reads and writes, interruptions, malformed bytes, expiry, replacement, and native failures on macOS. The internal production-native `Read(reference, clock)` overload lets a copied Linux ARM64 friend probe test lifetime behavior immediately with a future clock. These seams do not simulate or certify Linux kernel behavior.

## Private packaged worker anchor

The [realm-ready worker](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/realm-ready/Program.cs) has a private
`--appsurface-keycloak-memory-anchor <input-path> <reference-path>` entry point for the supervised-boundary proof.
It requires Linux ARM64. The input is a bounded 1–16 KiB private file with only owner read/write mode bits,
no leaf symbolic link, and JSON fields `RunId`, `Authority`, `ClientId` and `ClientSecret`. Declaration and credential
validation are the same as `Create`. The input is separate from ordinary desired configuration and is mounted
read-only into only the anchor. This is a fixture delivery mechanism; production host-to-anchor staging still needs
its complete ownership and custody proof.

The worker creates one sealed capture and writes only its serializable reference to a new output file. Existing
output files fail rather than being replaced. It flushes that file to disk and changes its mode to owner-read-only,
then holds the capture until SIGTERM or Ctrl+C requests cancellation. Successful cancellation disposes the capture
and returns zero; a malformed input or failed capture exits nonzero with a generic diagnostic. Anchor lifetime
itself is intentionally open-ended; the supervising owner must enforce the unchanged 30-second stop/verify/clean
deadline. The input must stay private even after capture. Never give its mount to web or reader members.

The private reconciliation binding accepts either an inline `ClientCredentialReference` or a
`ClientCredentialReferenceFile`, plus `ExpectedCaptureRunId`. It rejects both reference sources together, multiple
confidential-client credential sources, any credential source for a public client, non-memory references and
run/authority/client mismatches before reading the sealed value. A metadata file is bounded to 1–16 KiB and may not
be a leaf symbolic link. It is metadata, not a second credential store. These bindings do not yet wire the public
adapter graph or qualify real Keycloak reconciliation with the new anchor.

The [preparation tool](https://github.com/forge-trust/AppSurface/blob/main/tests/keycloak-identity-feasibility/prepare-worker-anchor.py) generates a project outside
the checkout with one reference to the extracted pure DLL, cleared package feeds and fresh private caches. It
preserves every relative path in the adapter's recursive worker closure and rejects a different pure DLL or any
fixture output that would replace candidate bytes. Preparation and execution use the same [worker archive reader](https://github.com/forge-trust/AppSurface/blob/main/tests/keycloak-identity-feasibility/worker_payload.py), which rejects invalid and duplicate worker paths. Keep it alongside the Python drivers when copying them. The [runtime driver](https://github.com/forge-trust/AppSurface/blob/main/tests/keycloak-identity-feasibility/run-sealed-memory.py)
uses read-only copied payloads, disabled networking and an empty read-only NuGet cache. Its worker/web readers are
small public-`Read()` fixtures; their role names do not imply actual reconciliation or web/OIDC acceptance.

After both readers have observed the original capture despite bootstrap-input rotation, a separate observer
process must verify every exact container stopped while the exclusive lease and active marker remain held. Only
then may the owner remove the containers, delete the private input and clear the marker under the same 30-second
deadline. Unavailable observation or an uncertain clear retains active state. See the
[qualification record](plans/issue-888-keycloak-identity-readiness.md#feasibility-results) for measured results and remaining gates.

`--inject-stop-observer-failure` and `--inject-clear-commit-failure` are mutually exclusive fixture-only controls
requiring worker-anchor mode. The former records the exact injected observer code 75 and verifies retained active
state and private input; the latter fails after marker unlink to exercise rollback. Both must exit nonzero and
retain active state. Neither is a successful normal run. The separate
[stopped-fixture cleanup tool](https://github.com/forge-trust/AppSurface/blob/main/tests/keycloak-identity-feasibility/cleanup-stopped-memory-fixture.py) accepts only
the exact observer-failure receipt and its retention checks, validates the private root and all four owned identities, reacquires the lease
and requires fresh independent stopped observation before removing the containers and input. It never starts or
stops a workload and leaves the failed marker and original receipt unchanged. It is not the product recovery API.

```sh
python3 tests/keycloak-identity-feasibility/cleanup-stopped-memory-fixture.py \
  --receipt <failed-observer-case/receipt.json> --evidence <fresh-cleanup-directory>
```

Both paths are required; the evidence directory must not exist. Exit zero describes only successful fixture
cleanup with `workloadsStarted=false`, `workloadsStopped=false` and `activeMarkerRetained=true`. A different failure
kind, missing retention proof, changed owner/profile, live container or unavailable independent observation fails
without granting workload termination or marker clear.

## Native Linux ARM64 behavior

The production entry points fail with `PlatformNotSupportedException` unless the process is Linux ARM64. They do not switch to a file, environment variable, or another custody mechanism on an unsupported platform.

Capture uses `memfd_create` with `MFD_ALLOW_SEALING | MFD_CLOEXEC`, writes the complete bounded frame while handling short writes, changes the descriptor mode to owner-read-only (`0400`), applies all four required seals (`F_SEAL_SEAL | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE`, value `15`), and rejects a negative or incomplete `F_GET_SEALS` result. Reads likewise reject a negative `F_GET_SEALS` result before inspecting or reading file contents; a negative native error must not pass a bitmask check.

To reach the holder descriptor, the reader opens `/proc/<pid>/fd/<fd>` with `O_NONBLOCK | O_CLOEXEC` (`0x800 | 0x80000`) before checking it. `O_NOFOLLOW` is intentionally absent: procfs's numeric descriptor link is the mechanism for opening the already-bound file, and the descriptor numbers come from validated metadata. Nonblocking mode ensures an unexpected FIFO or other unsuitable descriptor cannot block before its type is checked.

The reader uses `statx` with the ARM64 256-byte layout and requires the returned mask to include type, mode, UID, link count, inode, and size. It verifies a regular file, mode `0400`, the same effective UID, zero links, the exact recorded inode, and a bounded frame size. It checks the holder's raw integral start counter from field 22 of `/proc/<pid>/stat` both before and after access. This is a boot-relative kernel counter, not a reconstructed wall-clock process start time. Short reads and writes are completed in loops; `EINTR` is retried at most 16 times in total for each operation so persistent interruption fails closed instead of spinning without bound.

The mechanism and native layout were exercised by the Linux ARM64 [sealed-memory feasibility probe](../tests/keycloak-identity-feasibility/sealed-memory-probe/Program.cs). That probe passed the sealed-file, expiry, replacement, identity, and binding checks, including separate worker and web containers sharing the qualified PID namespace. It also confirmed that a foreign PID namespace cannot resolve the holder descriptor. The in-library test seam is not a substitute for rerunning that native probe on a qualified host.

## Decision and limits

Use this profile only when a supervised Linux ARM64 deployment can keep the capture owner alive for the complete worker/web use interval and give authorized readers the same effective OS user and qualified PID namespace. The reference can cross that boundary as metadata; possession of it does not grant a different user or PID namespace access. The design trusts the same effective user, root, and host services with equivalent authority, and it is not protected from a hostile or compromised host. A reader-open handle cannot keep the reference usable after the owner descriptor closes. Keep secret resolution inside the existing callback that needs it. The ordinary file-backed reference remains the appropriate choice for its separately qualified macOS ARM64 workflow.

The expiring `Create` profile still rejects reads after five minutes even with a live holder. The explicit
`CreateForOwnerLifetime` profile instead requires the same holder/file identity without a clock cutoff. The private
worker anchor selects this profile so delayed web startup can read the original once-per-run bytes. Qualification
of the full channel is recorded in the [owner-lifetime composition proof](plans/issue-888-keycloak-identity-readiness.md#feasibility-results);
it remains separate from normal supported adapter registration and public platform acceptance.

Kernel seals prevent ordinary modification of the anonymous file, and inode plus process-incarnation checks bind a read to the captured holder and file. This is bounded local-development custody, not a production credential manager. Closing a descriptor is not secure erasure, and managed strings or prior memory copies are not claimed to be erased. Do not place the credential in logs, command arguments, environment variables, serialized metadata, events, diagnostics, reports, or a credential-derived acknowledgment.
