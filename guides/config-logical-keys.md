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
