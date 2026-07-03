# Move To Google Secret Manager

LocalSecrets is a pre-vault step. The migration ladder is:

```text
appsettings defaults < LocalSecrets < Google Secret Manager < environment variables
```

Keep app code tied to logical AppSurface config keys such as `Stripe:ApiKey`. Move the value to Google Secret Manager,
map the same logical key with `ForgeTrust.AppSurface.Config.GoogleSecretManager`, and remove the local secret after the
remote source is proven.

The expected win is continuity: wrappers, validation, audit redaction, and diagnostics continue to work while the source
changes from local store to remote provider. Environment variables still win above Google Secret Manager for emergency
overrides.

See the package guide: [Migrate from LocalSecrets](../../ForgeTrust.AppSurface.Config.GoogleSecretManager/docs/migrate-from-local-secrets.md).
