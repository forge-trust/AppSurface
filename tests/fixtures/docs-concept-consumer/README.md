# Packed Docs concept consumer

This small external-style consumer is copied to a temporary directory by
[`scripts/verify-docs-concept-consumer.py`](../../../scripts/verify-docs-concept-consumer.py). The verifier restores its
exact Docs package version from the PackageIndex artifact feed, checks that the
transitive ReleaseContracts package resolves from that same feed, builds against
the public Docs API, and checks the packed ReleaseContracts assembly contains
internal `DocConcept*` core types without exporting any of them publicly.

The verifier also compiles the `DocConcept*.cs` core sources in a temporary
`net10.0` project with no PackageReferences. This keeps the existing Markdig
dependency used by release-note composition out of the concept-core compile
graph.

Run the complete proof with:

```bash
python3 scripts/verify-docs-concept-consumer.py --package-version 0.0.0-ci.local
```

The default run uses PackageIndex `verify-packages`, which packs and validates
the repository package set before the isolated consumer restore. To reuse an
already verified artifact directory, pass `--artifacts <directory>` with the
same exact `--package-version`.
