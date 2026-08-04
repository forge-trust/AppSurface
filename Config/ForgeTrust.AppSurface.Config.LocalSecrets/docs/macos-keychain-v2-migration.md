# Migrate macOS LocalSecrets Keychain records to v2

Use this guide when a macOS AppHost reports `local-secret-migration-required`. That status means the current process can
confirm that a retained legacy Keychain record is readable, but no v2 record exists yet. It is terminal by design: an
AppHost never writes Keychain records during configuration resolution or claims cross-process parity before migration.

## Before you start

This migration applies only to the macOS OS-backed LocalSecrets store. It does not apply to `--store-file`, CI,
containers, shared/team environments, or remote vaults. Pin the same application, environment, and optional prefix for
both the CLI and runtime `AppSurfaceLocalSecretsOptions`; changing any of them selects a different LocalSecrets
namespace.

```csharp
services.ConfigureAppSurfaceLocalSecrets(options =>
{
    options.ApplicationName = "MyApp";
});
```

## Recovery path

Run the explicit migration command from the same user session as the AppHost:

```bash
appsurface secrets migrate --app MyApp --environment Development
dotnet run
appsurface config diagnostics
```

Add `--prefix Payments` only when the AppHost configures the same `KeyPrefix`. The command reports logical key names,
actions, and `Migrated`, `AlreadyV2`, and `Failed` counts; it never prints values. A nonzero command result means one or
more keys were not migrated. Resolve the displayed value-safe diagnostic and run the exact same command again.

Migration is resumable and idempotent. It retains v1 records for recovery, writes and freshly verifies v2 values before
adding their v2 index entries, and never overwrites an existing v2 value. Once v2 exists, it is canonical: update it with
`appsurface secrets set`, not by editing a legacy v1 record and rerunning migration.

## macOS smoke checklist

Run this only in an interactive macOS user session. Use generated, non-secret smoke values and verify status/startup,
not the values themselves:

```bash
appsurface secrets init --app MyApp --environment Development
printf '%s' "smoke-one" | appsurface secrets set Smoke:One --app MyApp --environment Development --stdin
printf '%s' "smoke-two" | appsurface secrets set Smoke:Two --app MyApp --environment Development --stdin
printf '%s' "smoke-three" | appsurface secrets set Smoke:Three --app MyApp --environment Development --stdin
dotnet run
printf '%s' "smoke-one-updated" | appsurface secrets set Smoke:One --app MyApp --environment Development --stdin
dotnet run
```

The entitlement-free v2 store uses macOS file-based `SecItem` Keychain records. It deliberately does not configure an
access group, `kSecUseDataProtectionKeychain`, or an executable-specific custom ACL: those postures either need app-like
entitlements or do not generalize safely to arbitrary AppHosts. If macOS reports locked or unavailable Keychain status,
unlock the current user Keychain session and retry; use a remote or environment-backed provider for CI, containers, or
team scenarios.
