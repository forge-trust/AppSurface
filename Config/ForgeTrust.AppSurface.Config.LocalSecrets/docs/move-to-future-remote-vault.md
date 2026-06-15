# Move To A Future Remote Vault

LocalSecrets is a pre-vault step. The migration ladder is:

```text
appsettings defaults < LocalSecrets < environment variables < future remote vault provider
```

Keep app code tied to logical AppSurface config keys such as `Stripe:ApiKey`. When a remote vault provider lands, move
the value to the vault under the same logical key and remove the local secret.

The expected win is continuity: wrappers, validation, audit redaction, and diagnostics should continue to work while the
source changes from local store to remote provider.
