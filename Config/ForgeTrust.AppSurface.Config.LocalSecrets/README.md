# ForgeTrust.AppSurface.Config.LocalSecrets

OS-backed local secret posture for AppSurface configuration.

Use this package when a solo or hobbyist AppSurface app needs local development secrets before it has a remote vault.
LocalSecrets is not a team vault, CI secret system, container secret provider, or production rotation/audit solution.

## Install

```bash
dotnet package add ForgeTrust.AppSurface.Config.LocalSecrets
```

Register `AppSurfaceLocalSecretsModule` beside your Config module. Environment variables still win, LocalSecrets sits
above file configuration, and only a true missing local secret falls through to files.

For roots that contain inline secret destinations, the module exposes the same LocalSecrets singleton as
`IConfigCompositionValueProvider` and `IConfigProviderClaimInspector`. The raw adapter returns stored text before typed
conversion, marks contributions sensitive, and preserves the existing posture, identity, missing, and terminal rules.
LocalSecrets remains a whole-root base provider and does not implement the version-aware `IConfigSecretProvider` contract.

For typed file-declared secret destinations, read the [canonical reference guide](../ForgeTrust.AppSurface.Config/docs/file-secret-references.md) and run
the [network-free golden path](../../examples/file-secret-references/README.md). LocalSecrets can participate as a sensitive
raw base contribution; it does not reinterpret a remote descriptor or perform provider-specific reference resolution.

## Typed file-declared secret references

When a root contains `Secret<T>`, LocalSecrets supplies a lower scalar contribution only when its complete root is selected.
The composition engine then applies exact environment values last. A file descriptor naming Google remains a Google
reference, and LocalSecrets does not make a disabled remote declaration perform a local or remote read.

For Linux, AppSurface treats `secret-tool` as an external command with an explicit trust boundary. By default it uses
only `/usr/bin/secret-tool`, then `/bin/secret-tool`. It does not execute `secret-tool` from `PATH`; a PATH match is
reported only as ignored diagnostic context.

## First Secret

```bash
appsurface secrets init --app MyApp --environment Development
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets doctor --app MyApp --environment Development
DOTNET_ENVIRONMENT=Development dotnet run
appsurface config diagnostics
```

The diagnostics path reports where a value came from without printing the raw secret value.

## macOS v2 Keychain migration

On macOS, LocalSecrets now writes v2 `SecItem` records in an entitlement-free file-based Keychain namespace. The CLI and
runtime must use the same pinned application, environment, and optional prefix. A retained readable v1 record with no
v2 counterpart returns terminal `MigrationRequired` / `local-secret-migration-required`; it does not silently fall
through, write from the AppHost, or print the value.

Recover with the explicit, resumable command:

```bash
appsurface secrets migrate --app MyApp --environment Development
DOTNET_ENVIRONMENT=Development dotnet run
```

The command reports only key names and safe counts. It retains v1 for recovery and never overwrites a v2 record. Once a
v2 record exists it is canonical, so use `appsurface secrets set` to update it. See the [macOS v2 migration guide](docs/macos-keychain-v2-migration.md)
for matching runtime identity, failure handling, and the three-key CLI/AppHost smoke.

### When You See `local-secret-store-unavailable`

`local-secret-store-unavailable` means the OS-backed LocalSecrets store could not complete the requested operation.
It is retryable, but only a true `Missing` result falls through to lower-priority configuration.

Start with:

```bash
appsurface secrets doctor --app MyApp --environment Development
```

On Linux, verify that the trusted `secret-tool` path is installed and executable. AppSurface uses `/usr/bin/secret-tool`,
then `/bin/secret-tool`, unless you pass a verified absolute path through `--secret-tool-path` or
`AppSurfaceLocalSecretsOptions.LinuxSecretToolPath`. Also check that the current DBus or desktop session can reach a
Secret Service implementation.

For CI, headless sessions, containers, team environments, and production, use environment variables, key-per-file, or a
remote vault instead of OS-backed LocalSecrets. Use `--store-file <path>` only for deterministic local fallback examples
and tests.

Startup failures that happen before a platform command can run report `Unavailable`, not `Locked`, even when the raw OS
exception message contains words such as `denied` or `locked`. The display-safe diagnostic includes the operation,
exception type, `HResult`, and synthetic exit code. It intentionally omits secret values, logical values, raw OS
exception messages, command paths, command arguments, and absolute paths.

### File fallback posture

The OS-backed stores are the normal LocalSecrets path. The `--store-file <path>` fallback exists for deterministic
examples, unsupported local environments, and tests. It is not equivalent to Keychain, Secret Service, or Windows
Credential Manager.

On Unix platforms, the file fallback creates missing directories with `0700` mode bits and writes or repairs the JSON
file with `0600` mode bits during `set`, `delete`, and `doctor`. Existing parent directories are inspected, not
modified in place; loose parent directories stop resolution with a paste-safe diagnostic. Reads inspect existing files
before returning a secret value: symbolic-link paths, directory paths, and non-canonical mode bits stop resolution
instead of silently serving a risky file. `doctor` may report:

| Diagnostic code | Meaning |
| --- | --- |
| `local-secret-store-ready` | The fallback file can be opened and posture is already ready. |
| `local-secret-file-posture-repaired` | `doctor` or a write tightened Unix file mode bits. |
| `local-secret-file-posture-degraded` | The fallback can be opened and `doctor` can exit successfully, but this platform path does not prove owner-only posture in v1. |
| `local-secret-migration-recovery-pending` | The fallback is usable, but one or more explicitly retained exact-key migrations still protect their source and destination identifiers. |
| `local-secret-file-posture-unsupported` | The path shape or checked Unix posture is unsafe for fallback storage, such as a symbolic link, directory path, loose mode bits, or writable non-sticky ancestor. |

Example deterministic file fallback check:

```bash
appsurface secrets doctor --app MyApp --environment Development --store-file ./.appsurface/local-secrets.json
```

The command prints `Problem`, `Cause`, `Fix`, `Docs`, and `Retryable` without printing secret values.
`local-secret-file-posture-degraded` is a degraded readiness result for explicit local/test fallback workflows;
`local-secret-file-posture-unsupported` is a fail-closed result that stops reads and writes until the path is moved or
repaired. Prefer the OS-backed store for normal local development.

### Exact-key migration capability

`appsurface secrets migrate-key` previews the exact source, parsed destination segments,
and destination storage identity. Repeat with `--apply` to confirm the
[journaled migration](../../guides/config-key-migration.md#localsecrets-journaled-migration).
The migration is complete only when the selected backend implements
`IAppSurfaceLocalSecretMigrationStore.MigrateKey`. The file fallback and the indexed OS-backed adapters use one
exclusive cross-process maintenance lease, write a durable journal, copy and reread the exact source and destination
identifiers, publish the index before source deletion, and record each roll-forward state. A backend that cannot prove
those guarantees returns `local-secret-migration-unsupported` before either value is read or changed. The macOS
v1-to-v2 namespace migration remains a distinct bulk migration; its exact-key command uses the same journal protocol
when moving a retained legacy record into the v2 namespace.

For a programmatic preview, call
[`IAppSurfaceLocalSecretMigrationStore.GetKeyMigrationDestinationIdentity`](IAppSurfaceLocalSecretMigrationStore.cs)
with the application, environment, optional prefix, and strict destination key. The returned identity normalizes the
namespace and exposes the selected store's exact destination in `Identity.StorageName`. `MigrateKey` uses this same
preparation; macOS returns its v2 locator rather than the generic file/native locator. Preparation performs no value,
journal, or lease I/O and does not promise that the later migration can complete. An adapter without a known encoding
returns `local-secret-migration-unsupported`.

Native setters preserve a unique existing key's exact case spelling under the writer lease. Logical deletes resolve
that same spelling before mutation; macOS checks both the v2 and legacy indexes before deleting either version.
An index containing multiple case-only spellings returns `config-key-collision` before mutation, even when the stored values are equal.
Exact-key migration applies the same guard to the destination: an existing case-only destination spelling is rejected before
the destination write or source deletion, while the exact source record is excluded from that check so a deliberate
case-only source rename can copy to the requested spelling. A collision leaves the durable journal at its last safe state;
remove or reconcile the competing record and retry the same request.
Retained macOS migration reads and deletes the exact native source through the indexed adapter's raw operations;
logical lookup policy is never applied to a migration source. An exact v2 source missing from the v2 index remains
missing even when a v1 record has the same key spelling; only an explicit v1 source probes legacy metadata.
A legacy adapter without these exact operations is
unsupported before journal preparation or value I/O.

Platform journals and leases live under the current user's `.appsurface/local-secrets-state` directory; changing
`TMPDIR` or the working directory cannot split writer coordination. Platform mutations share a lease for the
application and environment, including all prefixes, index repair, namespace migration, and doctor writes. The file
backend locks the exact file path for every namespace. Acquisition has a ten-second deadline; failures return
`local-secret-maintenance-unavailable` (or the migration's structured I/O result). Internal coordinator calls also
honor cancellation. Migration capabilities are checked before `Prepared`, reads, or mutation.

Every journal replacement flushes the file and its directory entry before the next transition. Journals contain only
identifiers, operation id, and state. On retry, the coordinator reacquires the writer lease and rereads both exact
records. A still-present source is deleted only when its current value equals the current destination. A missing
source after durable verification finishes index publication and `Complete`; absence before verification is an
unrecoverable diagnostic. Failed or uncertain commits retain the last acknowledged state in the result; reopening
the persisted journal determines the actual resume point. The destination is never removed as rollback.

#### Recovering an unfinished file migration

The file fallback has one active journal slot. If retrying an exact-key migration cannot finish (for example, its
source disappeared before verification), preview the failed operation with
`appsurface secrets migrate-key recover --app MyApp --environment Development --store-file <path> --migration-id <id>`.
The preview reads the current journal and exact record presence under the shared maintenance lease; it does not print
values or change files. After inspecting both identifiers, repeat with `--apply --state <previewed-state>` to durably
retain the unfinished metadata and free the active slot. `--state` must match the journal state read again under the
lease. An unrelated migration may then proceed, but any source or destination that case-insensitively overlaps an
unresolved retained identifier returns `local-secret-migration-recovery-conflict` before value I/O.

The [optional recovery API](IAppSurfaceLocalSecretMigrationRecoveryStore.cs) exposes the same preview, retain, and
release behavior to programmatic callers. It stores only identifiers, namespace, operation ID, and state in a private
`<store>.migration-recovery.json` sidecar. The sidecar is written durably before the active journal receives its
`Retained` marker; an interruption between those steps leaves the old active journal blocking unrelated work until the
same recovery is retried. `appsurface secrets doctor` reports retained migration IDs with
`local-secret-migration-recovery-pending` as a successful readiness warning. It does not change application startup or
automatically delete either secret.

Once an operator has reconciled the exact records, preview with `--release` and repeat with
`--release --apply --state <previewed-state>` to lift only that operation's overlap guard. Release does not inspect
whether the surviving value is the intended one and does not write or delete any secret: the operator must decide
that using the normal store or native tooling. Do not remove the sidecar by hand or roll back to a binary that ignores
it while unresolved records remain. A malformed, unsafe, or oversized sidecar stops migration and recovery until its
posture or contents are repaired. The inventory is limited to 4,096 records and 8 MiB; reaching the limit stops new
retentions rather than dropping unresolved guards.

### Linux Nonstandard `secret-tool`

Use this only when your trusted Linux `secret-tool` install lives outside `/usr/bin` or `/bin`, such as a Nix,
Linuxbrew, Guix, or custom prefix install.

```bash
SECRET_TOOL=/absolute/path/to/secret-tool
test -x "$SECRET_TOOL"
appsurface secrets doctor --app MyApp --environment Development --secret-tool-path "$SECRET_TOOL"
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --secret-tool-path "$SECRET_TOOL" --stdin
```

Use the package option for app runtime configuration:

```csharp
services.ConfigureAppSurfaceLocalSecrets(options =>
{
    options.LinuxSecretToolPath = "/absolute/path/to/secret-tool";
});
```

The override must be an absolute path to an executable file. Empty, relative, missing, directory, non-executable, and
non-Linux overrides fail before command launch with `Problem`, `Cause`, `Fix`, `Docs`, and `Retryable` diagnostics.
`--secret-tool-path` cannot be combined with `--store-file`; `--store-file` is the deterministic example/test store, not
a platform-store verification path.

## Listing And Cleanup

`appsurface secrets list` prints only currently retrievable logical key names, never values. Platform-backed stores keep
a local name index so they can list safely across macOS Keychain, Linux Secret Service, and Windows Credential Manager.
When `list` can read the index and validate the named values, it silently prunes indexed names whose values are already
missing. If the platform store is locked, unavailable, or the index is corrupt, `list` fails with a paste-safe diagnostic
instead of hiding names it could not verify.

`appsurface secrets delete KEY` is narrowly idempotent for stale indexed names: if the value is already missing but the
platform index still contains `KEY`, delete removes the stale name and reports success. A key that has no value and no
index entry still reports `local-secret-missing`.

## Posture Modes

- `DevelopmentOnly` is the default. It permits `Development`, `Local`, and `Dev`.
- `SingleMachineSelfHosted` is explicit self-hosting. It does not provide team vault guarantees.
- `Disabled` stops LocalSecrets from resolving values.

Use environment variables, key-per-file, or `ForgeTrust.AppSurface.Config.GoogleSecretManager` in CI, containers, team
environments, and Google Cloud production hosts. When you are ready to move a local development value into an existing
Google Secret Manager secret, declare a LocalSecrets-to-Google job and run `appsurface secrets transfer plan` before
`appsurface secrets transfer apply --apply`. The workflow never prints the value and does not create secrets, grant IAM,
rotate, or disable old Google versions.

### Local integration testing with a production-labelled namespace

An IAM-authorized developer may materialize a pinned remote version into a local namespace named `Production` for a
local integration-test clone. That namespace remains local routing metadata; it does not authorize a remote operation or
make the machine a production host. Follow the [remote-to-local transfer guide](docs/materialize-remote-secrets-for-local-testing.md)
for the reviewed v2 configuration, numeric source-version requirement, value-safe plan/apply commands, and recovery
runbook.

The runtime provider remains fail-closed by default. A host that resolves the `Production` local namespace must opt in
deliberately:

```csharp
services.ConfigureAppSurfaceLocalSecrets(options =>
{
    options.Posture = LocalSecretsPostureMode.SingleMachineSelfHosted;
});
```

Without this host setting, the existing `local-secret-posture-development-only` diagnostic is expected. The transfer CLI
does not change host posture. `appsurface secrets delete <key>` removes only the local clone and its local transfer
attestation; it never deletes, disables, or changes a remote vault version.

## Metadata-Only Probes

`IAppSurfaceLocalSecretMetadataStore` is the LocalSecrets seam for transfer planning and overwrite checks. It answers
whether a normalized `AppSurfaceLocalSecretIdentity` is present without returning the stored secret value. Built-in
stores implement it directly:

- `InMemoryAppSurfaceLocalSecretStore` checks its key dictionary.
- `FileAppSurfaceLocalSecretStore` scans top-level storage-name metadata instead of deserializing value records.
- OS-backed platform stores use the LocalSecrets index and defer stale-entry verification until an apply path needs the
  value.

Use `IAppSurfaceLocalSecretStore.Get(...)` only when the caller is intentionally materializing the value, such as
`appsurface secrets transfer apply --apply`.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
## Structured Statuses

`AppSurfaceLocalSecretProvider.GetValue<T>` adapts LocalSecrets into the normal AppSurface config provider contract.
When callers need the LocalSecrets status directly, use `ResolveValue<T>(environment, key)`. It returns
`Found`, `Missing`, `Unavailable`, `Locked`, `UnsupportedPlatform`, `DisabledByPosture`, `InvalidIdentity`,
`ConversionFailed`, `ProviderFailed`, or terminal `MigrationRequired` with a paste-safe diagnostic and source name.
Only `Missing` means the
provider should fall through to lower-priority configuration.

## Platform Matrix

| Platform | Adapter | Exact-key migration |
| --- | --- | --- |
| macOS | Entitlement-free file-based `SecItem` v2 Keychain records, with retained v1 recovery reads | Exact-key migration is supported through the durable v1-to-v2 backend; requires an interactive user session. |
| Linux | Secret Service through trusted `secret-tool` paths | Exact-key migration is supported through the shared lease/journal backend; requires DBus/session Secret Service availability. |
| Windows | Credential Manager generic credentials for the current user | Exact-key migration is supported through the shared lease/journal backend; requires an interactive user profile. |
| Explicit file fallback | JSON file at `--store-file <path>` | Exact-key migration is supported when file posture passes; Unix mode-bit hardening is required and Windows/unknown ACL posture remains degraded. |

## Escape Hatches, Safest First

1. Keep the trusted Linux defaults when `secret-tool` is in `/usr/bin` or `/bin`.
2. Set `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath` or pass `--secret-tool-path` for a trusted nonstandard Linux
   executable that you verified with `test -x`.
3. Use `--store-file <path>` only for deterministic examples, tests, and docs snippets.
4. Replace the store with `UseAppSurfaceLocalSecretStore(...)` for controlled integration tests or app-specific local
   development behavior.
5. Change `FailClosedOnStoreFailure = false` only as a last resort. It can make unavailable local stores behave like
   missing values and hide secrets from lower-priority file providers.

## Linux Smoke Checklist

Deterministic tests cover resolver branches with fakes. Before release, run a live Linux desktop session smoke when DBus
and a Secret Service implementation are available:

```bash
appsurface secrets doctor --app MyApp --environment Development
printf '%s' "smoke-value" | appsurface secrets set Smoke:Value --app MyApp --environment Development --stdin
appsurface secrets get Smoke:Value --app MyApp --environment Development
appsurface secrets list --names-only --app MyApp --environment Development
appsurface secrets delete Smoke:Value --app MyApp --environment Development
```

For nonstandard installs, repeat the same commands with `--secret-tool-path "$SECRET_TOOL"` after `test -x "$SECRET_TOOL"`.

## Migration Ladder

```text
appsettings defaults < LocalSecrets < Google Secret Manager < environment variables
```

Keep the same AppSurface config key when moving from `.env`, `dotnet user-secrets`, or accidental
`appsettings.Development.json` secrets into LocalSecrets. Remote providers should preserve the same logical key.

Guides:

- [Local secrets without a remote vault](docs/local-secrets-without-a-remote-vault.md)
- [Migrate from dotnet user-secrets](docs/migrate-from-user-secrets.md)
- [Migrate from .env](docs/migrate-from-dotenv.md)
- [Use env or key-per-file in CI and containers](docs/use-env-or-key-per-file-in-ci-and-containers.md)
- [Move to Google Secret Manager](docs/move-to-future-remote-vault.md)
- [Migrate retained macOS Keychain records to v2](docs/macos-keychain-v2-migration.md)
- [Materialize a pinned Google Secret Manager version for local testing](docs/materialize-remote-secrets-for-local-testing.md)
