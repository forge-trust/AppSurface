# LocalSecrets example

This sample shows the complete local-secret workflow without printing the secret value.

Run from the repository root with a deterministic file store:

```bash
STORE=$(mktemp -t appsurface-local-secrets.XXXXXX.json)
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- secrets init --app LocalSecretsExample --environment Development --store-file "$STORE"
printf '%s' "sk_test_example" | dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- secrets set Stripe:ApiKey --app LocalSecretsExample --environment Development --store-file "$STORE" --stdin
DOTNET_ENVIRONMENT=Development APPSURFACE_LOCAL_SECRETS_FILE="$STORE" dotnet run --project examples/local-secrets -- show-secret-posture
DOTNET_ENVIRONMENT=Development APPSURFACE_LOCAL_SECRETS_FILE="$STORE" dotnet run --project examples/local-secrets -- config diagnostics
```

Expected command output redacts the value:

```text
Stripe:ApiKey resolved from configuration. Value: [redacted]
```

Use the default OS-backed store by omitting `--store-file` and `APPSURFACE_LOCAL_SECRETS_FILE`. Use environment
variables, key-per-file, or a remote vault for CI, containers, team environments, and production.
