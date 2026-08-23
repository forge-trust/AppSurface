# AppSurface Docs Python parser candidate gate

Issue: https://github.com/forge-trust/AppSurface/issues/771

This record resolves the first gate in the approved [Python docstring harvesting spike](../../../../docs/designs/python-docstring-harvesting-spike.md). It is intentionally a candidate rejection, not a shipped Python feature or a general Python-public-API policy.

## Decision

Reject `TreeSitter.DotNet` `1.3.0` for the AppSurface Docs Python-docstring spike.

The candidate's compressed package delta is 53,401,399 bytes (50.93 MiB). The approved cap is 5,242,880 bytes (5 MiB), so the package exceeds the cap by 48,158,519 bytes (45.93 MiB). That hard failure ends this candidate's spike before any AppSurface Docs package reference, Python source kind, harvester, ownership marker, search change, or consumer fixture is added.

The successful local native-load smoke below is useful evidence about the artifact, but it cannot turn an over-budget package into an accepted dependency. No parser fallback is selected by this record. A managed parser or a different package requires a new approved decision.

## Exact artifact measured

| Field | Value |
| --- | --- |
| Package | `TreeSitter.DotNet` `1.3.0` |
| Download source | `https://api.nuget.org/v3-flatcontainer/treesitter.dotnet/1.3.0/treesitter.dotnet.1.3.0.nupkg` |
| Measured on | 2026-08-23 |
| SHA-256 | `9d64f6a3d084d2c1c3cbc61a14e7d93e4c4d92604bbf526236d85b8e0b23ccbf` |
| Compressed archive bytes | 53,401,399 |
| Compressed archive size | 50.93 MiB |
| Approved compressed-delta budget | 5,242,880 bytes (5 MiB) |
| Budget result | **Fail — 10.19× the limit** |
| Archive entries | 266 |
| Total uncompressed bytes | 617,084,456 |

The archive size, rather than the NuGet gallery's rounded display, is the acceptance measurement. The SHA-256 identifies the exact bytes that produced this result.
The [machine-readable proof record](./tree-sitter-dotnet-1.3.0-proof.json) retains the bounded archive inventory, metadata, notice-path inventory, and static-gate result.

## Native payload inventory

The package README says that it includes native tree-sitter libraries and a complete grammar set. Archive inspection found 257 native files under `runtimes/<rid>/native/`:

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

This is the full Windows, Linux, and macOS RID set that the candidate advertises. It does not rescue the size gate: the all-grammar native payload is the reason the compressed artifact is over budget.

## Published provenance and license metadata

The artifact's single `.nuspec` declares the following metadata:

| Field | Value |
| --- | --- |
| License expression | `MIT` |
| Repository type | `git` |
| Repository URL | `https://github.com/mariusgreuel/tree-sitter-dotnet-bindings.git` |
| Repository commit | `8cae484bc033dac6e492ed15166877f3d784850f` |
| Target framework | `.NETStandard2.0` |

That metadata is recorded for traceability only. The proof's sorted `licenseAndNoticePaths` inventory is empty: the archive has no in-package `LICENSE`, `NOTICE`, `COPYING`, or third-party-notice file. Its `provenanceReview.status` is `metadata_recorded_not_accepted`, so AppSurface has not accepted the candidate's provenance or notices for redistribution and has not added a third-party notice. Completing that human review would not change the already-failing size gate.

## Static-inspection boundary

The original spike proposed a disposable restore and child-process smoke. Security review established that a local NuGet feed and child process do not sandbox package build assets, managed assemblies, analyzers, or native libraries. This candidate has already failed its non-negotiable compressed-size gate, so executing its code would add risk without changing the rejection decision.

The committed gate therefore performs bounded static inspection only: it reads the archive hash, central-directory metadata, native RID paths, notice-path inventory, and a root `.nuspec` whose size is capped at 512 KiB. It rejects archives above 64 MiB compressed, 1,024 entries, or 1 GiB declared uncompressed size before parsing package metadata. It never restores, builds, loads, or executes the candidate, and no AppSurface project references it.

A future candidate that passes the static gate needs a separately approved, operating-system-level sandbox design before runtime initialization can be considered acceptance evidence. This record makes no claim that TreeSitter.DotNet initializes correctly on any RID.

## Reproduction

Download the exact candidate, then verify the digest and archive measurements before treating any runtime observation as evidence:

```bash
curl -fsSL --output /tmp/treesitter-dotnet-1.3.0.nupkg \
  https://api.nuget.org/v3-flatcontainer/treesitter.dotnet/1.3.0/treesitter.dotnet.1.3.0.nupkg
shasum -a 256 /tmp/treesitter-dotnet-1.3.0.nupkg
stat -f '%z bytes' /tmp/treesitter-dotnet-1.3.0.nupkg
unzip -l /tmp/treesitter-dotnet-1.3.0.nupkg
unzip -p /tmp/treesitter-dotnet-1.3.0.nupkg '*.nuspec'
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  inspect-python-parser-candidate \
  --python-parser-package /tmp/treesitter-dotnet-1.3.0.nupkg \
  --python-parser-proof-report artifacts/python-parser-proof.json
```

Run the command from the repository root. The recorded digest and size are listed above. If either differs, the candidate has changed and the gate must be rerun from the new artifact rather than reusing this result. The command writes its JSON only below the repository's `artifacts/` directory and never adds, restores, builds, loads, or executes `TreeSitter.DotNet` in an AppSurface project.

## Scope consequence

The [approved spike plan](../../../../docs/designs/python-docstring-harvesting-spike.md) requires the candidate gate to pass before source integration. Accordingly, this change deliberately does **not** add `TreeSitter.DotNet` to any project, change a published package payload, or claim Python documentation support. The next work is a separately approved parser-selection decision, informed by this rejection and a real Python-host adoption convention.
