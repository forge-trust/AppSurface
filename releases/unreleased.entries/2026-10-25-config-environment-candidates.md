<!-- appsurface:unreleased-entry section="included" -->

- **Environment candidate inspection:** Added names-only environment candidate plans for typed native keys, strict string
  keys, and direct composition paths. The inspector shares the host's frozen mapping
  snapshot and performs no source reads or value resolution. See the
  [logical-key guide](../../guides/config-logical-keys.md#environment-candidate-inspection)
  and [packed consumer proof](../../tests/config-package-consumer/README.md).
- Candidate inspection does not establish source presence, ownership, or complete
  aggregate policy. Applications retain those decisions and their inventory comparer.
