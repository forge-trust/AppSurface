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

## First Secret

```bash
appsurface secrets init --app MyApp --environment Development
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets doctor --app MyApp --environment Development
dotnet run
appsurface config diagnostics
```

The diagnostics path reports where a value came from without printing the raw secret value.

## Posture Modes

- `DevelopmentOnly` is the default. It permits `Development`, `Local`, and `Dev`.
- `SingleMachineSelfHosted` is explicit self-hosting. It does not provide team vault guarantees.
- `Disabled` stops LocalSecrets from resolving values.

Use environment variables, key-per-file, or a remote vault in CI, containers, team environments, and production.

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
| Linux | Secret Service through `secret-tool` | Requires DBus/session secret service availability. |
| Windows | Credential Manager generic credentials for the current user | Requires an interactive user profile; use environment variables/key-per-file for services, CI, and containers. |

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
