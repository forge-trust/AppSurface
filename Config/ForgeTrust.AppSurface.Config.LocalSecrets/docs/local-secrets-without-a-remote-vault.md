# Local Secrets Without A Remote Vault

Use LocalSecrets when a solo AppSurface app needs one-machine local development secrets and is not ready for a remote
vault. The app keeps the same logical AppSurface config key, while LocalSecrets stores the value under the app and
environment on the current machine.

```bash
dotnet package add ForgeTrust.AppSurface.Config.LocalSecrets
appsurface secrets init --app MyApp --environment Development
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets doctor --app MyApp --environment Development
dotnet run
appsurface config diagnostics
```

LocalSecrets is fail-closed. Environment variables win. True missing local secrets may fall through to files, but
locked, unavailable, unsupported, invalid, or posture-disabled stores stop resolution for claimed keys.

## Explicit file fallback

Use the OS-backed store unless you deliberately need deterministic file fallback for an example, an unsupported local
session, or a test:

```bash
appsurface secrets doctor --app MyApp --environment Development --store-file ./.appsurface/local-secrets.json
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --store-file ./.appsurface/local-secrets.json --stdin
appsurface secrets get Stripe:ApiKey --app MyApp --environment Development --store-file ./.appsurface/local-secrets.json
```

The fallback JSON file contains raw secret values. On Unix, AppSurface creates missing fallback directories with `0700`
mode bits and writes or repairs the file with `0600` mode bits during `set`, `delete`, and `doctor`. Existing parent
directories are inspected, not modified in place; loose parent directories fail closed with a paste-safe diagnostic.
Reads inspect existing files before returning a value; symbolic-link paths, directory paths, and non-canonical mode bits
fail closed with a paste-safe diagnostic.

`doctor` distinguishes posture states:

- `local-secret-store-ready`: the file can be opened and its checked posture is ready.
- `local-secret-file-posture-repaired`: Unix file mode bits were tightened to `0600`.
- `local-secret-file-posture-degraded`: the file can be opened and `doctor` can exit successfully, but v1 cannot prove owner-only ACL posture for this platform path.
- `local-secret-file-posture-unsupported`: the path shape or checked Unix posture is unsafe for fallback storage, including symbolic links, directory targets, loose mode bits, and writable non-sticky ancestors.

This is mode-bit hardening, not a universal ACL guarantee. Windows file fallback is degraded by design in v1; use
Windows Credential Manager through the OS-backed store for normal local development.

Do not use LocalSecrets as a team vault, CI secret source, container secret source, production rotation system, or remote
audit trail. Use environment variables, key-per-file, or a remote vault for those cases.
