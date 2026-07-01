# Migrate From LocalSecrets

LocalSecrets is the single-machine pre-vault posture. Google Secret Manager is the remote provider for Google Cloud
hosts that need team-safe storage, IAM-mediated access, and production-like source evidence.

The migration ladder is:

```text
appsettings defaults < LocalSecrets < Google Secret Manager < environment variables
```

Keep the logical AppSurface config key stable. Move the value from LocalSecrets into Secret Manager, map the same
logical key to the remote secret, then remove the local value after the deployed app proves it reads from Google Secret
Manager.

## Steps

1. Create the Secret Manager secret and version through your deployment-owned process.
2. Grant the app runtime identity `secretmanager.versions.access` for that secret version.
3. Add `ForgeTrust.AppSurface.Config.GoogleSecretManager`.
4. Map the existing logical key:

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.ProjectId = "my-production-project";
    options.MapSecret("Stripe:ApiKey", "stripe-api-key", version: "7");
});
```

5. Run `appsurface config diagnostics` or an app-owned config audit endpoint in the deployed environment.
6. Confirm the source is `GoogleSecretManagerConfigProvider` and the value is redacted.
7. Delete the old LocalSecrets value from the local machine when it is no longer needed.

## Pitfalls

- Do not rename `Stripe:ApiKey` in app code during the move. Preserve the logical key so wrappers, validation, audit
  redaction, and diagnostics continue to work.
- Do not rely on `latest` as a hidden production default. Pin a version or opt in with `AllowLatest()` for workflows
  that intentionally accept a mutable alias.
- Do not leave a production fallback secret in `appsettings.*.json`. Claimed remote failures fail closed by default so
  a missing IAM grant or outage does not silently read a stale file value.
- Keep environment variables for emergency overrides, CI injection, and short-lived operational recovery.

