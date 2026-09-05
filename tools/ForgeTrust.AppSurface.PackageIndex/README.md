# ForgeTrust.AppSurface.PackageIndex maintainer guide

`ForgeTrust.AppSurface.PackageIndex` owns the curated package chooser, readiness dashboard, package-gate policy, and
the generated `## Release Guidance` region in public package READMEs. Start with the generated
[package chooser](../../packages/README.md) when deciding which package a consumer should install; use this guide when
you are changing the repository's package-story policy rather than one package's authored technical documentation.

## Release guidance

Every managed package README declares one `release_guidance_variant` in
[`packages/package-index.yml`](https://github.com/forge-trust/AppSurface/blob/main/packages/package-index.yml). The value is a finite reader-facing policy choice:

| Variant | Use when | Do not use when |
| --- | --- | --- |
| `default` | The package follows the ordinary coordinated prerelease story. | A package needs an AppHost-only or publication-held statement. |
| `apphost` | The package is primarily an AppHost, development, or test integration surface. | A runtime package merely happens to have an Aspire example. |
| `experimental` | The package has an explicitly experimental or publication-held contract. | A package needs extra product-specific release prose; keep that prose authored outside the region. |

The canonical bodies live in the generator-only
[`release-guidance.template`](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template).
The non-Markdown extension keeps its required unexpanded URL tokens out of the published Docs graph. Each body expands
the package chooser and release-hub links to canonical absolute GitHub URLs. This is deliberate: the package root
`README.md` is included in a NuGet package, but repository-relative targets are not package contents. The package
artifact validator confirms that the marked region and both URLs survive packing.

Do not add a fourth variant for package-specific instructions. Keep those instructions outside the managed marker pair:

```markdown
<!-- appsurface-release-guidance: begin -->
## Release Guidance
<!-- generated content -->
<!-- appsurface-release-guidance: end -->

## Package-specific operations
<!-- authored content -->
```

The renderer changes only the bytes inside the marker pair. It rejects missing, duplicate, reversed, unknown, or
unexpanded markers and tokens rather than guessing, and rejects README paths that cross symbolic links or other
reparse points before it reads or replaces them. For legacy README sections with one `## Release Guidance` heading, the
first `generate` migration inserts the pair before the next H2 or Markdown horizontal rule so trailing footer navigation
remains authored content; after that, retain the markers exactly.

## Change workflow

1. Edit the finite variant or a manifest field in [`packages/package-index.yml`](https://github.com/forge-trust/AppSurface/blob/main/packages/package-index.yml).
2. Keep package-specific adoption, operational, and proof content outside the generated region.
3. Reconcile the checked-in outputs:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate
   ```

   `generate` states its changed and managed README counts. It validates every target before replacement, stages
   same-directory temporary files, and rolls back ordinary replacement failures. Inspect and commit the resulting
   README, chooser, and readiness diffs.

4. Verify without writing:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- gate
   ```

   `verify` compares all generated documents and managed README regions; `gate` validates manifest, template, and
   marker policy without writing files. [Package-gate CI](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-gate.yml) runs both commands.

5. When a change affects package payloads or published documentation, run the existing package artifact proof:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version 0.0.0-ci.local
   ```

   It verifies that the packed `README.md` has exactly one managed marker pair and exactly one canonical chooser and
   release-hub URL inside that region.

### Python parser candidate gate

The `inspect-python-parser-candidate` command is a bounded, static dependency-selection proof. It accepts one local
`.nupkg`, enumerates its native runtime assets, NuGet metadata, and license/notice paths, then writes JSON containing the
archive hash, compressed size, RID inventory, and rejection reasons. It never adds, restores, builds, loads, or executes
candidate package content. A disposable local feed or child process would not sandbox untrusted NuGet build assets,
managed assemblies, analyzers, or native libraries.

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  inspect-python-parser-candidate \
  --python-parser-package /tmp/candidate.nupkg \
  --python-parser-proof-report artifacts/python-parser-proof.json
```

An exit code of `0` means the inspection completed and the report was written; it does **not** mean the candidate was
accepted. Reports must be written below the repository's `artifacts/` directory. Read `rejectionReasons` and
`isEligibleForFurtherReview` from the JSON before taking any dependency action. A candidate that passes static inspection
still needs a separately approved operating-system-level sandbox design before runtime initialization can become evidence.
Report destinations are immutable: choose a new filename for every run, because existing files and symbolic-link paths
are rejected rather than overwritten.
The [source-controlled TreeSitter.DotNet 1.3.0 rejection record](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs.Tests/TestData/PythonParserDecision/README.md)
is the current proof: its 50.93 MiB archive exceeds the approved 5 MiB cap, so it must not be referenced by product code.

## Recovery and release boundary

If `generate` reports a marker, variant, path, or token error, fix the named manifest row or README and rerun the
command. If a write is interrupted, rerun `verify` to identify drift, then rerun `generate`, inspect the diff, and
rerun `verify`. Do not edit the generated region by hand as a substitute for updating its template or manifest field.

Package README reconciliation is intentionally separate from the [release authoring checklist](../../releases/release-authoring-checklist.md).
`eng/release prepare`, including its dry run, must not regenerate package README files; release preparation validates
its own exact artifact set while PackageIndex remains the owner of checked-in package-policy documentation.

### Release-preparation witness

When a release-preparation pull request also changes generated package documentation, the [Release verifier](../ForgeTrust.AppSurface.Release/README.md#verify-prep-diff) invokes this read-only command once:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- release-prep-witness --base-ref <base-tip-commit> --witness /tmp/appsurface-release-prep-witness.json
```

It does not write chooser, readiness, or README files. Instead it records the base tip, exactly one merge base, HEAD, changed semantic sources, and deterministic SHA-256 hashes for the chooser, readiness dashboard, and each managed README body. Only a changed `packages/package-index.yml` or `release-guidance.template` can authorize those surfaces; `packages/README.md.yml` is hand-authored metadata and is never an input. When an authorized input changes a surface relative to the merge base, the release pull request must commit that surface and match the witness digest; a partial PackageIndex regeneration is rejected. The Release verifier rejects unknown, duplicate, unordered, unsafe, or uppercase-hash JSON values, and it requires a README's bytes outside the managed marker body to be identical to the merge-base version. Treat `--witness` as an advanced CI/test seam; use `./eng/release verify-prep-diff --base-ref main` as the normal front door.

## Adding a variant

Adding a variant is a policy change, not a per-package escape hatch. Document why the existing three variants cannot
express the reader-facing posture, add one exact template pair in [`release-guidance.template`](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template), extend
the renderer's finite allowlist and tests, update this table with a non-example, and add the package artifact proof.
Otherwise, use an existing variant and preserve the package-specific explanation outside the marker pair.
