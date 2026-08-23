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

That metadata is recorded for traceability only. A filename scan of the archive found no in-package `LICENSE`, `NOTICE`, `COPYING`, or third-party-notice file. AppSurface therefore has not accepted this candidate's provenance or notices for redistribution, and it has not added a third-party notice. Completing that review would not change the already-failing size gate.

## Isolated consumer smoke

An isolated `net10.0` console project was created outside the repository. It restored only from a local feed containing the SHA-256-verified `.nupkg`; no AppSurface project referenced the candidate. The child process performed the candidate's documented Python initialization and parsed three fixed inputs:

1. A valid documented Python function.
2. A syntactically malformed function declaration.
3. A 1,000,000-byte repeated-source input, which is the largest fixed corpus input used by this gate check. It exercises parser behavior only; AppSurface has no Python-file budget implementation because the candidate was rejected before integration.

Observed child-process result:

```text
exit code: 0
RID=osx-arm64
VALID=module
MALFORMED=module
LARGE_SOURCE_BYTES=1000000
LARGE=module
```

The binding initialized and returned a parse tree for every input, including the malformed source, without an abnormal exit or stderr output. This is evidence for the local `osx-arm64` asset only. Windows and Linux consumer smoke paths were intentionally not added to CI: a full multi-RID proof is only useful after a candidate has met the non-negotiable distribution-size limit.

## Reproduction

Download the exact candidate, then verify the digest and archive measurements before treating any runtime observation as evidence:

```bash
curl -fsSL --output /tmp/treesitter-dotnet-1.3.0.nupkg \
  https://api.nuget.org/v3-flatcontainer/treesitter.dotnet/1.3.0/treesitter.dotnet.1.3.0.nupkg
shasum -a 256 /tmp/treesitter-dotnet-1.3.0.nupkg
stat -f '%z bytes' /tmp/treesitter-dotnet-1.3.0.nupkg
unzip -l /tmp/treesitter-dotnet-1.3.0.nupkg
unzip -p /tmp/treesitter-dotnet-1.3.0.nupkg '*.nuspec'
```

The accepted digest and size are listed above. If either differs, the candidate has changed and the gate must be rerun from the new artifact rather than reusing this result.

For the smoke, create an isolated `net10.0` console project that references only `TreeSitter.DotNet` `1.3.0` from a local feed containing that verified archive. Its program must instantiate `new TreeSitter.Language("Python")`, parse the valid, malformed, and 1,000,000-byte inputs, write `RuntimeInformation.RuntimeIdentifier`, and treat a non-zero or abnormal child-process exit as a candidate rejection.

## Scope consequence

The [approved spike plan](../../../../docs/designs/python-docstring-harvesting-spike.md) requires the candidate gate to pass before source integration. Accordingly, this change deliberately does **not** add `TreeSitter.DotNet` to any project, change a published package payload, or claim Python documentation support. The next work is a separately approved parser-selection decision, informed by this rejection and a real Python-host adoption convention.
