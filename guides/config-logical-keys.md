# Logical configuration keys

An AppSurface key has one identity across configuration providers. `Payments:ApiKey`
and `payments:apikey` compare equal; `Payments:Api-Key` and `Payments:Api:Key` do not.
Start with the [Config quickstart](../Config/ForgeTrust.AppSurface.Config/README.md),
then use this reference when declaring keys or moving a setting between providers.

## Grammar and construction

Only `:` separates hierarchy. Every segment is nonempty and contains no colon,
control character, or leading/trailing whitespace. Interior whitespace and
non-control Unicode remain unchanged. There is no trimming, culture-sensitive
comparison, Unicode normalization, or arbitrary logical-key length limit. Each
provider has separate native representation limits.

| Input | Meaning |
| --- | --- |
| `Payments:ApiKey` | Two segments |
| `Logging:LogLevel:Microsoft.Hosting.Lifetime` | Three segments; the dots are literal |
| `Payments:Api-Key` | Two segments; the hyphen is literal |
| `A_B`, `A__B`, `A/B`, `A\B` | One literal segment each; convention support varies |
| `A B` | One segment with interior whitespace |
| Empty, `:A`, `A:`, `A::B`, ` A`, `A ` | Invalid |

```csharp
var key = AppSurfaceConfigKey.Parse("Payments:ApiKey");
var literal = AppSurfaceConfigKey.FromSegments("Microsoft.Hosting.Lifetime");
var nested = AppSurfaceConfigKey.FromSegments("Logging", "LogLevel", literal.Value);
```

`Parse` uses strict grammar and throws `FormatException` for invalid input.
`TryParse` returns false and a null output on failure. `FromSegments` copies its
array; null arrays throw `ArgumentNullException`, and empty arrays or invalid
members throw `ArgumentException` naming `segments` and the invalid member index.
An element is a literal segment: passing `"A:B"` as one element is invalid.

`Value` and `ToString()` preserve spelling. `Segments` is immutable. Equality and
hashing use `StringComparer.OrdinalIgnoreCase`; there is no lowercase canonical
text. `IsSameOrDescendantOf` compares complete segments: `Payments` matches itself
and `Payments:ApiKey`, but not `PaymentsArchive:ApiKey`. Deterministic reports sort
by ordinal ignore-case rendering and then ordinal spelling.

## Application strings and declarations

`IConfigManager.GetValue<T>(environment, typedKey)` resolves a typed identity.
The string overload parses once using `AppSurfaceConfigKeyOptions`. In release
train 1, `LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic` is the default:
`Payments.ApiKey` becomes `Payments:ApiKey` and produces `config-key-legacy-dot-path`.
A string containing a colon is strict, so its dots stay literal. Use the typed API
for one literal dotted segment during this train. The
[migration guide](config-key-migration.md) describes the strict-default and removal
trains.

```csharp
services.Configure<AppSurfaceConfigKeyOptions>(options =>
    options.LegacyDotPathBehavior = LegacyDotPathBehavior.Strict);
services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Payments:ApiKey"));
```

Attributes compose nested paths with colons. Public
`ConfigKeyAttribute.GetLogicalKey(Type)` is strict. Module discovery parses raw
attribute fragments using finalized application options, preserving migration
origin through wrappers and requests. `Root` ignores declaring-type ancestors.
`GetKeyPath(Type)` is obsolete and returns a colon rendering.

String `AddConfigAuditKey<T>` registrations defer parsing until the service provider
exists, so later options registrations are honored. The immutable declaration
registry belongs to one host. Exact canonical declarations merge audit metadata;
case-only declarations and legacy/canonical declarations for one identity fail
startup with `config-key-collision`. Direct obsolete `ConfigAuditKnownEntry(string, …)`
construction is strict because it cannot consult DI options.

### Explicit type registration

[`AddAppSurfaceConfig<TConfig>()`](../Config/ForgeTrust.AppSurface.Config/README.md#three-step-domain-example)
selects one concrete `IConfig` wrapper from any compatible assembly, including a
Domain assembly outside the normal discovery inputs. It uses the same declaration
registry and finalized key parser as module discovery. It does not scan an assembly,
accept a caller key, or parse an attribute while services are being registered.
Without `ConfigKeyAttribute`, the existing type-name and declaring-type hierarchy
convention supplies the declaration. With an attribute, nested fragments compose
under the same `Root` rule as discovered wrappers.

`ConfigKeyAttribute.GetLogicalKey(Type)` remains a strict public helper: it parses
attribute fragments with canonical colon syntax regardless of host compatibility
options. Host discovery and explicit registration instead resolve those fragments
through the finalized `AppSurfaceConfigKeyOptions`, so release-train legacy handling
and origin tracking remain host-owned. Do not call the strict helper to predict a
legacy-translated host key. The [migration guide](config-key-migration.md) documents
the package and rollback constraints for adopting this API.

Malformed fragments keep the parser's existing exception boundary: strict
`AppSurfaceConfigKey.Parse` throws `FormatException`, while the default finalized
`ConfigKeyInputParser` throws `ArgumentException` for invalid input after its
configured dot-only translation. A custom input parser retains its own exception
behavior. Canonical/case-only and legacy/canonical identity collisions still fail
with `InvalidOperationException` and `config-key-collision` when the finalized
declaration registry is constructed.

Registration contributes an attributed declaration even when the wrapper assembly
is not scanned. That record identifies the selected wrapper for normal audit
reporting; it is not evidence that the runtime singleton has been activated. Normal
module discovery remains lazy. An audit report may separately construct an
inspection wrapper and resolve providers, so keep report-time inspection and reads
separate from runtime singleton activation in counters and troubleshooting.

Invalid attribute grammar and key collisions still fail through the existing
finalized parser/registry boundary. Invalid audit traversal bounds also retain their
existing later audit diagnostic and safe fallback; explicit registration does not
turn them into an earlier key-registration failure.

The registration conflict code is `config-registration-conflict`. Its canonical
documentation URI is
[`https://appsurface.dev/guides/config-logical-keys`](https://appsurface.dev/guides/config-logical-keys),
the same target as `ConfigDiagnosticCatalog.Reference`. Messages identify the
problem and cause, safe wrapper identifier, actual exact-unkeyed descriptor count
or lifetime, repair action, and canonical link. They never invoke opaque factories
or include configuration values.

| Registration failure | Family and recovery |
| --- | --- |
| Null collection/type | `ArgumentNullException`; provide the required value before registration. |
| Unsupported selected type shape | `ArgumentException`; select a closed concrete class implementing `IConfig` with a public instance constructor. |
| Multiple exact unkeyed descriptors or a non-singleton | `InvalidOperationException` with `config-registration-conflict`; retain one unkeyed singleton or repair descriptors before another registration call. Keyed descriptors are independent. |
| Incompatible package family | `AppSurfacePackageCompatibilityException`; upgrade the coordinated package set and rebuild external providers and wrappers together. |
| Invalid declared key grammar | `ArgumentException` from the default finalized `ConfigKeyInputParser`; strict `AppSurfaceConfigKey.Parse` throws `FormatException`. Correct the declaration; custom parser exceptions retain their existing behavior. |
| Case-only or canonical/legacy identity collision | `InvalidOperationException` with `config-key-collision` when the finalized registry is constructed; use one spelling for each logical identity. |

The canonical [packed consumer proof](https://github.com/forge-trust/AppSurface/blob/main/tests/config-package-consumer/README.md)
demonstrates that a package-only Domain wrapper gains its attributed audit identity
without broadening discovery.

## Resolution and collision domains

The environment provider runs first. Other providers run in descending priority,
with stable registration order breaking priority ties. Missing continues traversal.
Found returns a value, including valid value-type defaults such as zero or false.
Terminal retains one diagnostic and stops lower-provider traversal. The environment
patcher then has one transactional chance to rescue a terminal result using valid
present children. A failed child rejects the entire patch. Without a successful
patch, the manager throws `ConfigurationResolutionException`.

Compatibility notices from environment child patches are published only when the
whole patch succeeds. The manager logs each committed notice once using its child
key and provider, and attributes the patched result to the environment provider.
A failed or unapplied candidate does not emit successful-alias notices or report a
missing base provider as resolved.

This terminal rule also applies when a provider cannot represent a key or capture
its source. Register an explicit mapping when a provider's convention cannot
represent a valid logical identity. An upper provider's failure is not converted
to absence merely because a lower provider could supply a value.

| Arrangement | Outcome |
| --- | --- |
| Exact duplicate inside one source layer | Terminal duplicate/collision |
| Case variants inside one layer | Terminal collision |
| Canonical and distinct legacy aliases inside one layer | Terminal collision, even if values match |
| Exact spelling in later documented layer | Intentional override; origins retained |
| Case-only spelling difference across ordered layers | Terminal collision |
| Same logical key in separate providers | Normal manager precedence |
| Different logical keys mapped to one native identity | Unrepresentable/collision before value retrieval |

The declaration spelling represents a known key in reports. Otherwise, the winning
provider's spelling represents it. Source records retain their own exact spellings
and native locators. Neither logical equality nor report ordering rewrites native
Google resources, LocalSecrets backend identifiers, file names, or environments.
Expanded object members keep display labels such as `Payments:Settings.Database.Host`,
and collection entries use brackets such as `Payments:Services[0].Name`. Those labels
are presentation only. Provider matching uses the corresponding colon source path,
including literal segment punctuation, so labels must not be parsed as logical keys.

Captured `ConfigAuditEntry` records include `ConfigPath`, the colon rendering used
for logical comparison independently of `Key`'s display label. This keeps a literal
`A.B` distinct from the expanded member at `A:B`, even when both display as `A.B`.
Paths containing hidden dictionary identifiers omit `ConfigPath`; dictionary HMAC
comparison metadata continues to govern those entries. Older or manually authored
child records without logical paths retain structural display evidence and cannot
prove logical equivalence from display punctuation alone. When captures mix child
records with and without `ConfigPath` under one logical root, those child items are
`Uncomparable` with `config-diff-logical-path-evidence-missing`; metadata upgrades do
not imply that children were added or removed. Root `Key` strings already
carry strict logical identity and remain comparable. Regenerate captured reports to
obtain the logical-path evidence. Source spellings still participate in provenance
comparison, while a change in logical-key casing alone is not a value change.


## Provider encodings

JSON properties contribute literal segments; array positions contribute numeric
segments. A property with a colon cannot be represented as one logical segment.
Raw duplicate properties and case collisions poison the affected identity and its
aggregate ancestors. Unrelated siblings remain available. Arrays replace arrays as
a unit; scalar/object/array replacement removes superseded descendant provenance.
Null remains missing; empty objects and arrays are present.

Environment convention names uppercase ASCII segment spelling and join hierarchy
with `__`. `Payments:Api-Key` becomes `PAYMENTS__API-KEY`; the hyphen is preserved.
Convention segments allow ASCII letters, digits, `.`, `-`, `/`, `\`, and single
interior underscores. Edge underscores, `__` inside a segment, and non-ASCII content
require an explicit `AppSurfaceEnvironmentConfigOptions.MapKey` mapping. A key whose
unscoped form starts with the current environment's scoped prefix also requires a
mapping, preventing `Production:Payments:ApiKey` from sharing a native name with
scoped `Payments:ApiKey`.

An explicit suffix replaces convention and legacy candidates in both layers. The
scoped `<ENVIRONMENT>__<SUFFIX>` wins over unscoped `<SUFFIX>`, after both layers
pass collision validation. Explicit suffixes accept ASCII letters, digits, and
single interior underscores only. They retain exact caller-owned casing.

| Suffix | Accepted |
| --- | --- |
| `PAYMENTS_API_KEY`, `Payments_ApiKey` | Yes |
| `PAYMENTS__APIKEY`, `_PAYMENTS`, `PAYMENTS_` | No |
| `PAYMENTS-APIKEY`, `PAYMENTS.APIKEY`, non-ASCII text | No |

For every expected native name, one exact match resolves; one case-only match is
`config-key-environment-name-case`; multiple case variants are `config-key-collision`.
These rules are identical for Unix and Windows snapshots. One top-level operation
uses one immutable snapshot for direct values, collections, patching, aliases, and
audit. Discovery considers known declarations and mappings, not unrelated process
variables.

## Inspect environment candidate names {#environment-candidate-inspection}

`IEnvironmentConfigCandidateInspector` reports ordered names that could be considered by
native environment lookup or direct composition lookup. It is a names-only projection:
it does not read environment variables or files, call providers, resolve or convert a
value, establish presence or source ownership, prove aggregate completeness, or consume
native claim capacity. The caller owns the inventory, its trust and comparer, platform
presence checks, and the acceptance policy. See the [packed package consumer proof](https://github.com/forge-trust/AppSurface/blob/main/tests/config-package-consumer/README.md).
Adopt it with coordinated package versions selected by the
[package chooser](../packages/README.md) and follow the
[coordinated migration guide](config-key-migration.md); candidate package proof is not
published-feed or release evidence.

Configure `AppSurfaceEnvironmentConfigOptions` and every other host option before build
or first resolution. The provider and inspector freeze the same mapping snapshot on first
use in one service container. Resolve the inspector after host configuration is final;
a mapping changed after the first provider/inspector use is stale for that container and
requires correcting configuration and rebuilding it. Directly constructed providers
have isolated snapshots.

Start with an existing AppSurface host and the .NET 10 SDK. For this standalone
snippet, run from an owned project directory. On macOS, set
`DOTNET_USE_POLLING_FILE_WATCHER=1` if the host's default file watching blocks in a
restricted development environment, as the [packed consumer verifier](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify_config_package_consumer.py)
does. This host setup is separate from inspector source activity.

```csharp
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;

var builder = Host.CreateApplicationBuilder(args);
var environment = new MetadataExampleEnvironment();
builder.Services.AddSingleton<IEnvironmentProvider>(environment);
var context = new StartupContext([], new NoHostModule(), EnvironmentProvider: environment)
{ OverrideEntryPointAssembly = typeof(NoHostModule).Assembly };
new AppSurfaceConfigModule().ConfigureServices(context, builder.Services);
foreach (var register in context.CustomRegistrations) register(builder.Services);

// Finalize the environment mapping before Build or any provider/inspector resolution.
builder.Services.Configure<AppSurfaceEnvironmentConfigOptions>(options =>
    options.MapKey("Payments:ApiKey", "Approved_ApiKey")
        .MapKey("Feature:Endpoint", "Feature_Endpoint")
        .MapKey("Service", "Root_Config")
        .MapKey("Service:ApiKey", "Service_ApiKey"));
using var host = builder.Build();
var inspector = host.Services.GetRequiredService<IEnvironmentConfigCandidateInspector>();

var typed = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey"));
var strict = inspector.InspectNativeKey("Production", "Payments.ApiKey");
var path = inspector.InspectCompositionPath("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey"));

// Caller policy: reject any supplied environment name that could override this setting.
var inventory = new HashSet<string>(["Approved_ApiKey"], StringComparer.Ordinal);
var rejected = typed.Candidates.Any(candidate => inventory.Contains(candidate.Name));
if (!rejected || environment.Reads != 0)
    throw new InvalidOperationException("Unexpected candidate-policy or source-read result.");
Console.WriteLine($"Supplied environment override rejected: {rejected}; inspector source reads: {environment.Reads}");

try
{
    inspector.InspectNativeKey("Production", "Payments::ApiKey");
}
catch (FormatException failure) when (failure.Data["Code"] as string == "config-key-invalid")
{
    Console.Error.WriteLine($"{failure.Data["Code"]}: {failure.Message}");
    Console.Error.WriteLine($"Recovery: {failure.Data["Recovery"]}");
    Console.Error.WriteLine(failure.HelpLink);
    Console.Error.WriteLine(failure.Data["Fix"]); // Keep the actionable fix last.
}

sealed class MetadataExampleEnvironment : IEnvironmentProvider
{
    public string Environment => "Production";
    public bool IsDevelopment => false;
    public int Reads { get; private set; }
    public string? GetEnvironmentVariable(string name, string? defaultValue = null)
    {
        Reads++;
        throw new InvalidOperationException("Metadata inspection must not read a value.");
    }
    public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables()
    {
        Reads++;
        throw new InvalidOperationException("Metadata inspection must not capture values.");
    }
}
```

The snippet prints `Supplied environment override rejected: True; inspector source reads: 0`.
This counter covers the supplied AppSurface environment source during inspector construction/calls;
normal host construction and application callbacks remain outside that boundary.

The regular module graph registers the inspector as a singleton; do not register it
manually. The standalone consumer fixture uses `StartupContext`,
`AppSurfaceConfigModule.ConfigureServices`, and `CustomRegistrations` to prove the
public package route. The `string` overload uses strict colon grammar, so
`"Payments.ApiKey"` is one literal segment. It does not apply a manager migration
option that translates dot-only application strings.

For the fixture mapping above, the typed result is exactly
`PRODUCTION__Approved_ApiKey` (scoped, explicit mapping) followed by
`Approved_ApiKey` (unscoped, explicit mapping). Both have `IsLegacyAlias == false`.
The strict-string input `"Payments.ApiKey"` is a distinct one-segment key; it yields
`PRODUCTION__PAYMENTS.APIKEY`, `PAYMENTS.APIKEY`, `PRODUCTION_PAYMENTS_APIKEY`,
`PRODUCTION__PAYMENTS_APIKEY`, and `PAYMENTS_APIKEY`, in that order. Only the final
three are `IsLegacyAlias == true`; every candidate has `IsExplicitMapping == false`.
For direct composition path `Payments:ApiKey`, the names are
`PRODUCTION_PAYMENTS_APIKEY`, `PAYMENTS_APIKEY`, `PRODUCTION__PAYMENTS__APIKEY`, and
`PAYMENTS__APIKEY`; the first two are flat (`IsHierarchical == false`) and the latter
two hierarchical. These plans use their distinct entry-point semantics despite the
similar-looking strings.
Without an explicit mapping, typed keys produce canonical names only. Strict string
inspection preserves compatibility aliases for its strict-string origin, while a typed
key never gains those aliases. Composition inspection applies its direct-path rules and
does not use native explicit mappings. `Scope` records the producing layer;
`IsHierarchical` describes the composition projection. Exact duplicate names are
removed ordinally without case folding.

### Canonical output and first producer

For `Production` and typed `Service:Api-Key`, native inspection yields:

| Name | Scope | Legacy alias | Explicit mapping |
| --- | --- | --- | --- |
| `PRODUCTION__SERVICE__API-KEY` | Scoped | false | false |
| `SERVICE__API-KEY` | Unscoped | false | false |

The composition projection is a distinct legacy table:

| Name | Scope | Hierarchical |
| --- | --- | --- |
| `PRODUCTION_SERVICE_API_KEY` | Scoped | false |
| `SERVICE_API_KEY` | Unscoped | false |
| `PRODUCTION__SERVICE__API__KEY` | Scoped | true |
| `SERVICE__API__KEY` | Unscoped | true |

First-producer metadata survives ordinal duplicate removal. For path `Service`, the
surviving sequence is `PRODUCTION_SERVICE` (scoped flat), `SERVICE` (unscoped flat),
and `PRODUCTION__SERVICE` (scoped hierarchical). The later duplicate `SERVICE` does
not replace the flat candidate's `IsHierarchical == false` flag. Mapping exact key
`Service` to `Root_Config` yields `PRODUCTION__Root_Config` and `Root_Config` with
explicit mapping flags, but does not rewrite `Service:ApiKey` or its composition plan.

Composition projection is deliberately not injective: literal paths made by
`FromSegments("A.B")`, `FromSegments("A-B")`, and `FromSegments("A", "B")` can share
`A_B` and `A__B` candidates, although their typed native identities differ. `A/B` and
`A_B` retain their existing separate rules. Do not use composition names as logical
key identity.

The package fixture demonstrates two caller-owned environment rejection rules. For a
file-only `Feature:Endpoint`, the caller rejects the setting when either planned native
name matches a forbidden-environment inventory. For `Service:ApiKey`, the caller applies
the same rule while a fake approved provider is registered; its `Resolve` is never
called by inspection. Both rules use actual `HashSet<string>` membership. Their mixed
case explicit suffixes make comparer behavior visible: an ordinal set misses a
lowercased environment name, while an ordinal-ignore-case set finds the candidate and
rejects the override. To show ancestor dependence, a separate container maps `Service`
to `Root_Config` only. Inspecting `Service:ApiKey` alone misses forbidden
`PRODUCTION__Root_Config`; adding the `Service` ancestor plan detects it. The fixture
checks these decisions against caller-provided environment-name inventories; it does not
read platform variables or claim source ownership.

| Entry point | Input interpretation | Candidate source |
| --- | --- | --- |
| `InspectNativeKey(environment, AppSurfaceConfigKey)` | Typed identity, irrespective of hidden parser provenance | Canonical encoding or exact explicit mapping; no aliases |
| `InspectNativeKey(environment, string)` | Strict `AppSurfaceConfigKey.Parse` grammar | Native encoding and strict-string compatibility aliases |
| `InspectCompositionPath(environment, AppSurfaceConfigKey)` | Supplied literal path segments | Direct composition projection; native mappings do not apply |

Do not replace an existing policy inventory until its expected names and flags have
been checked against the actual deployment contract. Caller-owned inventories may use
ordinal case-sensitive or case-insensitive matching. Those choices can change whether
a mixed-case explicit suffix is accepted; they never change the inspector's output.
Inspect relevant roots, ancestors, and known member paths explicitly: an exact mapping
for an ancestor does not rewrite a descendant native plan. Direct path projections may
collapse repeated or empty path components according to the existing composition rules.
Input size and number of requested paths are caller-controlled and unbounded by this
API; bound batches in application policy if names come from untrusted or large inputs.
Every result is materialized before return. Calls allocate in proportion to key and
environment spelling length; freezing a container copies its configured mapping set,
so snapshot storage also grows with mapping count. Inspection keeps no per-key cache,
claim history, or retained request state. Prefer startup and explicit policy checks
over unbounded repeated batches. No latency or memory budget is guaranteed; bounded
allocation observations in tests establish correctness under representative inputs,
not a production performance promise.

### Candidate inspection errors {#candidate-errors}

Framework-owned known failures retain their original CLR exception and carry string
`Exception.Data` fields `Code`, `Operation`, `Fix`, `Recovery`, `SideEffects`, and
`HelpUri`, plus `HelpLink`. These fields contain parameter/option names and repair
guidance, never raw keys or secret values. Framework inspector failures have
`SideEffects` set to `none`. A strict parse failure retains `FormatException` and
`config-key-invalid`; null input/DTO guards use `config-candidate-null`; native
unencodable keys use `config-candidate-unencodable`; invalid frozen options use
`config-candidate-options-invalid`.

Use the specific anchors for [null input or DTO rejection](#config-candidate-null),
[native unencodable key](#config-candidate-unencodable),
[invalid frozen options](#config-candidate-options-invalid), and
[strict key grammar](#config-key-invalid); this section is the
[overview](#candidate-errors).

```csharp
try
{
    var plan = inspector.InspectNativeKey("Production", untrustedKey);
    ApplyCallerPolicy(plan.Candidates);
}
catch (FormatException failure) when
    (failure.Data["Code"] as string == "config-key-invalid")
{
    RenderSafeFailure(failure.Message, failure.Data["Code"], failure.Data["Operation"],
        failure.Data["Recovery"], failure.Data["SideEffects"], failure.HelpLink,
        failure.Data["Fix"]); // Render Fix last so it remains the final user action.
}
```

Correct invalid syntax and retry; repair an unencodable key or configure its exact
mapping before construction rather than retrying unchanged input. Invalid mapping
options require repairing the named validation failure and rebuilding the container.
For an unexpected failure, stop and capture package version, operation, and a sanitized
reproduction; do not blindly retry. Its canonical guide anchor is
[`config-candidate-unexpected`](#config-candidate-unexpected). The framework does not
promise enrichment or redaction for arbitrary host callbacks, whose exceptions and
side effects remain host-owned. Candidate plans are immutable copies, but no refresh or
mapping mutation hook is provided after snapshot freeze.

### API reference and upgrade notes

The public XML reference is attached to `EnvironmentCandidateScope`,
`EnvironmentNativeCandidate`, `EnvironmentNativeKeyPlan`,
`EnvironmentCompositionPathCandidate`, `EnvironmentCompositionPathPlan`, and
`IEnvironmentConfigCandidateInspector` in the Config assembly. Plan constructors and
`with { Candidates = list }` copy to immutable arrays and reject null lists/elements;
generated deconstruction returns the immutable public list. Plain `with { }` copies may
share immutable backing values. A new typed overload and new inspector interface are
additive API surface, but callers should use the coordinated Config/Core package
versions established by release evidence. A packed package test proves a locally packed
candidate; it does not prove that a published feed contains that version.

#### Null input and DTO validation {#config-candidate-null}

Supply a non-null argument, key/path, candidate collection, or collection element.
The original `ArgumentNullException` or `ArgumentException` identifies its parameter;
no source lookup is attempted.

#### Unencodable native key {#config-candidate-unencodable}

Correct the key or configure its exact native mapping before first construction. The
existing `InvalidOperationException` is retained; unchanged retries cannot succeed.

#### Invalid frozen options {#config-candidate-options-invalid}

Repair the named `AppSurfaceEnvironmentConfigOptions` failure and rebuild the service
container. The original `OptionsValidationException` and its validation failures are
retained.

#### Strict logical key grammar {#config-key-invalid}

Use nonempty colon-delimited segments without controls or edge whitespace. Strict
parsing preserves its existing `FormatException` and grammar message.

#### Unexpected inspector failure {#config-candidate-unexpected}

Stop rather than retrying blindly. Capture package version, operation, and a sanitized
reproduction for maintainers. Framework-known errors are enriched in place; arbitrary
host callback exceptions and side effects are outside that guarantee.

[LocalSecrets](../Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md) compares
logical keys case-insensitively while retaining exact stored identifiers. A write
using another case updates the existing entry. Canonical writes no longer transform
double underscores or backslashes. Existing aliases require the explicit migration
flow; reads never rename or delete a secret.

[Google Secret Manager](../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md)
retains exact explicit resource names. Conventions claim strict descendants of a
segment prefix and encode the full key: lowercase segments joined with `--`, preceded
by the exact configured secret-id prefix. Segments match
`[A-Za-z0-9_]+(?:-[A-Za-z0-9_]+)*`; dots, unsupported Unicode, edge hyphens, and `--`
inside a segment require explicit mappings. All conventions share one nonempty,
ordinal-exact generated prefix. Final secret IDs must satisfy Google's native
grammar and 255-character limit. Resource identity includes project and version.

## Bounds and diagnostic trust

`ConfigResourceOptions` limits are positive and configurable before host creation.
Defaults are 16 MiB per JSON file, 256 files per environment, 65,536 environment
entries, 64 MiB of UTF-8 environment names plus values, 4,096 retained notice
identities, 256 rendered identifier characters plus a digest, and binding depth 32.
Exceeding a source limit fails closed without publishing a partial snapshot.
When an operation reaches its notice capacity, it retains the bounded first notices
and marks audit evidence incomplete with `config-audit-notice-limit`. Process-level
log suppression remains separately bounded and does not remove notices from an
otherwise complete report.

Google payload caching, when enabled through its TTL option, defaults to a capacity
of 1,024 expiring entries. Its native claims and
audit work have separate validated bounds; the aggregate audit defaults are
`ConfigResourceOptions.AuditTimeout` (30 seconds), `MaxAuditRemoteLookups` (256), and
`MaxAuditConcurrency` (4). An exhausted audit budget is explicitly
incomplete. Caller cancellation stops that waiter without cancelling another
waiter's shared fetch.

Expected diagnostics have Code, Problem, Cause, Fix, Docs, and Retryable. Built-in
templates accept safe identifiers only. Automatic manager logs contain reviewed
codes and bounded identifiers, never arbitrary third-party diagnostic prose,
configuration values, secret payloads, or raw provider exceptions. Logger failures
do not alter resolution. See the [provider-author guide](config-provider-authors.md)
for thread safety, result factories, conformance, and extension responsibilities.

The `ForgeTrust.AppSurface.Config` meter emits `appsurface.config.terminal` and
`appsurface.config.notice` counters. Their only tags are reviewed `code` and
`provider` categories; unknown providers become `custom`. Notice counts occur
before log suppression to expose migration usage. There are no key, environment,
source identifier, or value tags. Listener failures cannot change resolution.

`ConfigAuditTextRenderer` and `ConfigAuditDiffTextRenderer` escape control and format
characters in identifiers and apply `MaxRenderedIdentifierCharacters` before display.
Their parameterless constructors use the defaults; the `IOptions<ConfigResourceOptions>`
constructors copy validated limits when created. Explicit diagnostic prose is escaped
and capped at 4,096 characters without hashing its content. Identifier shortening is
presentation only: structured source records retain exact locators for matching and
comparison. Renderers consume already-redacted display values; they do not make an
untrusted provider's raw value or prose safe to publish.
