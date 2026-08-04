# Materialize a pinned remote secret for local testing

Use this guide when a developer is already authorized to read a Google Secret Manager value and needs a reproducible, local integration-test clone. The `appsurface secrets transfer` workflow moves the value directly between the approved providers so it is not exposed in the terminal, a plan, a receipt, or a shell history.

This is an intentionally local operation. A namespace such as `Production` is routing metadata for the local store; it neither authorizes remote access nor makes the workstation a production host. Google Secret Manager IAM remains the authorization boundary. See Google's [access-control guide](https://cloud.google.com/secret-manager/docs/access-control) for the roles that grant version metadata or payload access.

## Before you begin

- Install the [AppSurface CLI](../../../Cli/ForgeTrust.AppSurface.Cli/README.md) and configure Application Default Credentials or an approved credential file.
- Ensure the caller can read the specific remote version: `secretmanager.versions.get` is required for planning and `secretmanager.versions.access` is required for apply.
- Choose one numeric Google Secret Manager version, such as `projects/my-project/secrets/db-password/versions/42`. Do not use `latest` or another alias. The CLI rejects aliases so the reviewed plan remains bound to one immutable source. Use Google's [version metadata guide](https://cloud.google.com/secret-manager/docs/view-secret-version) to list available versions without reading a payload.
- Use a built-in LocalSecrets store. The platform store and file fallback are supported after `doctor`; custom `IAppSurfaceLocalSecretStore` implementations are deliberately rejected because the transfer coordinator cannot establish the required local locking and recovery guarantees for them.

## Declare the one-way transfer

Create a version-2 configuration. Each row has a Google source and the built-in `local` destination; the local key is the same normalized key used by the target application.

```json
{
  "version": 2,
  "endpoints": [
    {
      "name": "production-gsm",
      "provider": "google",
      "environment": "production",
      "credential": { "mode": "applicationDefault" }
    }
  ],
  "jobs": [
    {
      "name": "clone-production-db-password",
      "source": "production-gsm",
      "destination": "local",
      "rows": [
        {
          "key": "Database:Password",
          "source": "projects/my-project/secrets/db-password/versions/42"
        }
      ]
    }
  ]
}
```

Do not place a secret value in the configuration. The plan is value-free and contains resource identities, canonical resources, actions, expiry, destination preconditions, diagnostic codes, and configuration and plan identity digests.

## Plan and apply

Use identical `--app` and `--environment` arguments for `doctor`, `plan`, and `apply`. Any normalized namespace is allowed, including `Production`.

```bash
appsurface secrets doctor --app MyApp --environment Production
appsurface secrets transfer plan --config ./remote-to-local.json --job clone-production-db-password --app MyApp --environment Production --out ./clone-production-db-password.plan.json
appsurface secrets transfer apply --config ./remote-to-local.json --plan ./clone-production-db-password.plan.json --app MyApp --environment Production --apply --confirm clone-production-db-password
```

Planning makes metadata-only calls and confirms that the local target is absent. Apply rechecks the plan and source identity before accessing the value, then writes it while holding a per-local-key lock. Its value-free receipt reports `CreatedLocalSecret`, `ReplacedLocalSecret`, or `RecoveredLocalSecret`.

An existing local target is a `Conflict` by default. If the prior value was created by this transfer coordinator, create the plan with `--replace` and apply it with the exact `--confirm <job>` string. Replacement is deliberately confirmed even when the source environment is not labelled production. Legacy or manually created local values are not eligible for replacement; delete the local value and create a fresh plan instead.

## Recovery and cleanup

If a process stops after the local write begins, do not rerun a normal apply. Use the receipt with `--resume`. Under the same local lock, AppSurface compares the local and pinned remote values in memory. It commits only an equal value; missing, different, unreadable, corrupt, or unsafe state remains a value-safe conflict or indeterminate result for manual reconciliation.

`appsurface secrets delete <key>` removes only the local value and its local transfer attestation. It never reads, deletes, disables, or modifies the remote Google Secret Manager version.

## Run a host against the clone

The transfer CLI does not configure runtime posture. A host that resolves a namespace other than `Development`, `Local`, or `Dev` must explicitly opt in to `LocalSecretsPostureMode.SingleMachineSelfHosted` as described in the [LocalSecrets package README](../README.md#local-integration-testing-with-a-production-labelled-namespace). Keep this setup limited to a single developer-controlled machine; use remote vaults, workload identity, or environment/key-per-file injection for team, container, CI, and production-host scenarios.
