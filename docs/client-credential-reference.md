# Keycloak client credential reference

[`ForgeTrust.AppSurface.Auth.Keycloak`](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/README.md) exposes `KeycloakClientCredentialReference` as a serializable descriptor for a run-scoped captured Keycloak client credential. The descriptor carries only custody location and client identity metadata. It does not load or retain the credential when it is constructed.

## API

```csharp
var reference = new KeycloakClientCredentialReference(
    runDirectory,
    runId,
    new Uri("https://identity.example/realms/sample"),
    "web-client");

string credential = reference.Read();
// Or: string credential = await reference.ReadAsync(cancellationToken);
```

The JSON constructor accepts `(string runDirectory, string runId, Uri authority, string clientId)`. Its read-only properties expose those same declaration values: `RunDirectory`, `RunId`, `Authority`, and `ClientId`. Construction validates the declaration and performs no filesystem I/O or secret resolution. JSON serialization therefore describes the reference; it cannot serialize the credential because the object never contains it.

`Read()` opens the native capture and returns its credential only after checking run identity, expiry, captured-file identity, custody metadata, and exact authority/client binding. `ReadAsync(CancellationToken)` queues the same read on a worker task. Cancellation prevents a queued read from starting; a native read already in progress cannot be interrupted. Read failures do not include the credential in their messages.

## Declaration constraints

- `runDirectory` must be an absolute path. Its root ancestors must remain trusted.
- `runId` must parse as a 32-character GUID in N format.
- `authority` must be an absolute HTTPS URI whose final path segments identify `/realms/{realm}`. User information, query strings, and fragments are rejected. Its normalized URI is limited to 2048 UTF-8 bytes.
- `clientId` must contain valid Unicode encodable as strict UTF-8, include at least one non-whitespace character, contain no control characters, and fit within 256 UTF-8 bytes.

The captured credential is treated as opaque text. It must encode as strict UTF-8 to between 1 and 4096 bytes, include at least one non-whitespace character, and contain no control characters. Unpaired UTF-16 surrogates are rejected because they cannot be encoded as valid Unicode UTF-8. Control characters, including NUL, are rejected so values cannot carry terminators or other control bytes into the custody file or callback. The value is not trimmed, normalized, hashed, or copied into descriptor metadata.

`RealmClientCapture.Capture(root, runId, value)` and `RealmClientCapture.Read(root, runId)` remain available for existing internal callers. The extended capture accepts optional `authority` and `clientId` arguments; provide both or neither. A reference uses the identity-bound read, so it can read only captures created with the exact same normalized HTTPS realm URI and ordinal client identifier. A legacy capture without identity metadata remains readable through the legacy internal read overload, but not through `KeycloakClientCredentialReference`.

## Credential handling in a host

The descriptor's `Authority` and `ClientId` are metadata that a host may project into ordinary startup configuration. Keep the credential out of that configuration. Resolve it only inside the existing private OIDC token callback that needs it, use the returned string for that operation, and do not attach it to the descriptor, event arguments, logs, diagnostics, reports, or serialized state. The callback should not emit the credential or any credential-derived hash or acknowledgment.

## Platform and security boundary

The candidate [sealed-memory profile](sealed-client-credential-reference.md) adds
`KeycloakClientCredentialReference.ForSealedMemory(memoryIdentity, runId, authority, clientId)`. Its immutable
`KeycloakClientCredentialMemoryIdentity` carries `ProcessId`, the kernel `ProcessStartCounter`, `FileDescriptor`,
and `Inode`. Construction and JSON reconstruction validate positive process/counter/inode values and a descriptor
from 3 through 1,048,575 without native access. Memory references use an empty `RunDirectory`; combining a directory
with `MemoryIdentity` is rejected. Legacy four-argument file construction and metadata JSON remain supported.
`Read()` selects exactly the declared transport and never falls back to another custody mechanism.

The five-argument JSON constructor accepts a nullable `memoryIdentity` after the existing four metadata arguments;
its default is null for file custody. The property is omitted when serializing a file reference. Sealed-memory reads
are restricted to the qualified Linux ARM64 supervised PID namespace and same effective user. Public platform/API
acceptance and adapter composition remain gated by the [supervised qualification](plans/issue-888-keycloak-identity-readiness.md#feasibility-results).

Private-file capture and reads are supported only on macOS ARM64. On supported systems, the capture is placed under a private run-owned directory, uses owner-only Unix file modes, opens path components without following links, checks opened files with `fstat`, records device, inode, and birth-time identity, and seals the credential file with Darwin's immutable-file flag. The descriptor expires after ten minutes. Reads also check owner, mode, link count, file size, expiry, immutable state, and that the opened file did not change during the read.

This is an internal, bounded custody experiment for local development. Trusted root ancestors are required. Unix modes and the immutable flag do not provide full ACL protection or isolation from code running as the same user or root; the owner can remove the immutable flag. This API is not a production credential store and does not claim cross-platform custody support.

See the [Keycloak package guide](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/README.md) for the package's broader scope and boundaries.
