# Historical compatibility consumers

The non-packable `V2WorkHarness` and `LegacyCoreProviderAbiConsumer` projects compile against the exact published `0.2.0-preview.8` package graph. The [PostgreSQL release verifier](../verify-postgresql.sh) uses the former to prove old-package behavior on schema 11 and refusal of schema 12. The [packed-consumer verifier](../verify-packed-consumers.sh) uses the latter to load an old compiled consumer against current packages.

The local [central package file](Directory.Packages.props) preserves the third-party pins recorded by the historical lock files. It intentionally does not import the repository's current package pins: upgrades to the product graph must not silently alter the old runtime used by these proofs. Central transitive pinning, locked restore, package auditing and the exact first-party version guards remain enabled. Both consumers keep their original lock files and content hashes.

When a historical package version is deliberately replaced, review its project guard, complete locked graph and compatibility assertions together. Do not regenerate the historical locks merely to accommodate a current dependency upgrade. Keep this boundary limited to these compatibility projects; current source and generated consumers use their own coordinated pins.
