# AppSurface Docs Tree-sitter candidate record

Issue: https://github.com/forge-trust/AppSurface/issues/771

This record preserves the dependency evidence for the approved [Python docstring harvesting spike](../../../../docs/designs/python-docstring-harvesting-spike.md).
It is a bounded product decision, not a claim that every bundled grammar is a supported AppSurface Docs language.

## Decision

Accept `TreeSitter.DotNet` `1.3.0` for the bounded Python harvesting spike.

The original review treated the 50.93 MiB compressed archive as a hard 5 MiB rejection. That preference was reversed:
the package's all-grammar, multi-RID payload is a visible trade-off, but it supports the broader polyglot product
direction and is not a hard stop. AppSurface Docs uses only its Python grammar in this slice. A future per-RID or
smaller language-focused distribution would be an optimization, not a prerequisite.

The accompanying [machine-readable evidence](./tree-sitter-dotnet-1.3.0-proof.json) records the archive inspection
with no automated rejection reasons. The static inspection tool still has a 64 MiB archive-read safety cap; that is an
inspection resource limit, not a product package-size budget.

## Exact artifact measured

| Field | Value |
| --- | --- |
| Package | `TreeSitter.DotNet` `1.3.0` |
| Download source | `https://api.nuget.org/v3-flatcontainer/treesitter.dotnet/1.3.0/treesitter.dotnet.1.3.0.nupkg` |
| Measured on | 2026-08-23 |
| SHA-256 | `9d64f6a3d084d2c1c3cbc61a14e7d93e4c4d92604bbf526236d85b8e0b23ccbf` |
| Compressed archive bytes | 53,401,399 |
| Compressed archive size | 50.93 MiB |
| Archive entries | 266 |
| Total uncompressed bytes | 617,084,456 |
| Product decision | Accepted for the bounded multi-language spike; payload size is recorded trade-off evidence |

The archive size, rather than the NuGet gallery's rounded display, is the decision measurement. The SHA-256 identifies
the exact bytes that produced this inventory.

## Native payload inventory

The package advertises and the archive contains native libraries for these RIDs:

| Runtime identifier | Native files |
| --- | ---: |
| `linux-arm` | 28 |
| `linux-arm64` | 28 |
| `linux-x64` | 28 |
| `linux-x86` | 28 |
| `osx-arm64` | 26 |
| `osx-x64` | 26 |
| `win-arm64` | 31 |
| `win-x64` | 31 |
| `win-x86` | 31 |

The current spike uses the Python grammar and has a real native parser proof on macOS ARM64. It does not claim that a
native load has been observed on every advertised RID. Verify package-consumer publishing and loading on Windows,
Linux, and macOS before broad release adoption or a parser-package upgrade.

## Provenance and notices

The artifact's single `.nuspec` declares MIT licensing and source provenance at
`https://github.com/mariusgreuel/tree-sitter-dotnet-bindings.git` commit
`8cae484bc033dac6e492ed15166877f3d784850f`. It does not include a `LICENSE`, `NOTICE`, `COPYING`, or third-party
notice file inside the nupkg. AppSurface Docs records the declared package license in
[its third-party notices](../../../../Web/ForgeTrust.AppSurface.Docs/THIRD-PARTY-NOTICES.md).

This record is not a substitute for a release/legal review of changed upstream assets. Re-run the static archive
inspection and update the notice review whenever the pinned version changes, especially because this package bundles
multiple grammar artifacts.

## Static-inspection boundary

The `inspect-python-parser-candidate` command is deliberately static: it hashes and inventories a supplied `.nupkg`
but never restores, builds, loads, or executes that archive. This is useful dependency provenance evidence; it is not
the runtime proof. The product spike separately exercises the pinned dependency from the Docs test fixture with native
load failures converted to an actionable harvest diagnostic.

## Reproduction

```bash
curl -fsSL --output /tmp/treesitter-dotnet-1.3.0.nupkg \
  https://api.nuget.org/v3-flatcontainer/treesitter.dotnet/1.3.0/treesitter.dotnet.1.3.0.nupkg
shasum -a 256 /tmp/treesitter-dotnet-1.3.0.nupkg
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  inspect-python-parser-candidate \
  --python-parser-package /tmp/treesitter-dotnet-1.3.0.nupkg \
  --python-parser-proof-report artifacts/python-parser-proof.json
```

Run the command from the repository root and choose a new artifact filename. The command writes only below
`artifacts/`, refuses existing targets and symbolic-link paths, and reports static integrity and inventory facts. It
does not change project dependencies or execute the supplied package.
