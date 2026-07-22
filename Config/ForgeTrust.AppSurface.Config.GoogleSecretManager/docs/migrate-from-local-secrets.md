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

1. Create the Secret Manager secret through your deployment-owned process.
2. Grant the app runtime identity `secretmanager.versions.access` for the version it will read. Grant the operator
   running transfer `secretmanager.secrets.get`, `secretmanager.versions.list`, and `secretmanager.versions.add` on the
   existing destination secret so plan can verify its metadata and apply can add the version.
3. Declare the source, destination, and exact key mapping in a reviewed promotion job, then create its value-free plan:

   ```bash
   appsurface secrets transfer plan --config ./secret-promotion.json \
     --job local-to-production \
     --out ./local-to-production.plan.json
   ```

4. Apply only after the plan reports the intended row:

   ```bash
   appsurface secrets transfer apply --config ./secret-promotion.json \
     --plan ./local-to-production.plan.json \
     --apply --confirm local-to-production
   ```

   If the destination secret has no enabled versions, the apply writes the first enabled version. If it already has
   enabled versions, create the plan with `--replace` to add another enabled version. The workflow does not create the
   secret parent, disable old versions, destroy versions, grant IAM, or rotate values.

5. Add `ForgeTrust.AppSurface.Config.GoogleSecretManager`.
6. Map the existing logical key to the written version:

   ```csharp
   services.ConfigureAppSurfaceGoogleSecretManager(options =>
   {
       options.ProjectId = "my-production-project";
       options.MapSecret("Stripe:ApiKey", "stripe-api-key", version: "<applied numeric version>");
   });
   ```

   Replace `<applied numeric version>` with the numeric version from the value-free apply receipt, such as `3`, after
   confirming that version is the intended production read target.

7. Run `appsurface config diagnostics` or an app-owned config audit endpoint in the deployed environment.
8. Confirm the source is `GoogleSecretManagerConfigProvider` and the value is redacted.
9. Delete the old LocalSecrets value from the local machine when it is no longer needed.

The promotion configuration carries batch mappings and keeps source/sink authority reviewed in one place:

```json
{
  "version": 1,
  "endpoints": [
    { "name": "production", "provider": "google", "environment": "production", "credential": { "mode": "applicationDefault" } }
  ],
  "jobs": [
    { "name": "local-to-production", "source": "local", "destination": "production", "allowMutableLocalSource": true,
      "rows": [ { "key": "Stripe:ApiKey", "destination": "projects/my-production-project/secrets/stripe-api-key" } ] }
  ]
}
```

Apply validates the whole job before any payload read. Writes are ordered but not cross-secret atomic. Before mutation,
the workflow persists a value-free journal for the planned rows, then updates that journal atomically after each row. If
the process crashes, resume from the latest journal or receipt; rows already confirmed as `Written` are skipped, rows not
confirmed are revalidated before another write, and indeterminate Google writes require operator reconciliation instead
of automatic retry. Journals and receipts record resources, row statuses, written version names, and configuration/plan
identity digests only; they never include secret values, payload bytes, secret-value hashes, credentials, or raw provider
exceptions.

## Pitfalls

- Do not rename `Stripe:ApiKey` in app code during the move. Preserve the logical key so wrappers, validation, audit
  redaction, and diagnostics continue to work.
- Do not rely on `latest` as a hidden production default. Pin a version or opt in with `AllowLatest()` for workflows
  that intentionally accept a mutable alias.
- Do not leave a production fallback secret in `appsettings.*.json`. Claimed remote failures fail closed by default so
  a missing IAM grant or outage does not silently read a stale file value.
- Keep environment variables for emergency overrides, CI injection, and short-lived operational recovery.
- Do not use promotion as a rotation system. `--replace` adds a new enabled version to an existing secret and leaves
  older versions under operator control.
- Do not use promotion jobs for implicit convention mapping. Every row names the local logical key and Google secret
  explicitly.
