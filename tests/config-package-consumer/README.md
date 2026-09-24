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

The [verifier](../../scripts/verify_config_package_consumer.py) packs Core, Config,
and Config.Testing from the current source, then generates a fresh console project
outside the repository. It copies [Program.cs](Program.cs) and the
[non-secret JSON fixture](appsettings.json) into that project. Separate NuGet package,
HTTP, and CLI-home directories prevent an existing project reference or build cache
from supplying an AppSurface assembly. The verifier inspects restored assets and
rejects project dependencies.

The consumer runs twice. The file run must resolve `Payments:ApiKey` from the real
file provider. The override run must resolve the same logical identity from
`PAYMENTS__APIKEY`, while normal `IConfiguration` access retains its expected
behavior. The consumer's external provider exercises the public request/result and
conformance APIs. It invokes the shared `Unrepresentable` case with a dotted
counterexample for its native codec, then `MissingToLowerFallback` with an empty
higher provider and a populated lower provider. Fixture counters verify that the
terminal case skips the lower source and the missing case reaches it on both
original and lowercase reads. Both consumer runs must report these two cases passing;
merely loading the conformance case list is insufficient. All values are fixed demonstration markers; no credential store
or remote secret service is used.

| Option | Default and behavior |
| --- | --- |
| `--package-version VERSION` | `0.1.0-config-contract.local`; applies to every candidate package |
| `--configuration NAME` | `Release`; used for candidate packing and the generated consumer |
| `--work-directory PATH` | A new temporary directory; its `consumer` child must not already exist |
| `--artifacts PATH` | Use an existing package directory instead of packing source; requires all three matching package files |

To verify an already packed candidate, pass its exact version and use a fresh work
directory:

```bash
./scripts/verify-config-package-consumer.sh \
  --artifacts /absolute/path/to/candidate-packages \
  --package-version 0.1.0-config-contract.local \
  --work-directory /absolute/path/to/new-consumer-run
```

Every subprocess has a five-minute deadline. Logs and per-stage timing remain in
the work directory, together with `evidence.json`, which records the SDK, OS,
version, isolated cache state, timings, and pass or incomplete result. A failing
stage exits nonzero and preserves its log. On macOS the verifier enables polling
file watching for its subprocesses to avoid sandboxed native-watcher stalls; it
does not change application configuration registration.

The [.NET build workflow](../../.github/workflows/build.yml) invokes this proof on
Linux and Windows. The separate [previous-package compatibility fixture](../config-key-compatibility/README.md)
checks coordinated binary-break diagnostics and mixed package families. Neither
verifier publishes packages or changes a remote repository.
