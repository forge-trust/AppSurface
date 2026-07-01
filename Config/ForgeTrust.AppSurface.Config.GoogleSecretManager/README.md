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
or host-owned Application Default Credentials. This package does not mint credentials, create secrets, assign IAM,
provision Terraform, shell out to `gcloud`, write secret versions, delete secrets, or rotate values.

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

The seam returns structured statuses rather than Google exceptions, which keeps provider tests deterministic and avoids
network access.

## Migration

- [Migrate from LocalSecrets](docs/migrate-from-local-secrets.md)
