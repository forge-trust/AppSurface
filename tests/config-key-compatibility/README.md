# Config logical-key compatibility fixture

This fixture preserves a consumer compiled against commit `540b59f7e2a3e1cb072645d0d7bff519dcf55589`. It is an executable with no test framework and no direct third-party package references. Its only direct package references are the four historical AppSurface packages; their existing transitive dependencies are restored from NuGet.

Run the reproducible baseline verifier from the repository root:

```text
bash scripts/verify-config-compatibility.sh
```

The verifier creates or reuses the detached checkout at `/tmp/appsurface-config-compatibility/archive`, rebuilds these packages into `/tmp/appsurface-config-compatibility/previous-packages`, and builds each run into a fresh `run.*` directory below `/tmp/appsurface-config-compatibility/previous-fixtures`:

- `ForgeTrust.AppSurface.Core.0.1.0-previous.540b59f7.nupkg`
- `ForgeTrust.AppSurface.Config.0.1.0-previous.540b59f7.nupkg`
- `ForgeTrust.AppSurface.Config.LocalSecrets.0.1.0-previous.540b59f7.nupkg`
- `ForgeTrust.AppSurface.Config.GoogleSecretManager.0.1.0-previous.540b59f7.nupkg`

The old Config project at that commit omitted framework package references that its source used. The verifier copies [the build-only overlay](old-build/Directory.Build.targets) into the isolated `/tmp` checkout before packing. It does not modify the archived source commit, the current branch, or production source. The old package build can emit its historical missing README warning and a Google logging assembly conflict warning; the package and fixture build remain successful.

The fixture source is [PreviousConsumer.csproj](previous-consumer/PreviousConsumer.csproj) and [Program.cs](previous-consumer/Program.cs). It proves the old public contracts by compiling and running all of these calls:

- `IConfigProvider.GetValue<T>(string environment, string key)` and concrete provider string helpers;
- `IConfigManager : IConfigProvider` inheritance;
- the old three-member `IEnvironmentProvider` contract;
- `IConfig.Init` with a string logical key;
- `IConfigProviderAuditDiagnostics`, `ConfigProviderAuditResolution`, and `ConfigProviderAuditDiscoveredKey`;
- LocalSecrets identity normalization, in-memory persistence, and provider lookup;
- Google Secret Manager mapping, fake client lookup, and persisted resource identity.

The fake LocalSecrets and Google clients use sentinel values only. They never access a credential store, network service, or real secret.

The verifier honors `NUGET_HTTP_CACHE_PATH` and defaults it to `/tmp/config-compat-http`; `NUGET_PACKAGES` is isolated inside each fresh run directory so repacking a candidate under the same fixture version cannot reuse stale package binaries.

Plugin and candidate restore lock files are generated inside the fresh run directory. Their local package content hashes describe that run's rebuilt artifacts; a checked-in lock from an earlier pack is not reused. The subsequent builds use `--no-restore`, preserving the exact restored package set for execution.

## Bootstrap guard strategy

The verifier's candidate mode builds an executable bootstrap fixture against the candidate package family. After the baseline succeeds, run:

```text
bash scripts/verify-config-compatibility.sh --candidate-packages /absolute/path/to/candidate-packages
```

It requires exactly one candidate nupkg for each of the four packages above and requires their filenames to carry one identical version. The candidate fixture is [CandidateBootstrap](candidate-bootstrap/Program.cs); the separate [previous provider plugin](previous-provider-plugin/LegacyProviderPlugin.cs) is compiled against the historical packages, not the candidate contracts.

The candidate proof runs each scenario in a fresh process:

- **Metadata guard:** load the previous plugin into a collectible `AssemblyLoadContext`, then call the real public `ConfigPackageCompatibility.ValidateAssemblies` before type discovery or activation. Rejection must be the six-field `config-package-version-mismatch` diagnostic from `AppSurfacePackageCompatibilityException`.
- **String startup:** load the previous plugin, then call the public `AppSurfaceStartup<TRootModule>.RunAsync(string[])`. Its overridden root factory would scan and activate the actual old provider if invoked. The guard must reject with zero root-factory calls.
- **Early dependency preparation:** expose the existing protected `RegisterDependencies` hook from a candidate startup subclass. With the previous plugin loaded, rejection must precede any root dependency callback.
- **Host-builder entry:** call `IAppSurfaceStartup.CreateHostBuilder` with a caller-created context. Rejection must precede dependency and host configuration callbacks.
- **Unguarded control:** deliberately attempt the old plugin's actual type discovery and activation. The fixture reports only the resulting loader exception class; successful activation fails the verifier.
- **Four independent mixed-family metadata checks:** load one old Core, Config, LocalSecrets, or Google assembly into its own collectible context and validate that assembly alone. No old provider plugin is loaded in these rows. Each package must fail its own identity check. These rows prove metadata rejection, not execution of a mixed package graph.

The startup scenarios assert callback counts; they do not fabricate a manager result or expected exception. Compatible startup behavior is covered by the [Core startup tests](../../ForgeTrust.AppSurface.Core.Tests/AppSurfaceStartupTests.cs), including preservation of the custom root instance and unchanged propagation of factory exceptions.

## Implemented boundaries and limits

[`AppSurfacePackageCompatibility`](../../ForgeTrust.AppSurface.Core/AppSurfacePackageCompatibility.cs) validates the supplied assembly identities and their direct assembly references. Core, Config, LocalSecrets, GoogleSecretManager, and Config.Testing must identify assembly contract version **0.2.0.0**. Simple assembly names are matched case-insensitively. This checks assembly metadata; it does not infer a contract from the NuGet package version or inspect API contents.

[`AppSurfaceStartup`](../../ForgeTrust.AppSurface.Core/AppSurfaceStartup.cs) checks the process's already loaded assemblies before the string startup entry invokes `CreateRootModule`, before host-builder configuration, and before the first dependency-registration callback. Custom factories still run once and return the root instance used by the host. [`AppSurfaceConfigModule`](../../Config/ForgeTrust.AppSurface.Config/AppSurfaceConfigModule.cs) also validates each discovery assembly immediately before enumerating its `DefinedTypes`, including direct invocation of its deferred registration callback.

The guard does not load referenced dependencies recursively, intercept future assembly loads, or inspect unfinished dynamic assemblies. A custom root factory or module callback that loads a plugin must call `ValidateAssemblies` on that assembly before scanning or activating its types. Validation at the start of the callback cannot protect a later unguarded load inside it. A caller-created `StartupContext` already contains a constructed root; host-builder validation cannot undo that construction.

Code that fails while the runtime loads an application's entry type, closes a generic type, or initializes a type before reaching AppSurface startup is outside this boundary. A plugin loader needing rejection before those operations must use a compatible bootstrap, load the plugin assembly metadata, validate it, and only then discover or activate types. The guard neither rewrites nor catches unrelated `TypeLoadException`, `ReflectionTypeLoadException`, `FileLoadException`, or metadata I/O failures. Root-factory failures propagate unchanged. The existing `RunAsync(StartupContext)` host lifecycle handler still logs host failures and sets exit code `-100`; callers requiring a typed preflight exception can invoke the metadata guard or host-builder entry directly.

No package-family manifest, API-content attestation, or legacy adapter is implemented. Binaries that falsely retain the current assembly version while carrying an incompatible API cannot be distinguished by this metadata check. Rebuild all affected AppSurface packages and external providers together as described in the [migration guide](../../guides/config-key-migration.md).
