<!-- appsurface:unreleased-entry section="included" -->

### Packaged coverage semantic proof

- Package maintainers can now use [`verify-packages`](../../packages/README.md#issue-674-packaged-coverage-proof) to prove that the packed CLI selected the intended `Smoke.Tests` coverage report, observed a covered `Smoke.Calculator.Sign` branch before merge, and retained those facts after merge. The proof accepts only regular manifests up to 16 KiB and emits one bounded, public-safe `coverage-cli-consumer-proof.evidence.json` companion next to its private diagnostic report, while the [CLI coverage guide](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-proof-levels-and-driver-boundary) distinguishes this packaged semantic guarantee from local readiness and the MSBuild compatibility path.
