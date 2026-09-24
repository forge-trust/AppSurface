# AppSurface provider conformance

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

`ForgeTrust.AppSurface.Config.Testing` is a test-framework-neutral conformance harness
for the [logical-key contract](../../guides/config-logical-keys.md). The package targets
`.NET 10 (net10.0)`, references Config, and has no dependency on xUnit, NUnit, MSTest, or another
test runner. It supports the coordinated AppSurface Config package family on that TFM; the
assertions remain callable from any test framework. Ship and consume it with the same coordinated package version as the
[provider SPI](../../guides/config-provider-authors.md); invoke it from the test
framework of your choice on the supported .NET 10 package family.

Implement `IConfigProviderContractHarness.Create` to build fresh, isolated native
sources for each `ConfigProviderContractScenario`. Return a
`ConfigProviderContractSession` containing an application `IConfigManager`, its
environment, two distinct non-secret fixture markers, and cleanup. Run
`ConfigProviderContractAssert.All(harness)` from the test framework of your choice.
Failures identify a case and category without printing either marker, the resolved
value, or a raw provider exception.

The fourteen canonical cases verify identity, literal dots, hyphen distinctions, absence, missing-to-lower fallback, terminal
precedence, collisions, ordered overrides, environment precedence, prefix boundaries,
unrepresentability, train-1 translation, and concurrent repeatability. Fixtures may
use explicit native mappings where a convention cannot represent a valid logical key.
Provider-specific tests must additionally exercise option validation, all native
limits, malformed source data, provenance, cancellation, and storage recovery.

Create a new provider/session for each case. Reusing a native claim index or cache
across scenarios can conceal collisions or make the result depend on test ordering.
For `Missing`, leave the key absent everywhere. For `MissingToLowerFallback`, leave
`Payments:ApiKey` absent in the provider under test and supply `expectedMarker` from
a lower-priority provider. The runner checks the original and lowercase key; fixtures
should also verify lower-provider calls to prove that the intended source was reached.

`Unrepresentable` defaults to the environment codec's `A_:B` counterexample. A provider
whose codec accepts that key must pass a provider-specific `unrepresentableKey` as
the final optional `ConfigProviderContractSession` constructor argument. For example:

```csharp
return new ConfigProviderContractSession(manager, "Production", "expected", "distinct",
    cleanup: DisposeFixture,
    unrepresentableKey: AppSurfaceConfigKey.Parse("Payments:Unsupported.Dot"));
```

That Google fixture configures a valid `Payments` convention with a required native
prefix and leaves the dotted descendant unmapped. `A_:B` is valid in Google's
convention grammar. A file fixture can instead resolve `Payments` above an invalid
JSON property such as `"Invalid:Segment"`. The runner uses the override only for
`Unrepresentable` and verifies the terminal exception's logical key; an override in
another scenario fails. Keep a lower marker present and assert no lower lookup or
network call occurred. Do not manufacture the expected exception in the fixture.
Providers that have no applicable native restriction should document the exclusion
and run applicable cases with `ConfigProviderContractAssert.Case`.

Never point conformance sources at real credentials or existing secret namespaces.
The [source proof](../../examples/config-key-contract/README.md) and
[migration guide](../../guides/config-key-migration.md) describe application adoption.
