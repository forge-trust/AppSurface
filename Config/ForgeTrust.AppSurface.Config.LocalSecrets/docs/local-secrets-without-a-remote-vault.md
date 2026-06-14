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

Do not use LocalSecrets as a team vault, CI secret source, container secret source, production rotation system, or remote
audit trail. Use environment variables, key-per-file, or a remote vault for those cases.
