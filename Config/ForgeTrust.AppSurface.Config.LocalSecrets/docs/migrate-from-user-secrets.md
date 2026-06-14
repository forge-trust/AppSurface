# Migrate From dotnet user-secrets

Keep the same logical config keys when moving from `dotnet user-secrets` to LocalSecrets.

Current user-secrets shape:

```bash
dotnet user-secrets set "Stripe:ApiKey" "<secret>"
```

LocalSecrets shape:

```bash
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets get Stripe:ApiKey --app MyApp --environment Development
```

After migration, remove the old user-secrets value so diagnostics do not leave two local sources for the same key. Run
your app-owned `config diagnostics` command to verify that the key resolves from LocalSecrets and the value is redacted.
