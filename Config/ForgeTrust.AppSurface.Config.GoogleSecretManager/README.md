# ForgeTrust.AppSurface.Config.GoogleSecretManager

Google Cloud Secret Manager remote secret storage for AppSurface configuration.

Use this package when an AppSurface app running on Google Cloud needs production-like secret reads through the same
logical config keys used by `ForgeTrust.AppSurface.Config`. The provider is read-only, source-aware, fail-closed for
claimed keys by default, and keeps environment variables as the top emergency override.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the
[package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk,
migration guidance, and readiness.

## Install

```bash
dotnet package add ForgeTrust.AppSurface.Config.GoogleSecretManager
```

Register `AppSurfaceGoogleSecretManagerModule` beside your app modules. It brings in `AppSurfaceConfigModule`, registers
`GoogleSecretManagerConfigProvider`, and uses Application Default Credentials through the Google Cloud client library.

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.ProjectId = "my-production-project";
    options.MapSecret("Stripe:ApiKey", "stripe-api-key", version: "7");
    options.MapSecret("OpenAI:ApiKey", "projects/shared-secrets/secrets/openai-api-key/versions/3");
});
```

Grant the runtime identity `secretmanager.versions.access` only for the secrets it should read. Prefer Workload Identity
or host-owned Application Default Credentials. The runtime provider does not mint credentials, create secrets, assign
IAM, provision Terraform, shell out to `gcloud`, write secret versions, delete secrets, or rotate values. The package
also exposes a separate transfer client seam used by the declared `appsurface secrets transfer` promotion workflow; that
workflow can add a new version to an existing secret only when the user applies a validated plan.

## Provider Order

The AppSurface config order is:

```text
appsettings defaults < LocalSecrets < Google Secret Manager < environment variables
```

Environment variables stay above Google Secret Manager so an operator can override a broken remote secret without
changing code or mutating Secret Manager. File configuration and LocalSecrets stay below the remote provider. A claimed
Google Secret Manager key stops lower-priority providers when the remote lookup is unavailable, denied, invalid, or
cannot be converted, unless `FailClosedOnProviderFailure` is set to `false`.

Unmapped keys are not claimed and continue through the normal provider chain.

## Explicit Mappings

Prefer explicit mappings for production:

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.ProjectId = "my-production-project";
    options.MapSecret("Billing:Stripe:ApiKey", "billing-stripe-api-key", version: "12");
});
```

Short secret ids require `ProjectId` and a version from either the mapping or `DefaultVersion`. Full version resource
names, such as `projects/prod/secrets/billing-stripe-api-key/versions/12`, already contain the project, secret, and
version.

`latest` is mutable and is not the hidden default. If you need it for a development, canary, or app-owned rollout
workflow, opt in explicitly:

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.ProjectId = "my-dev-project";
    options.AllowLatest();
    options.MapSecret("Stripe:ApiKey", "stripe-api-key", version: AppSurfaceGoogleSecretManagerOptions.LatestVersion);
});
```

For production release discipline, pin a numeric version or an app-owned stable alias and rotate by updating the mapping
or alias through your deployment process.

## Convention Resolver

The convention resolver is opt-in and scoped. It is useful when an app owns a narrow config prefix and the corresponding
secret names follow the same pattern.

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.ProjectId = "my-production-project";
    options.EnableConventionResolver("TenantA:", secretIdPrefix: "tenanta-", version: "5");
});
```

Only keys under the exact logical prefix are claimed. The provider normalizes the key suffix by replacing `:`, `.`, and
`_` with `-`, lowercasing invariantly, and prepending `secretIdPrefix`. Broad conventions can claim more keys than
intended, so production apps should prefer explicit mappings for high-value secrets.

## Typed Values

Secret payloads must be UTF-8 text. The provider converts values with the same AppSurface config converter used by
LocalSecrets: strings, numbers, booleans, enums, `Guid`, nullable scalars, and JSON object payloads are supported.
Conversion failures are terminal diagnostics for claimed keys by default and never include the raw payload.

Use normal `Config<T>` wrappers or direct `IConfigManager.GetValue<T>` calls:

```csharp
public sealed class StripeApiKeyConfig : Config<string>
{
}
```

## Diagnostics And Audit

Google Secret Manager diagnostics are paste-safe. They identify the logical key, provider, status, and remediation class
without printing secret values, payload bytes, credentials, or raw exception messages.

| Diagnostic code | Meaning |
| --- | --- |
| `google-secret-manager-secret-missing` | The claimed secret or version was not found. |
| `google-secret-manager-access-denied` | The runtime identity cannot access the secret version. |
| `google-secret-manager-invalid-secret-resource` | The mapping, project, version, or Secret Manager resource name is invalid. |
| `google-secret-manager-unavailable` | Secret Manager or the client was unavailable within the bounded lookup. |
| `google-secret-manager-cancelled` | The lookup was cancelled. |
| `google-secret-manager-invalid-secret-payload` | The payload was not valid UTF-8 text. |
| `google-secret-manager-conversion-failed` | The payload could not be converted to the requested config type. |

`IConfigAuditReporter` records Google Secret Manager as a provider source and marks returned values sensitive. Audit
reports show source evidence and redaction state, not raw secrets.

## Cache Behavior

By default every lookup reads through the client. Set `CacheTtl` only when the app can tolerate delayed visibility after
rotation:

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.CacheTtl = TimeSpan.FromMinutes(5);
});
```

Only successful payload reads are cached. Failures are not cached, and the provider does not run a background refresh.

## Testing

Use `UseAppSurfaceGoogleSecretManagerClient(...)` to replace the Google client seam in tests:

```csharp
services.UseAppSurfaceGoogleSecretManagerClient(new FakeGoogleSecretManagerClient());
```

The seam returns payload bytes from a resource name and timeout. Test fakes can return deterministic bytes or throw
Google `RpcException` instances so the provider's status mapping remains deterministic without network access.

Use `UseAppSurfaceGoogleSecretTransferClient(...)` to replace the explicit transfer seam used by CLI transfer workflows:

```csharp
services.UseAppSurfaceGoogleSecretTransferClient(new FakeGoogleSecretTransferClient());
```

The transfer seam is separate from the read-only provider client. It probes secret parents and enabled versions, returns
structured payload access only during apply, and adds a new enabled version to an existing secret. It does not include
create, delete, disable, destroy, rotate, IAM, Terraform, or `gcloud` operations.

Transfer writes use `GoogleSecretManagerTransferStatus.IndeterminateWrite` when Google may have accepted a new version
but did not return a definitive response. Callers must not retry that result automatically because `AddSecretVersion`
has no idempotency key; reconcile the destination's versions first, then create a new reviewed plan for any remaining
work. Definitive missing-resource, access-denied, and invalid-resource failures remain separately classified.

## CLI Transfer

Use the [AppSurface CLI transfer workflow](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-secrets-transfer)
to promote a declared LocalSecrets or Google source into an existing Google secret. Configuration names the source and
destination endpoints plus exact job rows; Google production sources must use numeric version resources, never `latest`.

```bash
appsurface secrets transfer plan --config ./secret-promotion.json --job staging-to-production --out ./promotion.plan.json
appsurface secrets transfer apply --config ./secret-promotion.json --plan ./promotion.plan.json --apply --confirm staging-to-production
```

The plan is value-free and metadata-only. Apply rechecks its digest, expiry, and destination preconditions before
materializing a UTF-8 payload. `--replace` adds a new enabled Google version; it does not overwrite, disable, or destroy
an existing version. Treat endpoint configuration and plan artifacts as operator-controlled security-sensitive files.

## Migration

- [Migrate from LocalSecrets](docs/migrate-from-local-secrets.md)
