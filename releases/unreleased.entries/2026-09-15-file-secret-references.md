<!-- appsurface:unreleased-entry section="included" -->
### File declared scalar secret references

Configuration models can use `Secret<T>` to declare a sensitive scalar destination. Its checked-in JSON descriptor
selects a resource, version, provider, and static activation state while keeping the payload outside the file. Start with
the [reference guide](../../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md) and
[network-free executable example](../../examples/file-secret-references/README.md).

The [Google provider](../../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md) resolves enabled references;
[LocalSecrets](../../Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md) can supply a sensitive whole-root base.
Disabled references still validate locally. Exact environment values can supply or rescue a destination, and a valid
direct environment root bypasses file and provider resolution. Effective audit performs the same reads as runtime and
shows only opaque activation, availability, and source state.

This behavior is an explicit opt-in. Existing models keep their legacy resolution path. File loading now rejects duplicate
JSON members, including case-only duplicates, and skips those invalid files with a safe diagnostic for legacy roots.
Third-party base providers for
opted-in roots must expose raw composition support or prove that they do not claim the root. Migrate a whole root and
remove overlapping code mappings in the same change; descriptor layers replace one another atomically. See the
[migration and rollback guidance](../../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md#migrate-map-secret).

Stable publication remains gated on the separately scoped real Skoolit root migration, measured glue deletion,
Google verification without environment rescue, and previous-artifact rollback rehearsal. The deterministic repository
and packed-consumer proofs support this pull request; they do not replace that adopter rehearsal.
