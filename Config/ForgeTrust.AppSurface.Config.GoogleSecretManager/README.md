# ForgeTrust.AppSurface.Config.GoogleSecretManager

Google Cloud Secret Manager remote secret storage for AppSurface configuration.

Use this package when an AppSurface app running on Google Cloud needs production-like secret reads through the same
logical config keys used by `ForgeTrust.AppSurface.Config`. The provider is read-only, source-aware, fail-closed for
claimed keys by default, and keeps environment variables as the top emergency override.

The package also implements the typed file-declared secret contracts. Its canonical provider id is
`google-secret-manager`. For a `Secret<T>` destination, the provider validates the declared key, version, project, full
resource name, and `latest` policy locally before any Secret Manager call. Resolution caps `LookupTimeout` to the shared
synchronous deadline and decodes payloads as strict UTF-8 text. Missing resources, denied access, unavailable service
calls, invalid references, and provider failures remain distinct provider-neutral outcomes for the composition engine.

See the [canonical file-declared secret reference guide](../ForgeTrust.AppSurface.Config/docs/file-secret-references.md) and
the [executable golden path](../../examples/file-secret-references/README.md) for the complete descriptor, no-rescue Google
proof, exact environment rescue, and value-safe failure contract.

## Typed file-declared references

The inline descriptor uses the provider id `google-secret-manager` and a complete `key`, optional `version`, and static
`enabled` value. Local validation happens before any Secret Manager call. A disabled descriptor validates its shape but never
calls `IAppSurfaceGoogleSecretManagerClient`; an enabled missing or denied reference remains terminal unless the exact
destination receives a valid environment value.

The module registers one singleton and aliases that same instance to the legacy provider, raw-root, claim inspection,
declaration inspection, and child-reference interfaces. Child-reference cache entries are separate from legacy mapping
entries and include the environment, canonical version resource, and validation-affecting options. An optional
`TimeProvider` constructor argument makes TTL behavior deterministic in tests while the existing two-argument
constructor remains source-compatible.

Existing `MapSecret(...)` mappings and conventions remain the compatibility path for whole roots. Declaration inspection
reports explicit mappings at, above, or below a requested root, including mappings for ordinary destinations. Conventions
are inspected only when the convention already claims the requested root; descendant convention discovery is not widened.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
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
    options.MapSecret(AppSurfaceConfigKey.Parse("Stripe:ApiKey"), "stripe-api-key", version: "7");
    options.MapSecret("OpenAI:ApiKey", "projects/shared-secrets/secrets/openai-api-key/versions/3");
});
```

Grant the runtime identity `secretmanager.versions.access` only for the secrets it should read. Prefer Workload Identity
or host-owned Application Default Credentials. The runtime provider does not mint credentials, create secrets, assign
IAM, provision Terraform, shell out to `gcloud`, write secret versions, delete secrets, or rotate values. The package
also exposes a separate transfer client seam used by the declared `appsurface secrets transfer` workflow. Depending on
the configuration version, a validated plan can add a version to an existing Google secret or materialize one pinned
Google version into LocalSecrets for a developer-controlled local test environment.

## Provider Order

The AppSurface config order is:

```text
appsettings defaults < LocalSecrets < Google Secret Manager < environment variables
```

Environment variables stay above Google Secret Manager so an operator can override a broken remote secret without
changing code or mutating Secret Manager. File configuration and LocalSecrets stay below the remote provider. A claimed
Google Secret Manager logical key stops lower-priority providers when the remote lookup is unavailable, denied, invalid, or
cannot be converted. This is always terminal for the logical-key provider contract. For a `Secret<T>` root,
`FailClosedOnProviderFailure` (default `true`) controls whether a failed Google raw whole-root base permits fallback to
lower-priority bases. File-declared scalar references always report their claimed failures; the option does not make
those failures fall through. Keep it enabled when lower-priority bases must not mask an unavailable secret. See the
[coordinated upgrade guide](../../guides/config-key-migration.md#google-convention-migration) when migrating conventions.

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
    options.EnableConventionResolver("TenantA", secretIdPrefix: "tenanta-", version: "5");
});
```

The exact logical prefix key and its descendants are claimed. The provider encodes the complete logical key by
lowercasing each valid segment and joining segments with `--`, then prepends the exact non-empty `secretIdPrefix`.
Both `EnableConventionResolver` overloads require this prefix explicitly; an empty prefix fails startup validation.
For example, `Payments:Api-Key` becomes `prefix-payments--api-key`. Every convention in one provider instance must use
the same ordinal-exact prefix. Dots, Unicode, empty segments, leading or trailing hyphens, and `--` inside a segment
are unrepresentable; use an explicit typed mapping for those keys.

The convention codec is injective and does not probe historical generated names. Before upgrading, inventory known
convention declarations with `AppSurfaceGoogleSecretMigrationInventory.Inventory(options, knownKeys)`. This overload
assumes the old raw key prefix and old secret-id prefix match the current convention prefixes. If either historical
prefix differs, supply both explicitly with
`AppSurfaceGoogleSecretMigrationInventory.Inventory(options, knownKeys, legacyLogicalKeyPrefix, legacySecretIdPrefix)`;
the raw key prefix preserves its original spelling and punctuation, and the old secret-id prefix may be empty. For each
result, add its `MapSecretSnippet` so the existing legacy secret id remains selected without a network probe. Unknown
ad-hoc keys cannot be inventoried because the provider never enumerates Secret Manager.

Typed overloads are the primary API. The retained string overloads parse strict colon-delimited keys and are convenient
for source migration; the obsolete provider `GetValue` and `ResolveValue` helpers throw terminal failures and should be
replaced with `Resolve(new ConfigProviderRequest(...))` by provider integrations.

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
| `config-provider-failed` | A claimed lookup, payload decode, or typed conversion failed. The diagnostic is value-safe. |
| `config-key-collision` | Two logical identities claimed one exact full Google resource, or a projection was ambiguous. |
| `config-key-unrepresentable` | A convention key cannot produce a valid Google secret id; add an explicit mapping. |
| `config-key-prefix-overlap` | Convention prefixes overlap by complete logical-key segments. |

`IConfigAuditReporter` records Google Secret Manager as a provider source and marks returned values sensitive. Audit
reports show source evidence and redaction state, not raw secrets. An audit scope owns a 30-second deadline, up to
256 uncached remote lookups, and four concurrent remote lookup leases; a rejected lease is a terminal, value-safe
incomplete-audit diagnostic.

Google's [version access API](https://docs.cloud.google.com/secret-manager/docs/reference/rest/v1/projects.secrets.versions/access)
can resolve `latest` or an app-owned alias to a numeric version. Audit provenance retains the exact returned version
name with its payload, including a returned project number for a requested project ID. Secret and location identifiers
remain ordinal-exact, and a pinned numeric version cannot resolve to another version. Project ID/number equivalence is
accepted from the configured client response; it is not inferred by lowercasing or merging identifiers.

## Cache Behavior

By default every lookup reads through the client. Set `CacheTtl` only when the app can tolerate delayed visibility after
rotation. The successful payload cache is bounded to `CacheCapacity` entries, which defaults to 1,024; expired and
failed entries are evicted. Cache keys are exact full version resources, and payload bytes are copied at every package
boundary. A cached alias lookup retains the resolved version name alongside the bytes, so audit does not refetch a
possibly newer version to identify an older cached value.

```csharp
services.ConfigureAppSurfaceGoogleSecretManager(options =>
{
    options.CacheTtl = TimeSpan.FromMinutes(5);
});
```

Only successful payload reads are cached. The TTL uses elapsed monotonic time, so wall-clock corrections do not extend
or shorten a cached payload's lifetime. Failures are evicted after the shared fetch completes, so a later caller can
retry. Child-reference payloads are decoded before entering their cache, so invalid UTF-8 is retried on the next
request. For mapped legacy lookups, invalid UTF-8, failed typed conversion, and null conversion results evict their
exact cached payload generation;
a slow failed conversion cannot remove a newer successful entry. Concurrent misses for one exact resource share a
side-effect-free `Lazy` fetch. Cancelling one request stops its waiter while the shared fetch continues for other callers.
Already-cancelled callers throw before native claims, cached reads or client access; an expired audit deadline instead
records `config-audit-deadline` as incomplete coverage. Ad-hoc claims are checked and bounded before network access.
Resolved names also participate in ordinal resource claims. If two distinct logical keys resolve to one concrete name,
both claims become terminal, including cached values and pending resolutions. Additional resolved names consume the
`MaxAdHocClaims` budget (default 1,024); a long-lived provider following many alias rotations can exhaust it and returns
`config-provider-failed` rather than retaining an unbounded history. Recreate the provider to reset its claim lifetime.
Audit work shares the operation scope's aggregate limit of 30 seconds, 256 remote lookups, and four concurrent leases;
exhausted limits produce incomplete audit coverage rather than unbounded remote work.

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
to transfer a declared LocalSecrets or Google source into an existing Google secret, or to materialize one pinned Google
version into LocalSecrets for local testing. Configuration names the source and destination endpoints plus exact job rows.
Every Google-to-LocalSecrets source must be a full numeric version resource, never `latest` or another alias.
For prerequisites, guarded replacement, recovery, and host posture, follow the [remote-to-local testing guide](../ForgeTrust.AppSurface.Config.LocalSecrets/docs/materialize-remote-secrets-for-local-testing.md).

```bash
appsurface secrets transfer plan --config ./secret-transfer.json --job staging-to-production --out ./promotion.plan.json
appsurface secrets transfer apply --config ./secret-transfer.json --plan ./promotion.plan.json --apply --confirm staging-to-production
```

The plan is value-free and metadata-only. Apply rechecks its digest, expiry, canonical source resource, and destination
preconditions before materializing a UTF-8 payload. For Google destinations, `--replace` adds a new enabled version; it
does not overwrite, disable, or destroy an existing version. For LocalSecrets destinations, `--replace` requires exact
job confirmation and a matching AppSurface local transfer attestation. Treat endpoint configuration and plan artifacts
as operator-controlled security-sensitive files.

## Migration

- [Migrate from LocalSecrets](docs/migrate-from-local-secrets.md)
