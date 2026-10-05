# Existing-permissions coverage mechanism probe

This private probe tests a root-owned coverage session **inside** the worker's
existing run-anchor write grant. It uses the frozen launcher's unchanged unit
properties and a fresh same-name user/group resolving to UID65010/GID65011.
It does not run Evidence admission, enroll an application, qualify Trusted,
change production source, or supply a repository coverage-gate result.

The controller creates a root-owned tmpfs mountpoint inside a worker-owned0700
anchor. The mount is limited to32MiB/64inodes, `nosuid,nodev,noexec`, with a
root-owned sticky01770 directory. Official Coverlet state/backups are root0600.
The actual worker namespace, process identity, NNP, capabilities and cgroup must
be measured before releasing the worker. The worker attempts actual file
read/write/delete and session rename, then produces genuine branch hits.

`supervise-native.py` owns the generated unit and controller process group for
90seconds plus bounded cleanup. `run-native-probe.py` keeps the same official
task host alive until worker physical exit, report collection, and assembly
restoration. It records restoration bytes and explicitly reseals original modes.
Unmount and strict account cleanup follow. Every error remains failure; an
unsafe remaining mount/workspace is quarantined. The production unit's task,
memory, path, capability and timeout properties are unchanged.

Build inputs are separately verified against the unchanged2814-file baseline
and SHA-pinned Coverlet10.0.1 package. The native build uses the dependency-free
lock and an external180-second owner. The bounded archive exports fixed receipts,
two actual reports and four private role logs, with no raw log echo in CI.

Portable `test_native_probe.py` controls parse data and read actual temporary
files; they do not run the root controller or prove kernel isolation. Local C#
format/build evidence is a separate source-validation result. Native success
requires both the root-controller and external-owner receipts to pass.

The controller explicitly sets its fresh private result directory to root:root
0700 through retained parent/leaf descriptors. The parent must be creator-owned
without group/other write, and the leaf's creator ownership and fresh identity are
checked before either metadata write. Private receipts and the isolated workspace
use `/run`, whose strict root:root0755 parent check remains required. Closed numeric
preflight facts record the result directory before/after sealing and parent metadata.
The worker still receives only its generated run-anchor write grant. Portable
substitution/collision controls supply no worker-isolation result.

The hosted image's .NET installation is a build input, not a root-sealed execution
location. `sealed-runtime.py` copies only the selected host, the highest stable
10.0 hostfxr and the highest stable 10.0 `Microsoft.NETCore.App` files into a
fresh root-owned runtime directory under the private workspace. It retains file
hashes/identities and seals directories0755, ordinary files0444 and the host0755.
The copy is bounded to256MiB and excludes SDKs. The worker executes that immutable
copy through its existing readable filesystem; no writable runtime or additional
unit path grant is introduced. A failed copy retains only its fixed private error category. Exact manifest
cleanup follows physical exit of
both worker and task host. Portable marker files test copying, not executable
runtime validity; native execution remains the required check.

The official MSBuild utility reference emits thirteen culture directories. The
private build copy accepts only that closed culture set, each containing a nonempty subset of the closed MSBuild Utilities.Core,
Framework and NET.StringTools `.resources.dll` names, with the same no-follow,
single-link, per-file and aggregate byte checks as top-level files. Each copied
culture directory is0555 and each file0444. The receipt records the exact copied
relative inventory and hashes; cleanup removes those exact files/directories.
Unknown directories, nested layouts or other resource files fail closed.
A bounded culture/name inventory is retained before the closed-name check so a
rejected native layout remains diagnosable; it grants no extra file eligibility.
Attempt3 measured runtime sealing and the root mount but failed before worker
start; its exact rejected build filename was not retained. The satellite layout
explanation comes from the actual local build and remains separate evidence.

The selected .NET10.0.12 Unix implementation returns raw errno values in
`IOException.HResult`. The probe accepts raw16 (EBUSY) only for the mounted
session rename, retaining post-denial path checks. The assembly write probe
accepts `UnauthorizedAccessException` or raw30 (EROFS); the root-owned
state/backup read/write/delete probes still require `UnauthorizedAccessException`.
An operation that succeeds always fails the probe. These source-backed classifier
corrections do not identify an errno from an earlier native run. See the
[Unix directory implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs#L409-L422)
and [Unix I/O error mapping](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs#L179-L184).
