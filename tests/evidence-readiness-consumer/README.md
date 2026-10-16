# Packed neutral readiness consumer

This complete .NET 10 program proves that a plain consumer can restore, compile, and run using only the locally packed `ForgeTrust.AppSurface.Evidence.Readiness` package. Its restore graph contains exactly that one `.nupkg`; the local feed also carries the packages used by the separate typed proof. At runtime the program checks the restored `project.assets.json`, the package cache hash, and the package's `.nuspec`; it fails if the package adds an Aspire, hosting, EvidenceHost, planner, producer, or other package dependency.

The executable uses a controlled `HttpMessageHandler`, not a network service. It verifies a `503` retry followed by exact `200`, then verifies the public attempt-limit diagnostic preserves the final status and attempt count. This is packed API and controlled-transport evidence, not live-network or Aspire-resource evidence.

Prerequisites are the .NET 10 SDK and the prepared repository checkout. The neutral restore uses only the generated local feed; the combined script also needs NuGet.org access for the typed Aspire consumer. Select an exact coordinated published version through the [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for independent use:

```sh
dotnet add package ForgeTrust.AppSurface.Evidence.Readiness --version <selected-exact-version>
```

Run the complete proof from the repository root:

```sh
scripts/verify-evidence-readiness-consumers.sh
```

The script builds and locally packs the current source tree with one unique prerelease version, then restores, builds, and runs this complete program in a fresh consumer directory and isolated NuGet cache. It also runs the typed consumer in [evidence-readiness-typed-consumer](../evidence-readiness-typed-consumer/README.md). No package publication occurs.

The package's independently consumable public probe is [`HttpReadinessProbe.WaitAsync`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#httpreadinessprobewaitasync). Add the exact package version selected by the coordinated release when using it in another project:

```xml
<PackageReference Include="ForgeTrust.AppSurface.Evidence.Readiness" Version="<exact-coordinated-version>" />
```

The proof output reports `packageId`, unique `packageVersion`, the source Git commit, restore/nuspec closure, and proof kind. Human onboarding and execution timing are `unmeasured`; this is not a timing or reliability claim.

Expected final output starts with `READINESS_PACKED_CONSUMER_OK proof=neutral-controlled-transport`, includes `nuspecDependencies=0 retryAttempts=2 attemptLimitAttempts=1`, and identifies the actual version, source commit, and dirty/clean source state. The combined script prints its final `status=PASS` only after the typed proof also completes. These output shapes describe the test contract; the [execution record](../../docs/plans/issue-867-execution-progress.md) links the actual run identities.
