# LocalSecrets example

This sample shows the complete local-secret workflow without printing the secret value.

Run from the repository root with a deterministic file store:

```bash
STORE=$(mktemp -t appsurface-local-secrets.XXXXXX.json)
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- secrets init --app LocalSecretsExample --environment Development --store-file "$STORE"
printf '%s' "sk_test_example" | dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- secrets set Stripe:ApiKey --app LocalSecretsExample --environment Development --store-file "$STORE" --stdin
DOTNET_ENVIRONMENT=Development APPSURFACE_LOCAL_SECRETS_FILE="$STORE" dotnet run --project examples/local-secrets -- show-secret-posture
DOTNET_ENVIRONMENT=Development APPSURFACE_LOCAL_SECRETS_FILE="$STORE" dotnet run --project examples/local-secrets -- config diagnostics
DOTNET_ENVIRONMENT=Development APPSURFACE_LOCAL_SECRETS_FILE="$STORE" dotnet run --project examples/local-secrets -- config diagnostics --debug
```

Expected command output redacts the value:

```text
Stripe:ApiKey resolved from configuration. Value: [redacted]
```

Use the default OS-backed store by omitting `--store-file` and `APPSURFACE_LOCAL_SECRETS_FILE`. Use environment
variables, key-per-file, or a remote vault for CI, containers, team environments, and production.

The `--debug` form expands only bounded child topology beneath known audit entries. It keeps local-secret values redacted
and labels the resulting support artifact as expanded; use the canonical command when child topology is unnecessary.

If an IAM-authorized developer needs a local integration-test clone of a specific Google Secret Manager version, use the
[remote-to-local materialization guide](../../Config/ForgeTrust.AppSurface.Config.LocalSecrets/docs/materialize-remote-secrets-for-local-testing.md).
It uses a reviewed numeric remote version, never prints the value, and does not turn this sample into a remote-vault
integration example.
