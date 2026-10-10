# Config package consumer proof

This fixture verifies that an application outside the repository can restore the
coordinated Config packages and use their public APIs. Unlike the
[source provider proof](../../examples/config-key-contract/README.md), it uses only
NuGet package references. It also runs a provider implemented by the consumer
through the [Config.Testing harness](../../Config/ForgeTrust.AppSurface.Config.Testing/README.md).

From the repository root, with Python 3 and the .NET 10 SDK installed:

```bash
./scripts/verify-config-package-consumer.sh
```

The [verifier](../../scripts/verify_config_package_consumer.py) packs
`ForgeTrust.AppSurface.Core`, `ForgeTrust.AppSurface.Config`, and
`ForgeTrust.AppSurface.Config.Testing` from the current source at one candidate version. It restores
the separate [Domain fixture project](fixtures/domain/DomainFixture.csproj) against
those exact Core and Config packages, then packs it as
`ForgeTrust.AppSurface.Config.PackageConsumer.DomainFixture` at the same version.
The generated consumer references Config, Config.Testing, and that Domain package;
Core is transitive. The verifier checks both restore graphs for the exact candidate
package identities and rejects project references.

It generates a fresh .NET 10 console host outside the repository and copies
[Program.cs](Program.cs), the
[explicit registration helper](ExplicitRegistrationExample.cs),
[worker-root wrappers](WorkerRootConfigs.cs), the
[staged worker proof](WorkerStageProof.cs), and the
[non-secret JSON fixture](appsettings.json). Separate NuGet package, HTTP, and
CLI-home directories prevent an existing project reference or build cache from
supplying an AppSurface assembly. The isolated consumer is restored and built from
these packed packages; the verifier then executes the compiled helper in baseline
and candidate modes.

### Pinned Web and worker selection

The [pinned inventory](pinned-inventory.md) records the original two Web methods
and all six worker selections, including their discovery membership, conditions
and provider stages. Only catalog/parity live in packed Domain; DurableWorker,
AlphaSourceOnly, ForwardingExtraction and SharedDataProtection are discovered in
the worker entry assembly. The injected Payments example is separate.

The worker proof clones its service collections and builds distinct first and
second providers. Five scenarios cover another lane, disabled EvidenceSupport,
disabled admission, standby and active EvidenceSupport. Catalog/parity/DurableWorker
resolve in the first provider; AlphaSourceOnly resolves there only when the lane
and both admission flags permit it. Extraction/DataProtection resolve in a second
provider only for active EvidenceSupport. Explicit helpers on the four already
discovered wrappers remain idempotent.

Registration and startup activate no wrappers or provider reads. Runtime phase
deltas assert each selected wrapper's constructor, typed-key manager call from
`Init`, and provider read exactly once. Each provider's audit separately constructs
six inspection wrappers and reads their declared keys, with zero `Init` calls.
An unselected `Config<Secret<string>>` wrapper and secret-provider payload-read
tripwire stay untouched. These counters and the full selection inventory are
validated by the verifier and saved in `evidence.json`, without configuration
values. This is equivalent composition evidence; actual downstream adoption
requires its own downstream run.

### Canonical explicit Domain example

The focused [Domain wrapper source](fixtures/domain/DomainConfigs.cs) declares
`PaymentsEndpointConfig` at `Payments:Endpoint`, with
`ConfigPackageConsumer.Domain.PaymentsEndpointValue` as its value type and an
ordinary constructor-injected `IPaymentsEndpointFixtureDependency`. The host uses
the existing `AppSurfaceConfigModule` and `IConfigManager`, Core's
`IEnvironmentProvider` via `DefaultEnvironmentProvider`, and ordinary `IConfigProvider`
services. The proof's `PackageProofProvider` returns the non-secret marker
`https://payments.example.test`; it is asserted but never printed.

1. Use an existing `.NET 10` Generic Host and one coordinated candidate version.
   The default isolated-proof identity is `0.1.0-config-contract.local` for
   `ForgeTrust.AppSurface.Core`, `ForgeTrust.AppSurface.Config`,
   `ForgeTrust.AppSurface.Config.Testing`, and the packed Domain fixture. It is
   local test metadata, not a release version.
2. Put the attributed wrapper in a separate Domain assembly and register its
   constructor dependency. The focused helper's usings are in the linked source;
   the host integration shows the Core, Core.Defaults, Config, Configuration, DI,
   and Hosting usings and normal AppSurface service composition.
3. Before `Build()`, call
   `services.AddAppSurfaceConfig<PaymentsEndpointConfig>()`. Resolve it from the
   built provider and verify exactly one resolved audit entry for
   `Payments:Endpoint` with declared type
   `ConfigPackageConsumer.Domain.PaymentsEndpointValue`.

The safe candidate marker is `Explicit Domain registration: PASS`. The baseline
manual `Init` path reports `Baseline manual Domain selection: Missing declaration`,
showing that runtime selection alone does not add the attributed audit identity.
Neither path prints the endpoint value. The verifier marks the package proof passed
only after building this exact helper with the packed Domain package and completing
its baseline and candidate assertions; that result is compiled package-consumer
evidence rather than a source-only check.

The consumer also keeps the pre-existing file and environment override cases for
`Payments:ApiKey`: the file run resolves the value from the real file provider,
while the override run resolves the same logical identity from `PAYMENTS__APIKEY`
and preserves normal `IConfiguration` behavior. Its external provider exercises
the public request/result and conformance APIs. It invokes `Unrepresentable` with a
dotted counterexample for its native codec, then `MissingToLowerFallback` with an
empty higher provider and a populated lower provider. Fixture counters verify that
the terminal case skips the lower source and the missing case reaches it on both
original and lowercase reads. Both candidate runs must report these cases passing;
merely loading the conformance case list is insufficient. All values are fixed
demonstration markers; no credential store or remote secret service is used.

| Option | Default and behavior |
| --- | --- |
| `--package-version VERSION` | `0.1.0-config-contract.local`; applies to every candidate package |
| `--configuration NAME` | `Release`; used for candidate packing and the generated consumer |
| `--work-directory PATH` | A fresh directory; verifier-owned `consumer`, `domain-project`, `domain-feed`, `nuget-packages`, `http-cache`, and `dotnet-home` paths must not exist (also `packages` when packing sources) |
| `--artifacts PATH` | Use an existing package directory instead of packing source; requires all three matching package files |

To verify an already packed candidate, pass its exact version and use a fresh work
directory:

```bash
./scripts/verify-config-package-consumer.sh \
  --artifacts /absolute/path/to/candidate-packages \
  --package-version 0.1.0-config-contract.local \
  --work-directory /absolute/path/to/new-consumer-run
```

Every subprocess has a five-minute deadline. Logs and per-stage verifier timings
remain in the work directory, together with `evidence.json`, which records the SDK,
OS, baseline revision references, actual checkout/input and package hashes,
coordinated package graphs, version, isolated cache state, stage timings, and
pass or failure result with the failed stage. These timings measure verifier subprocesses, not human
reading or onboarding time. A failing
stage exits nonzero and preserves its log. On macOS the verifier enables polling
file watching for its subprocesses to avoid sandboxed native-watcher stalls; it
does not change application configuration registration.

### Optional human onboarding check (estimated 2–5 minutes)

This is a later, lightweight docs assessment, not a measured result or a release
gate. Record the docs revision, exact coordinated candidate package version, and
the existing-host prerequisites before starting. Time the human work of opening
and reading the example, adding its wrapper/dependency registration, restoring the
consumer, and running it until you can explain both the DI-resolved wrapper and its
attributed audit identity. The expected safe result is the fixture's key and pass
markers, with no resolved configuration value printed.

Record blockers and human elapsed time in assessment notes alongside `evidence.json`;
keep the verifier's subprocess timings in that file as a separate measure. Do not
add telemetry. If the route is unclear, use
the existing [docs and developer-experience feedback form](../../CONTRIBUTING.md#feedback-path)
and name the page and step that blocked you. This assessment remains optional and
does not add a recurring task or timed release check.

The [.NET build workflow](../../.github/workflows/build.yml) invokes this proof on
Linux and Windows. The separate [previous-package compatibility fixture](../config-key-compatibility/README.md)
checks coordinated binary-break diagnostics and mixed package families. Neither
verifier publishes packages or changes a remote repository.
