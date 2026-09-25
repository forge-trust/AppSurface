# File-declared secret references

Use a typed `Secret<T>` member when a configuration model needs a sensitive scalar whose resource, version, and activation
belong in checked-in configuration. The JSON object declares the reference; the provider supplies its payload. Ordinary
models keep the existing configuration behavior. Start with the [executable example](../../../examples/file-secret-references/README.md)
for a network-free proof through the real Google module.

## First success

For an existing AppSurface application, install the [Google package](../../ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md),
which brings in core Config:

```sh
dotnet package add ForgeTrust.AppSurface.Config.GoogleSecretManager
```

Declare a root and register the module in the host's existing module:

```csharp
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

[ConfigKey("Payments")]
public sealed class PaymentsConfig : Config<PaymentsOptions> { }

public sealed class PaymentsOptions
{
    public required Uri Endpoint { get; init; }
    public required Secret<string> ApiKey { get; init; }
}

public sealed class PaymentsModule : IAppSurfaceModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
        builder.AddModule<AppSurfaceGoogleSecretManagerModule>();
    }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.ConfigureAppSurfaceGoogleSecretManager(options => options.ProjectId = "my-project");
    }
}
```

Place the complete root in `appsettings.json` for Production, or `appsettings.Development.json` for Development. The file
provider uses separate environment snapshots; an unqualified file does not automatically become a Development base.

```json
{
  "Payments": {
    "Endpoint": "https://payments.example",
    "ApiKey": {
      "key": "payments-api-key",
      "version": "4",
      "provider": "google-secret-manager",
      "enabled": false
    }
  }
}
```

The disabled declaration validates locally and produces `Enabled=false`, `HasValue=false`, and `ResolvedProvider=null`
without fetching the inline reference. Set the exact environment variable `PAYMENTS__APIKEY` to supply a temporary value;
it remains disabled but reports `HasValue=true` and `ResolvedProvider=EnvironmentConfigProvider`. Check these state members
without printing `Value`. The [example commands](../../../examples/file-secret-references/README.md) cover this first success,
Google success with a fake client, and safe failure in one terminal session. A remote run requires the existing
[Google authentication and IAM setup](../../ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md).

## Descriptor

| Field | Shape | Default | Meaning |
| --- | --- | --- | --- |
| `key` | nonblank string | required | Opaque provider resource input; never normalized as an AppSurface path. |
| `version` | nonblank string | provider policy | Optional version or alias. A full Google version resource goes in `key` and must not also specify `version`. |
| `provider` | lower-case kebab-case string | omitted | Restricts resolution to a registered canonical id; Google's id is `google-secret-manager`. |
| `enabled` | boolean | `true` | Static reference activation; `false` skips its `Resolve` calls. |

These are direct members; no `$secret` envelope is used. Unknown fields, wrong JSON types, duplicate case-insensitive
members, explicit null descriptors, and scalar file values at a `Secret<T>` path are invalid. A disabled descriptor must
still name a valid key and registered compatible provider. Ordinary objects with `key`, `version`, or `enabled` remain
ordinary when their destination is not `Secret<T>`.

## Scalar values

`Secret<T>` supports the scalar types handled by [`ConfigValueConverter`](../ConfigValueConverter.cs), including strings,
booleans, numeric types, enums, GUIDs, dates/times, time spans, and URIs. Arrays, dictionaries, collections, object payloads,
and nested `Secret<Secret<T>>` are unsupported. A provider returns text; scalar conversion occurs only after one provider
has uniquely succeeded. Provider and environment date/time, time-span, and URI text may be unquoted; JSON-quoted text for
those types remains accepted. Empty strings count as present when the destination accepts them. Null is absence, not a value.

| Enabled | HasValue | Meaning | Value access |
| --- | --- | --- | --- |
| `true` | `false` | Empty undeclared destination or an unbound default | `Value` throws; `TryGetValue` returns false. |
| `true` | `true` | Enabled reference, lower source, or environment value supplied the destination | Both accessors return the scalar. |
| `false` | `false` | Disabled reference without another source | `Value` throws; `TryGetValue` returns false. |
| `false` | `true` | Disabled reference with a sensitive base or exact environment value | Both accessors return the scalar. |

The public constructor creates an enabled empty wrapper. Runtime owns resolved and disabled wrapper construction.
`ResolvedProvider` is null when empty; otherwise it identifies the winning secret provider or base/environment provider.
Default JSON serialization, debugger display, and `ToString()` omit the payload. An application that reads `Value` or
`TryGetValue` owns that sensitive material; third-party serializers and private-memory inspection are outside this guarantee.
A C# `required Secret<T>` requires the wrapper member, not a present payload. Use application validation of `HasValue` when
availability is mandatory. Framework validation treats the wrapper as opaque and never recursively reads its payload.

## Supported models

Composition uses the default System.Text.Json contract. Finite reference-type roots, records, constructor-bound members,
init setters, `JsonPropertyName`, and `JsonInclude` members are supported. Serialized names determine logical paths.
Ignored members do not declare destinations. A secret-containing readonly property needs a supported constructor parameter.

Secret destinations inside collections, recursive secret graphs, custom converters hiding secret members, polymorphic
secret graphs, extension-data contracts, direct `Secret<T>` roots, and `ConfigStruct<T>` roots are rejected locally.
Ordinary collection members elsewhere in an opted-in root retain environment-array/dictionary support.
A custom options or converter pipeline is not accepted by this release.

## Base sources

Resolution first tries the ordered direct-root environment candidates. A parseable complete root bypasses file policy,
base providers, inline providers, and descendant environment values. Invalid earlier candidates retain safe conversion
diagnostics while later candidates are tried.
Complete means every declared `Secret<T>` destination is present in the environment object. Each supplied value must
convert to its scalar type; explicit JSON `null` deliberately supplies an empty destination. An omitted secret member
rejects that root candidate and lets later candidates or normal composition supply the root.

Without that bypass, the engine validates the plan, selects the first resolved raw whole-root base by existing provider
priority, resolves each declared secret, applies exact environment overrides, and binds the root once. A sensitive base
such as [LocalSecrets](../../ForgeTrust.AppSurface.Config.LocalSecrets/README.md#typed-file-declared-secret-references)
can supply a scalar to a secret slot. Non-sensitive file data supplies ordinary members and declaration metadata.
For ordinary members, if every environment candidate is invalid, normal composition keeps the lower file value and
retains the conversion diagnostics.
A terminal base failure prevents lower-provider fallback; supply a complete direct environment root or repair that base.

An enabled declaration claims its slot, replacing lower sensitive material even when the reference fails. A disabled or
absent declaration preserves eligible lower material. Exact environment values win last at their own destinations.
A valid sibling or containing-object override cannot rescue another failed secret slot. A root with no contribution
remains missing, allowing existing wrapper defaults; failed composition never falls back to those defaults.

## Provider selection

An explicit `provider` selects one registered canonical id and uses that provider's native timeout. Without a constraint,
all registered providers validate locally and compatible providers are queried in canonical id order. Every local
validation finishes before any secret resolution starts, including validation of shadowed file declarations.

Only `Unclaimed` and `Missing` allow probing to continue. Exactly one success is required. Two successes are ambiguous,
even if their payloads happen to match. Access denial, unavailability, invalid references, or unexpected failures prevent
uniqueness from being established. A valid exact environment value can rescue that runtime slot; malformed descriptors,
unknown registrations, and other plan errors need a plan correction or a complete direct-root bypass.

## Resolution budget

`AppSurfaceConfigOptions` is captured per host. All limits must be positive:

| Option | Default | Boundary |
| --- | --- | --- |
| `ProviderlessResolutionBudget` | 30 seconds | One cooperative monotonic deadline shared by all unconstrained slots in a root invocation. |
| `MaxCompositionGraphDepth` | 32 | Member depth beneath the root. |
| `MaxCompositionGraphNodes` | 4096 | Structural nodes, including the root. |
| `MaxSecretDestinationsPerRoot` | 256 | Scalar secret slots in one root. |

Configure these through `services.Configure<AppSurfaceConfigOptions>(...)` in the host module. Rebuild the host to change
a captured plan. Providers cap native synchronous timeouts to `ConfigSecretResolutionContext.Remaining`. Explicitly
constrained providers receive an effectively unbounded `TimeSpan.MaxValue` budget, so providers must still set a finite
native timeout of their own and use the lesser of that timeout and `Remaining`. The deadline cannot interrupt a
non-cooperative implementation, but the engine rejects a late success and does not start another
provider after expiry. Synchronous providers must not block on asynchronous tasks. Explicitly constrain the provider when
ownership is known; providerless resolution pays the cost of establishing uniqueness.

## Provider contract

A custom provider implements `IConfigSecretProvider`: a unique lower-case kebab-case `Id`, local `ValidateReference`, and
synchronous `Resolve`. Keep `Id` available during startup: an invalid id or ordinary getter exception becomes a value-safe
registration failure before reference validation or provider I/O, while a process-fatal exception escapes startup. The
following network-free provider is useful in a test host:

```csharp
public sealed class DemoSecretProvider : IConfigSecretProvider
{
    public string Id => "demo-provider";
    public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) =>
        reference.Key == "demo" && reference.Version == "1"
            ? ConfigSecretReferenceValidation.Supported()
            : ConfigSecretReferenceValidation.Unclaimed();

    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>
        ConfigSecretProviderResolution.Resolved("test-only", ConfigSecretSourceMetadata.Create(Id));
}

// Inside ConfigureServices:
services.AddSingleton<IConfigSecretProvider, DemoSecretProvider>();
```

Use the sealed result factories for `Resolved`, `Unclaimed`, `Missing`, `AccessDenied`, `Unavailable`, `InvalidReference`,
and `ProviderFailed`. Success accepts nonnull text plus allowlisted `ConfigSecretSourceMetadata`; failures retain stable
classifications and retryability, never arbitrary provider exception text. Source metadata allows a canonical provider id,
`remote`, `local`, or `custom` kind, and an optional explicitly approved opaque correlation token. Keys and versions are
excluded from default reference formatting and serialization. Providers must not log them or their payloads.

If `Resolve` throws an ordinary non-fatal exception, the composition engine converts it to `ProviderFailed` and retains only
the safe provider observation and framework-owned failure details; it does not retain or render the exception message, stack,
or arbitrary provider text. `OutOfMemoryException`, `StackOverflowException`, and `AccessViolationException` are treated as
process-fatal and escape this redaction boundary. Providers should return the documented result factories for expected
operational outcomes and reserve exceptions for failures that the host should handle at its outer boundary.

Whole-root providers also implement `IConfigCompositionValueProvider.ResolveRaw`, returning the legacy whole-root JSON
and truthful sensitivity through `ConfigCompositionValueResolution`. A typed-only provider must implement the local
`IConfigProviderClaimInspector` and return `Unclaimed` for the requested root, or the opted-in root fails before reads.
`IConfigSecretDeclarationSource.InspectClaims` reports existing explicit mappings and conventions without broadening
which roots a convention claims. An invalid claim or ordinary exception from inspection fails composition with a
value-free `secret-claim-overlap` diagnostic; process-fatal exceptions escape. Alias all implemented capabilities to the same concrete singleton; avoid registering
separate provider objects through each interface. Google and LocalSecrets already provide these registrations.

Known opted-in roots compile in the host's pre-start lifecycle, before ordinary hosted services start, without constructing
wrappers or reading enabled references. This ordering also validates fast console hosts before a command can request
shutdown. Runtime and live audit share that compiler, immutable registration snapshot, and execution logic. Caches retain structural metadata
and up to 1,024 root plans per host. Additional root identities compile without being retained; their resolution semantics
are unchanged. Payloads and binding converters belong to one invocation. Provider payload caches, when present, remain
provider-owned; Google keeps its raw-root and declared-reference caches separate.

## Atomic file layers

A higher file layer repeats and replaces the complete descriptor. Omitted optional members reset to their defaults;
`key` cannot be inherited from a lower descriptor. Every layer is locally validated, including shadowed declarations.
For composition, member paths are compared case-insensitively across layers, so `Service.ApiKey` and `service.apikey`
refer to one logical destination and the higher layer wins while ordinary sibling members remain merged. Complete
secret descriptor replacement, including omitted optional fields, is enforced by the type-aware compiler after this
ordinary raw merge; objects at non-secret destinations keep normal deep-merge behavior.
Malformed, unreadable, empty, non-object, duplicate-member, or case-colliding applicable files remain failure events for
opted-in roots. The loader skips duplicate JSON members, including case-only duplicates, with a safe diagnostic for
legacy roots; other accepted files retain the established merged view. File locations accompany
composition failures where available; descriptor values and parser exception text do not.

## Path identity

AppSurface logical keys use dot or colon separators with case-insensitive segment comparison. JSON member names are the
canonical destination names and cannot contain literal dots, colons, or empty segments. External resource keys remain
opaque and retain their exact spelling.

Environment candidates follow existing scoped flat, unscoped flat, scoped hierarchical, and unscoped hierarchical order,
with duplicates removed. For `Payments.ApiKey`, candidates include `PRODUCTION_PAYMENTS_APIKEY`, `PAYMENTS_APIKEY`,
`PRODUCTION__PAYMENTS__APIKEY`, and `PAYMENTS__APIKEY`. Exact casing matters on systems with case-sensitive environment
lookups. If distinct destinations normalize to the same candidate, compilation fails before I/O and reports both paths
and the shared environment name. Rename the serialized members instead of relying on candidate order.

## Migrate Map Secret

Migrate one complete requested root at a time. A legacy mapping and inline descriptor cannot target equal, ancestor, or
descendant paths together. A mapped plain sibling also makes a partial typed migration invalid. For two sibling secrets,
change both members to `Secret<T>`, add both complete descriptors, and remove the corresponding `MapSecret(...)` mappings
in the same change. Existing whole-root mappings remain supported when they do not overlap inline declarations.

Keep temporary exact environment values during rollout if needed, then run with them removed and verify Google's
canonical provider id and `HasValue=true`. Environment rescue proves compatibility; it does not prove the Google path.
Rollback means restoring the previous application artifact together with its matching configuration and package set,
not just changing `enabled`. Keep that previous artifact until the rollout rehearsal succeeds.

The [package smoke script](https://github.com/forge-trust/AppSurface/blob/main/scripts/file-secret-references-smoke.sh) and two-sibling tests provide deterministic
repository evidence. Stable publication additionally requires a separately scoped real Skoolit root migration, measured
glue deletion, Google verification without environment rescue, and previous-artifact rollback. This pull request does
not mutate that external application or claim its rehearsal has passed.

## Provider failures

A missing enabled reference reports `secret-not-found`; an outage reports `secret-provider-unavailable`; an unexpected
provider failure reports `secret-provider-failed`. Correct the reference or provider condition, or temporarily supply a
valid exact environment value. Invalid scalar material reports `secret-value-conversion-failed`. Other failed siblings
remain failed when one slot is rescued. Providers can mark transient conditions retryable without exposing raw errors.

## Access denied

`secret-provider-access-denied` means the runtime identity could not access the declared resource. Check the existing
[Google IAM guidance](../../ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md), correct the identity or access
policy, and retry. A valid exact environment value is the emergency override. The diagnostic intentionally omits the
resource key, version, payload, and raw provider exception.

## Diagnostics

`ConfigurationCompositionException` exposes `EnvironmentName`, `RootKey`, and ordered `ConfigCompositionFailure` records.
Each record provides a stable code, canonical `Path`, safe `ProviderId`, `Retryable`, `Problem`, `Cause`, `Fix`, and a `Docs`
link to this guide. `Source` identifies the declaration location when available. Alias conflicts also supply `RelatedPath`
and `EnvironmentVariableName`. Failures are ordered by path, code, and provider id and do not retain an inner exception.

Effective configuration audit performs the same enabled reads as runtime. It may consume network, IAM, provider-cache,
and latency budgets; it is not a compile-only command. Disabled references skip inline reads, although a selected remote
whole-root base may still be read. A complete direct environment root skips all provider reads. Audit emits opaque leaf
state and declared nesting without invoking a secret member's value getter. Descriptor values stay redacted even when
the plan fails. Framework diagnostics, JSON output, and default formatting omit payloads and external resource identities.
A future separately named compile-only preflight, asynchronous providers, structured secrets, and background rotation
remain outside this release; see the [design decisions](../../../docs/designs/issue-807-file-declared-secret-references.md).
