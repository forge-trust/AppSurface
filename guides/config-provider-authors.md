# Author an AppSurface configuration provider

Implement one typed provider contract and ship it with the coordinated AppSurface
package family. The [logical-key reference](config-logical-keys.md) defines grammar,
collision domains, precedence, and native representability. The
[upgrade guide](config-key-migration.md) covers the intentional pre-1.0 source and
binary break.

```csharp
public sealed class ExampleProvider : IConfigProvider
{
    public int Priority => 10;
    public string Name => nameof(ExampleProvider);

    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        if (!request.Key.Equals(AppSurfaceConfigKey.Parse("Payments:ApiKey")))
            return ConfigProviderValueResult<T>.Missing();

        // Replace this fixture marker with a validated native-source read.
        if (typeof(T) == typeof(string))
            return ConfigProviderValueResult<T>.Found((T)(object)"demo-marker");

        return ConfigProviderValueResult<T>.Terminal(new ConfigProviderTerminalDiagnostic(
            "example-type-unsupported",
            "The requested type is unsupported.",
            "This example provides a string marker only.",
            "Request a string or implement the native conversion.",
            "https://appsurface.dev/guides/config-provider-authors",
            retryable: false));
    }
}
```

Register `IConfigProvider` through the normal service collection. The manager supplies
`ConfigProviderRequest`; external providers do not construct it. Consume
`request.Environment` and `request.Key`. Use key equality or
`IsSameOrDescendantOf` for identity and prefix matching. Keep native resource names
as exact provider-owned strings. Never reinterpret dots, hyphens, underscores, or
slashes as logical hierarchy.

## Result invariants

`Missing()` carries no value, diagnostic, or notices and permits fallback.
`Found(value, notices)` requires a non-null value and accepts zero or false as a real
found value. Notice arrays and members must be non-null; arrays are defensively
copied. `Terminal(diagnostic)` requires a diagnostic and carries no value or notices.
Each call returns one immutable result. Do not maintain a follow-up terminal
diagnostic cache; concurrent callers must never observe another request's failure.

`ConfigProviderNotice` requires code, problem, cause, fix, docs, and retryable fields.
Use it for successful compatibility resolution. All prose must be value-safe. The
manager treats external prose as untrusted and logs only approved codes and bounded
identifiers. Explicit audit output escapes and bounds external text. Do not put a
payload, credential, provider exception message, or value-derived fingerprint into
any diagnostic, cache key, source locator, or metric label.

Terminal suppresses every lower-priority provider. It may be rescued by a successful
transactional environment-child patch; a partially valid patch is never successful.
Return Missing only when absence is established. Denied access, malformed data,
collisions, unrepresentability, and uncertain source availability are terminal.

## Source projection and concurrency

Retain every raw source occurrence before case-insensitive dictionary insertion.
One file, mapping table, environment scope, or LocalSecrets namespace snapshot is
one collision layer. Duplicate spellings, case variants, and distinct aliases inside
that layer fail closed, even when values are equal. Declare layer order explicitly:
only exact-spelling overrides across ordered layers are intentional. Case changes
across layers remain terminal. Do not infer order from enumeration or arrival time.

Native encoding must be injective for accepted logical identities. Validate complete
identifiers, including environment scope, project, version, or configured prefix.
Offer explicit native mappings for valid keys outside a convention's grammar. Build
known-key forward and reverse indexes from the finalized declaration set. Atomically
claim ad-hoc resources before retrieval, and bound retained claims.

Providers are normally singletons and must support concurrent requests. Capture
local mutable sources once per operation. Cache only immutable data or copy-owned
buffers, keyed by exact native resource identity. Coalesce concurrent remote misses
with a side-effect-free holder factory; a concurrent dictionary factory may run more
than once. Evict failed fetches, expire successful entries, and bound cache size.
Caller cancellation must not cancel work still needed by other callers.

For richer provenance, implement request-based `IConfigProviderAuditDiagnostics`.
`ConfigProviderAuditResolution.LogicalKey` and
`ConfigProviderAuditDiscoveredKey.Key` carry typed identities. Construct them
from the request or an already-parsed source key, preserving input provenance;
provider internals must not invoke the application string compatibility parser.
The discovered record constructor takes the typed key, raw value, value kind,
source records, and diagnostics. Raw values remain input to redaction, never safe
output. Source and diagnostic collections must be non-null. Implement
`IConfigProviderAuditKeyEnumerator` only when enumeration is safe without broadening
access to unrelated secrets. Public report DTO roots remain colon renderings.
Report exact safe source identifiers and original spelling. Never use dotted/bracketed
display paths for matching, grouping, ancestry, or redaction.

Audit resolves the effective value with the same typed request and environment snapshot as its other reads.
For explicit provenance, it also inspects eligible lower providers, preserving all successful source records while
retaining the first winner. A terminal result stops that lower-provider walk. A shadowed failure can add a diagnostic
but cannot replace an already resolved winner. Environment patch failure is terminal and publishes no partial value;
an applied transactional patch may rescue a missing or terminal base. Cancellation propagates instead of becoming
an ordinary missing result. Audit notice-limit exhaustion produces an explicit incomplete-report error.

## Verify and migrate

Use the test-framework-neutral [ForgeTrust.AppSurface.Config.Testing](../Config/ForgeTrust.AppSurface.Config.Testing/README.md)
package, which targets `.NET 10 (net10.0)` and the coordinated AppSurface Config package family.
It can be invoked from any test framework. Supply isolated native sources through
`IConfigProviderContractHarness`, then run `ConfigProviderContractAssert.All`.
Conformance includes identity, separator distinctions, collisions, native encoding,
prefixes, missing and terminal behavior, manager precedence, and concurrent reads.
The fourteen canonical cases distinguish absence everywhere (`Missing`) from a missing
provider with a populated lower source (`MissingToLowerFallback`). For `Unrepresentable`,
use the session's optional `unrepresentableKey` when your native codec accepts the
default `A_:B`; Google's counterexample can be `Payments:Unsupported.Dot` under a
valid `Payments` convention. This override is rejected in other scenarios. Follow the
[fixture arrangements](../Config/ForgeTrust.AppSurface.Config.Testing/README.md) and
verify that terminal failures never read the populated lower source.
Add provider-specific option, resource-bound, failure, cancellation, and provenance
tests. Test public APIs or deliberate internal seams; do not inspect private state
with reflection.

Replace old `GetValue<T>(environment, string)` implementations with `Resolve<T>`.
Remove `IConfigProviderTerminalDiagnosticProvider`. Rebuild every provider and
wrapper against the candidate package set, including the new environment snapshot
member. No V2 adapter or dual provider SPI is provided. Existing applications retain
`IConfigManager.GetValue<T>(…, string)` but must follow the migration guide for
persisted native identifiers and dot-only inputs.
