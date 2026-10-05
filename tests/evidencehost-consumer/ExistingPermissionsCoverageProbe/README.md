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
