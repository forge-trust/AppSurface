# Upgrade the configuration logical-key contract

The train-1 release introduces case-insensitive, colon-delimited
[logical keys](config-logical-keys.md) across Config, LocalSecrets, and Google Secret
Manager. Upgrade the coordinated package family together. The provider SPI, manager
inheritance, environment snapshot contract, wrapper initialization, and typed audit
surfaces change intentionally before 1.0. Rebuild external providers and wrappers;
there is no V2 adapter. Use the [provider-author guide](config-provider-authors.md)
and [conformance package](../Config/ForgeTrust.AppSurface.Config.Testing/README.md).

## Three release trains

| Train | Application string default | Required operator action |
| --- | --- | --- |
| 1: compatibility | Dot-only input translates once with guidance; colon input is strict | Inventory, upgrade together, replace hierarchy dots, and pin old native identities explicitly |
| 2: strict default | Dots are literal; translation can be opted back into for one train | Finish source and persisted-identity migration before disabling compatibility |
| 3: removal | Strict colon syntax only | Remove obsolete concrete-provider string helpers and historical aliases |

Typed `Parse` and `FromSegments` always use strict syntax. For example,
`AppSurfaceConfigKey.FromSegments("Microsoft.Hosting.Lifetime")` is one literal
segment in every train. In train 1, a string `"Payments.ApiKey"` passed to a manager
means `Payments:ApiKey`. A string `"Logging:LogLevel:Microsoft.Hosting.Lifetime"`
contains a colon and therefore preserves its literal dots.

## Inventory before changing stored identities

Inventory attributed keys, manual audit registrations, environment mappings,
LocalSecrets indexed names, and known Google convention declarations. Inventory
must not read or print secret payloads. Register unknown ad-hoc convention keys
explicitly before upgrading; a provider does not enumerate arbitrary Google secrets
or process variables to discover application keys.

Replace hierarchy dots in attributes, direct string reads, and audit registrations
with colons. Do not mass-replace dots inside literal segments. A declaration using
legacy dots and another using the corresponding canonical colons is a startup
collision. Case-only declarations are also collisions. Keep one spelling even when
the values would be equal.

Run the [source contract proof](../examples/config-key-contract/README.md), provider
conformance, and clean packed-consumer verifier before selecting the candidate
packages. The execution evidence records previous/candidate binary and persisted
source scenarios separately; a source build alone does not prove binary compatibility.

## Environment aliases

For Production and `Payments:ApiKey`, canonical names are
`PRODUCTION__PAYMENTS__APIKEY` and `PAYMENTS__APIKEY`. Train 1 considers historical
aliases only when application input provenance permits them:

| Origin | Additional historical candidates |
| --- | --- |
| Typed key | None |
| Strict string `Payments:ApiKey` | `PRODUCTION_PAYMENTS:APIKEY`, `PRODUCTION__PAYMENTS:APIKEY`, `PAYMENTS:APIKEY` |
| Translated string `Payments.ApiKey` | `PRODUCTION_PAYMENTS_APIKEY`, `PAYMENTS_APIKEY` |

Aliases identify one already-parsed logical key, not multiple logical lookup paths.
Canonical plus legacy in one scope, or two distinct legacy aliases in one scope,
is terminal `config-key-collision`, even if values match. One legacy alias resolves
with `config-key-legacy-provider-alias`. Scoped and unscoped layers validate
independently before scoped precedence applies. An invalid present alias cannot
fall through to a parseable alias.

An explicit `MapKey` suffix replaces canonical and legacy conventions entirely.
Use mappings for underscore-boundary, environment-prefix, or unsupported-character
cases described in the [environment reference](config-logical-keys.md#provider-encodings).
Preserve exact expected casing. AppSurface diagnoses a single case-only native
spelling on both Unix and Windows.

## LocalSecrets journaled migration

Use [LocalSecrets doctor](../Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md)
to select an exact stored source identifier and a proposed logical destination.
The command includes its full namespace context:

```bash
appsurface secrets migrate-key \
  --app MyApp \
  --environment Development \
  --prefix Demo \
  --store-file /absolute/path/to/isolated-store.json \
  --from-stored-key 'appsurface:MyApp:Development:Demo:Payments.ApiKey' \
  --to 'Payments:ApiKey'
```

Supply `--prefix` and `--store-file` only when selected for that namespace.
`--from-stored-key` is an exact case-sensitive backend selector and bypasses logical
parsing. `--to` is strict: `Microsoft.Hosting.Lifetime` is one literal segment;
`Logging:LogLevel:Microsoft.Hosting.Lifetime` is three. Inspect the printed destination
segments and storage identity in the default preview, which asks the selected store
for its exact destination encoding without reading or changing values. This includes
the macOS v2 identifier when that backend is selected. Repeat the same command with `--apply` to confirm the migration. The command
then reports its migration ID and durable state; values are never printed. An invalid
destination returns `config-key-invalid` before store access.
Displayed identifiers escape controls and are bounded at 256 characters plus a stable
digest. When any argument needs display transformation, the CLI omits executable
command output and directs you to repeat the original invocation with `--apply`;
it never reuses a truncated display value for migration.

All package-owned mutations share one exclusive maintenance lease. Under that
lease the durable migration advances through Prepared, DestinationWritten,
DestinationVerified, SourceDeletePending, and Complete. The journal stores only
operation ID, identities, and state. Index publication is journaled before deletion.
The store rereads source and destination and compares their UTF-8 bytes with a
fixed-time comparison before authorizing deletion. It never persists a value hash.

| Condition | Recovery |
| --- | --- |
| Destination exists with a different value | Retain both; resolve the conflict through an authorized store operation |
| Crash after destination write | Retry the same migration under the lease; roll forward |
| Delete fails | SourceDeletePending remains durable; retry rereads and compares before deletion |
| Source absent after durable verification | Complete without rewriting the destination |
| Source absent before durable verification | Stop as unrecoverable; do not alter the destination |
| Backend cannot guarantee lease, durable journal, confirmed operations, or index publication | Return `local-secret-migration-unsupported`; do not delete |

There is no rollback that deletes the destination after it has been written. A
case-only write updates an existing logical entry without renaming its stored
spelling. Canonical writes preserve `__` and backslashes literally; historical
transformations apply only to the migration aliases allowed by request origin.

## Google convention migration

The old convention was lossy and used a suffix normalization. The new convention
encodes the full key with injective segment boundaries: `Payments:ApiKey` becomes
`payments--apikey`, before the configured exact prefix is added. It does not probe
old generated IDs as a hidden fallback.

Inventory every known convention declaration without accessing a secret. Where the
old and new generated IDs differ, use the inventory-generated explicit mapping to
pin the logical key to the existing exact ID or resource. Keep the explicit mapping
indefinitely or move the remote secret separately through your normal authorized
operations. The Config upgrade does not rename, delete, enumerate, or change IAM for
Google secrets. See the [Google provider reference](../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md).

Project, secret ID, and version all participate in exact native resource identity.
Distinct logical keys cannot claim one full version resource. Known declarations
are validated at startup; ad-hoc claims are atomic and bounded before network access.

## Compatibility and downgrade boundaries

| Consumer/packages/source | Contract |
| --- | --- |
| Candidate application and coordinated candidate packages | Supported; run conformance and source proof |
| Existing application source using manager string overload | Source-compatible; train-1 migration diagnostics apply |
| Previous provider or wrapper binaries with candidate SPI | Unsupported; rebuild, with package-version failure before use |
| Mixed previous/candidate Config, LocalSecrets, or Google packages | Unsupported; install one coordinated set |
| Candidate with old environment/LocalSecrets identities | Supported only through the documented train-1 origin aliases; ambiguous pairs fail closed |
| Candidate with old Google convention IDs | Requires inventory-generated explicit mappings |
| Downgrade after read-only inventory | No stored identities changed |
| Downgrade after destination write with source retained | Previous consumers may still use their source; verify the exact fixture before deployment |
| Downgrade after source deletion | Previous consumers need an explicit compatible mapping or authorized recovery; do not assume old naming works |

Do not advance to a removal train until inventory is clean, package compatibility
fixtures pass, and the previous train's diagnostics show no remaining alias use for
the deployment. Record operating system, SDK, coordinated package version, cache
state, and source/consumer timings in release evidence. These are local diagnostics
and test artifacts; no configuration usage telemetry is sent automatically.

## Diagnostic repairs

`config-key-invalid`: correct a segment or use valid literal segments.
`config-key-collision`: retain one spelling/identifier in the named collision domain.
`config-key-unrepresentable`: configure an explicit native mapping or rename the key.
`config-key-prefix-overlap`: remove or narrow the overlapping convention.
`config-key-environment-name-case`: rename to the exact expected native spelling.
`config-environment-snapshot-failed`: repair capture; lower-provider fallback is
suppressed because override availability is unknown. Resource-limit diagnostics
require reducing the source or raising the documented positive option. A logger
failure never changes resolution behavior.
