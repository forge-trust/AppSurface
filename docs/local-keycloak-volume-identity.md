# Local Keycloak volume root identity

[`KeycloakVolumeIncarnation`](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/KeycloakVolumeIncarnation.cs) is an internal Linux ARM64 helper for recording the filesystem identity of a mounted local Keycloak volume root. It calls `statx` once for the fixed path `/volume`, requests the root type, inode, and birth-time fields, and supplies a 256-byte result buffer. It follows no final-component symlink and accepts only a directory with all requested fields present, a nonzero inode, positive birth seconds, and a nanosecond value below one billion.

The returned record contains the filesystem device major and minor numbers, root inode, and root birth timestamp. `Read()` exposes no path parameter and has no alternate-platform or alternate-syscall fallback. `Parse(ReadOnlySpan<byte>)` exists as an internal, deterministic seam for verifying the fixed ARM64 layout; it requires exactly 256 bytes and reads the `statx` fields at their Linux ABI offsets.

`ReadOwnership()` also requires the numeric UID and GID fields from that same fixed root and returns them
alongside the incarnation. `ParseOwnership` rejects a result missing either field. The
[supervised migration path](supervised-keycloak-execution.md#legacy-volume-migration) uses the ownership pair
to select the pinned image's compatible server identity. Recovery continues to compare the root incarnation.

## When to use it

Use the stamp as one piece of evidence when a local owner is checking whether the mounted volume root still has the identity it observed earlier. The owner must hold its volume lease while taking and comparing stamps, following the ownership design in [Keycloak identity readiness](designs/issue-888-keycloak-identity-readiness.md).

Before calling `Read()`, the caller must independently attest the exact local Docker daemon, the exact local volume, and the exact Docker-reported mountpoint, then mount that mountpoint read-only at `/volume`. The helper sees only the mounted root metadata. It does not inspect Docker, prove which daemon supplied the mount, verify the bind-mount source or read-only flag, check the lease, inspect child entries, or authorize a volume for use. Any missing or ambiguous caller-side attestation must keep the owner from proceeding.

The parser checks the file type only; it does not enforce UID or permission policy. It does not initialize, change, or repair the root's owner or mode. Existing-volume UID policy and any separate new-volume initialization remain responsibilities of the caller's lifecycle code.

## Limits and pitfalls

The birth timestamp is the filesystem birth time of the root directory, recorded in seconds and nanoseconds. It is not a Docker `CreatedAt` value, a wall-clock run generation, or a unique security token. Compare the complete record as supporting replacement evidence; do not let it replace daemon, volume-name, mountpoint, read-only-mount, or lease checks.

This helper reads metadata for one root object only. It does not read volume contents or enumerate files, and it creates or writes nothing. Unsupported operating systems and architectures fail with `PlatformNotSupportedException`; a missing `statx` symbol, syscall failure, incomplete mask, unexpected root type, or invalid fields fails closed. The stamp offers no protection against a privileged administrator or root user that can replace mounts or manipulate the filesystem, and it does not establish distributed exclusion.

The parser tests in [`KeycloakVolumeIncarnationTests`](../Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/KeycloakVolumeIncarnationTests.cs) verify offsets and malformed metadata without invoking Docker or touching a real volume. They establish parser behavior only, not native syscall behavior or the caller's mount and ownership checks.
