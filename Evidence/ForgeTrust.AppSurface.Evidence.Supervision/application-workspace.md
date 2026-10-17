# Immutable application workspace

[`LinuxApplicationWorkspace`](LinuxApplicationWorkspace.cs) gives one selected closed application definition a fresh, root-owned bundle and a bounded application scratch mount. The workspace is part of the Linux application launch and cleanup flow described in [application execution](application-execution.md); the separate application UID and its storage-custody gate are described in [application accounts](application-accounts.md).

## API and ownership

The internal factory is:

```csharp
LinuxApplicationWorkspace.Create(
    EvidenceProtectedLaunchInput input,
    LinuxOwnerActivation owner,
    LinuxRunAccounts accounts,
    EvidenceClosedApplicationDefinition definition,
    string entryDigest,
    CancellationToken token)
```

The caller performs compiled application selection and passes that exact definition and digest. `Create` recomputes the canonical digest, copies the selected file rows and six capability limits into Supervision-owned data, checks that the declared read-only input is exactly the selected input, and validates that the generated workspace does not overlap `ToolRoot`. It does not select a registration or accept caller-supplied executable arguments. The live `ApplicationUid` and `ApplicationGid` come from `LinuxRunAccounts`; copied numeric values and layout rows do not grant filesystem authority.

Before opening the source bundle or creating a directory, `Create` reserves this exact workspace with `RetainApplicationForCustody`. It then opens the protected `ToolRoot/application` tree through retained no-follow descriptors and requires the complete tree to match the selected rows. It copies every file into a new generation, rehashes both source and destination bytes, and holds source and destination handles through process settlement and sealing. A failure or cancellation leaves the account holder retaining the workspace for quarantine.

The exposed path properties have fixed meanings:

| Property | Path or contract |
| --- | --- |
| `Root` | `/run/appsurface-application-<RunId:N>` |
| `BundleRoot` | `Root/bundle`, outside `ToolRoot` |
| `ScratchRoot` | `Root/scratch`, a host-visible tmpfs sized by `ScratchBytes` and owned by the application UID/GID |
| `ProtectedOutputRoot` | `Root/denied-output`, root-owned and mode `0700` |
| `DeniedToolRoot` | The complete original `ToolRoot`, for masking from the application unit |
| `AppHostPath` | The selected top-level AppHost DLL inside `BundleRoot` |
| `DcpPath` | The fixed `BundleRoot/dcp/dcp` executable |
| `InputPath` | The exact selected declared input inside `BundleRoot` |
| `EntryDigest` | The canonical digest supplied by selection and independently recomputed by the workspace |

The selected bundle directories are root-owned mode `0555`. Files are root-owned `0444`, except the fixed DCP executable and its declared extensions, which are `0555`. The generation root is root-owned `0711`; `control` and `denied-output` are root-owned `0700`. Scratch is a real `tmpfs` mount with `nosuid`, `nodev`, and `noexec`; its size and owner come from the selected capability and account holder. The output-probe name `native-resource-output-probe` must be absent from `ProtectedOutputRoot` at creation and on later guards.

## Required launch and cleanup order

1. Complete application selection, then call `Create` before constructing or dispatching the application process.
2. Call `RecheckBundle(token)` at the process integration checkpoints. It verifies the retained source and copied file inventories, hashes, names, ownership, modes, and workspace paths.
3. After authentic owner activation and immediately before dispatching the application start, call `Arm(token)`. It creates a fixed root-owned marker in the empty scratch tmpfs. Later checks authenticate its held inode, name, mode, and bytes.
4. Retain the actual `LinuxApplicationProcess` with `RetainApplication(process)` before process work can dispatch. Do not seal while start, stop, process monitoring, or output pumps remain unsettled.
5. Call `SealForCleanupAsync(owner, accounts, token)` only after the retained application process reports authentic physical settlement. The workspace closes its own scratch descriptors and calls `umount2` with flags `0` through the retained root directory’s `scratch` child. The unmount is strict: a busy or otherwise failed unmount latches quarantine and leaves account IDs retained.
6. Account cleanup calls `RequireSealedForAccountRelease(accounts, owner, token)` before releasing application identities. The guard repeats the native path, bundle, output-probe, and owner checks. `Dispose` closes the retained handles only after sealing and a successful release guard.

`RequireOwnedBy` is for active work and uses the owner’s active-work guard. `RequireControlOwnedBy` is for stop/cleanup control after admission closes. Both bind the exact owner and account objects, recheck the retained generation, and honor the original deadline and cancellation token. No operation substitutes a new timeout or path-based fallback for failed `openat2` resolution.

## Design decisions and sharp edges

- The application executes the copy under `BundleRoot`, not files inside `ToolRoot`. The unit can therefore mask the entire original `ToolRoot` while keeping the audited selected bundle readable.
- `ScratchRoot` is mounted in the host mount namespace. A private `/tmp` alone would not satisfy host-side resource probes or give cleanup a retained, independently verifiable mount boundary.
- Do not call `Arm` before application startup authority exists, and do not let the application create or replace the marker. It is a freshness signal rooted in trusted C# launch control.
- Do not recursively delete scratch contents. Once the process is physically settled, unmount the whole tmpfs. If another holder keeps the mount busy, preserve the mount and quarantine the corresponding accounts until the native fixture or operator can establish a safe terminal condition.
- A successful process exit alone is not physical settlement. The retained process must complete its stop/join checks, including the application control group and output pumps.
- `LinuxApplicationWorkspaceLayout` and `LinuxApplicationObservedEntry` are data-only test seams. The tests in [`LinuxApplicationWorkspaceTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationWorkspaceTests.cs) validate path projections and exact row comparisons; they do not fabricate owner, account, descriptor, mount, or process authority. Native lifecycle behavior must be exercised through the Linux supervision fixture.

## Private unmount diagnostic

After an actual failed `umount2` with flags zero, the root may write one private stderr event with
`schema: evidence-application-storage-v1`, `phase: unmount-rejected`, and `errno`. The errno is the
number captured immediately from that syscall, bounded to 1 through 4095; unavailable or out-of-range
values are null. The event contains no paths, raw exception text or child output. Emission is best effort
and cannot replace or clear the original unmount rejection. This diagnostic is data only; the retained
failed seal task and quarantine guard still control identity release.

Joined application stdout/stderr prefixes may be retained separately as fixed `control/stdout.prefix`
and `control/stderr.prefix` files beneath the selected root-owned generation. Each is root-owned 0600,
single-link, exclusively created and at most 64 KiB, inside the root-only 0700 control directory.
Raw bytes remain private and never appear in safe result JSON. Missing or failed diagnostic retention
cannot replace the original execution/cleanup outcome or establish health.

### Fresh marker descriptor access

`Arm` exclusively creates the fixed `armed` file, writes the fixed bytes and sets root ownership and 0444
mode. `OpenMarkerReadOnly` then reopens the same name through the retained scratch directory with mandatory
openat2 and compares full inode/device/ownership/mode/length/time metadata against the actual creator FD
before and after opening. The write-only creator closes before the original marker bytes are read through
the new read-only FD. The held descriptor is read-only; no writable marker
handle is transferred to the application. Rechecking a write-only creator with `RandomAccess.Read` is an
EBADF error and cannot arm a run. A changed name/inode rejects and quarantines rather than falling back.

The FD helper authenticates no application or root owner on its own: Linux tests use ordinary owned files
to verify real descriptor access and deterministic name replacement. Full root ownership, contents, freshness,
original deadlines and native application ownership remain `Arm`'s separate mandatory guards.
