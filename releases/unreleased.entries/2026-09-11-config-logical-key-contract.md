<!-- appsurface:unreleased-entry section="included" -->
### One logical configuration-key contract

- [Config](../../Config/ForgeTrust.AppSurface.Config/README.md) now uses immutable,
  case-insensitive colon paths across file, environment,
  [LocalSecrets](../../Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md),
  and [Google Secret Manager](../../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md).
  Source spelling and exact native identifiers remain distinct from logical identity.
  Same-layer duplicates, aliases, and case-only spelling changes across ordered
  layers fail closed; exact overrides retain provenance.
- The [provider SPI](../../guides/config-provider-authors.md), environment snapshot,
  wrapper initialization, and typed audit surfaces are a coordinated pre-1.0 source
  and binary break. Upgrade all packages and rebuild external providers together.
  Applications retain `IConfigManager.GetValue<T>`; no V2 adapter is introduced.
  [Config.Testing](../../Config/ForgeTrust.AppSurface.Config.Testing/README.md) ships
  the framework-neutral provider conformance harness in the same release.
- Follow the [three-train migration guide](../../guides/config-key-migration.md).
  Train 1 automatically translates dot-only application strings with diagnostics.
  LocalSecrets moves use an exclusive maintenance lease and durable roll-forward
  journal. Google legacy convention IDs require inventory-generated explicit mappings;
  runtime lookup does not probe historical generated IDs.
- The [no-credential source proof](../../examples/config-key-contract/README.md),
  clean packed consumer, previous/candidate package fixtures, and coverage gates
  verify adoption. No source or migration output displays configuration values.
