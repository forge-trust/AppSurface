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

Use environment variables, key-per-file, or a remote vault in CI, containers, team environments, and production.

## Release Guidance

Use the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for the current package-facing prerelease story, risk notes, and migration guidance.

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
appsettings defaults < LocalSecrets < environment variables < future remote vault provider
```

Keep the same AppSurface config key when moving from `.env`, `dotnet user-secrets`, or accidental
`appsettings.Development.json` secrets into LocalSecrets. Later vault providers should preserve the same logical key.

Guides:

- [Local secrets without a remote vault](docs/local-secrets-without-a-remote-vault.md)
- [Migrate from dotnet user-secrets](docs/migrate-from-user-secrets.md)
- [Migrate from .env](docs/migrate-from-dotenv.md)
- [Use env or key-per-file in CI and containers](docs/use-env-or-key-per-file-in-ci-and-containers.md)
- [Move to a future remote vault](docs/move-to-future-remote-vault.md)
