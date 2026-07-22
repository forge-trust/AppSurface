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

For Linux, AppSurface treats `secret-tool` as an external command with an explicit trust boundary. By default it uses
only `/usr/bin/secret-tool`, then `/bin/secret-tool`. It does not execute `secret-tool` from `PATH`; a PATH match is
reported only as ignored diagnostic context.

## First Secret

```bash
appsurface secrets init --app MyApp --environment Development
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets doctor --app MyApp --environment Development
dotnet run
appsurface config diagnostics
```

The diagnostics path reports where a value came from without printing the raw secret value.

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
| `local-secret-file-posture-unsupported` | The path shape or checked Unix posture is unsafe for fallback storage, such as a symbolic link, directory path, loose mode bits, or writable non-sticky ancestor. |

Example deterministic file fallback check:

```bash
appsurface secrets doctor --app MyApp --environment Development --store-file ./.appsurface/local-secrets.json
```

The command prints `Problem`, `Cause`, `Fix`, `Docs`, and `Retryable` without printing secret values.
`local-secret-file-posture-degraded` is a degraded readiness result for explicit local/test fallback workflows;
`local-secret-file-posture-unsupported` is a fail-closed result that stops reads and writes until the path is moved or
repaired. Prefer the OS-backed store for normal local development.

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

## Release Guidance

Use the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for the current package-facing prerelease story, risk notes, and migration guidance.

## Structured Statuses

`AppSurfaceLocalSecretProvider.GetValue<T>` adapts LocalSecrets into the normal AppSurface config provider contract.
When callers need the LocalSecrets status directly, use `ResolveValue<T>(environment, key)`. It returns
`Found`, `Missing`, `Unavailable`, `Locked`, `UnsupportedPlatform`, `DisabledByPosture`, `InvalidIdentity`,
`ConversionFailed`, or `ProviderFailed` with a paste-safe diagnostic and source name. Only `Missing` means the
provider should fall through to lower-priority configuration.

## Platform Matrix

| Platform | Adapter | Notes |
| --- | --- | --- |
| macOS | Keychain generic passwords through Security.framework | Requires an interactive user session when Keychain prompts. |
| Linux | Secret Service through trusted `secret-tool` paths | Uses `/usr/bin/secret-tool`, then `/bin/secret-tool`, or an explicit absolute `LinuxSecretToolPath`/`--secret-tool-path`. Requires DBus/session secret service availability. |
| Windows | Credential Manager generic credentials for the current user | Requires an interactive user profile; use environment variables/key-per-file for services, CI, and containers. |
| Explicit file fallback | JSON file at `--store-file <path>` | Unix mode-bit hardening only; Windows and unknown filesystem ACL posture is reported as degraded. |

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
