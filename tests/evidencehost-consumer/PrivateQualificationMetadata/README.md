# Private qualification metadata formatter

This standalone data formatter prepares one immutable definition for the separately compiled private qualification variant. It does not authenticate a worker, grant admission, allocate output, inspect a bundle file, start an application or producer, supply a factory, resolve a compiled registration, enroll a proof, or establish a passing runtime result. Production catalogues and proof/Trusted registries remain unchanged. The parent's private admission/manifest guard owns the approved Observation exception and forced Eligibility=None / Claim=None.

## Baseline dependency and build ordering

The qualification Contracts sources contain unexpanded compile-owned binding constants until real metadata is available. **Do not build this formatter against those sources.** Set the mandatory MSBuild property `QualificationBaselineRoot` to the parent's independently verified pristine `5d325bb8c0f857eb37f0a736f39a0342b03e5c38` archive. For prospective local compilation the known baseline path is `/private/tmp/issue779-coverage-edges-zr4naijr/repo`; verify its exact source/index/tree binding and clean HEAD before use. The workflow archives that same known ancestor into a separate baseline directory. This project rejects an empty/nonabsolute baseline path or missing Contracts/Planner projects; it does not authenticate the contents of that path. The protected parent owns that verification.

The two ProjectReferences use this baseline property. They select metadata build dependencies only and cannot act as an execution/admission switch. The executable's assembly name is `ForgeTrust.AppSurface.Cli.Tests`, using the baseline's already-existing friend declarations to call the pure internal catalogue audit APIs. There is no new friend declaration or reflection, and the executable uses no admission/context/worker APIs. It is a console executable, not a test suite or a packable product.

At the initial source handoff, no build or restore had run and this new project had no generated lock. The parent subsequently generated `packages.lock.json` against the verified baseline, then completed locked restore, owned-file formatting and a local build with exit0 and zero compiler warnings/errors. An initial MSBuild path-condition failure was preserved before that successful attempt. This is local compilation evidence; the subject fixture has not been evaluated and native qualification has not run.

For a new fixture without its lock, the reviewed initial command is `dotnet restore --use-lock-file -p:RestoreLockedMode=false -p:QualificationBaselineRoot=<verified-absolute-baseline>`. With the committed lock, run `dotnet restore --locked-mode` and `dotnet build --no-restore -p:UseSharedCompilation=false`, each with the same baseline property. The initial lock-generation override applies only to the new fixture; do not change the baseline's checked-in dependency locks. Contracts and Planner have no direct PackageReferences in this baseline, so this project's dependency graph consists of those existing ProjectReferences plus net10.0 framework references. Audit defaults remain enabled; no warning/audit suppression is added. This project treats compiler warnings as errors.

Run the finished executable by its private DLL with one complete stdin JSON envelope and EOF. The parent must impose its finite process deadline. The formatter byte bound is not a promise to finish when a caller leaves stdin open. Do not pass command-line filenames, environment-selected metadata or the unexpanded qualification source as input.

## Exact stdin data shape

Top-level JSON has exactly two case-sensitive fields, `policy` and `bundle_files`. Unknown fields, duplicate names and case aliases at this level or in a bundle row reject. `policy` is the complete typed `EvidencePolicy` object from the provided qualification fixture, not a policy invented by the formatter. The existing [contracts parser](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input) performs its real required-member/null/duplicate/enum validation and existing additive unknown-property compatibility for that policy; outputs contain only its canonical typed snapshot. The caller's raw policy bytes are not its canonical output digest.

`bundle_files` is an array of at most 256 rows with exactly:

| Field | Type and meaning |
| --- | --- |
| `RelativePath` | String; normalized closed ASCII relative bundle name. Data only, never opened by this executable. |
| `Role` | Exact case-sensitive name from the nine roles below; numeric enums reject. |
| `LengthBytes` | JSON Int64 integer; catalogue requires positive length, at most 128 MiB per file and 512 MiB aggregate. |
| `Sha256` | Exact lower-case 64-hex SHA256 supplied by the protected parent's actual file inspection. The formatter cannot establish it matches physical bytes. |
| `Mode` | JSON UInt32 integer: decimal **292** (`0444`) or **365** (`0555`). JSON has no octal number syntax. |

Roles are `AppHost`, `AppHostRuntimeConfiguration`, `Resource`, `ResourceRuntimeConfiguration`, `Dcp`, `DcpExtension`, `Dependency`, `DeclaredInput`, `DependencyManifest`. The actual catalogue validates duplicate/colliding names, lengths, hashes, native executable modes and role-specific names. Required roles appear exactly once: AppHost DLL, AppHost runtimeconfig, Resource DLL, Resource runtimeconfig, `dcp/dcp` executable, and declared input. DCP extensions are executable beneath `dcp/ext/`; dependency manifests are read-only names ending `.deps.json`. Any declared-input inventory must match the one fixed input below exactly. Root independently verifies retained physical bytes, ownership, mode and platform; matching caller metadata is not that verification.

Input must be one JSON document of at most **1,048,576 UTF-8 bytes**, with no comments/trailing commas and depth at most32. A one-byte overflow check rejects oversized input without retaining extra payload. Null/wrong type/malformed requests reject. This is a byte/data bound, not a one-MiB total heap limit.

## Fixed definition and planner selection

The formatter copies complete declarations from the `qualification-http` profile of the provided policy and constructs only:

| Value | Fixed selection |
| --- | --- |
| Application ID | `issue779-qualified-native-http` |
| Version | `1.0.0` |
| Build ID | `issue779-private-qualification-1` |
| Aspire SDK | `13.4.4` |
| Profile | `qualification-http` |
| Resource adapter | `native-http-uds` / `1.0.0`, resource name `native-http`; actual declaration copied unchanged |
| Producer implementation | `coverage` / `1.0.0`; every selected producer declaration copied unchanged |
| ReadOnlyInputs | Exactly `proof-input/declared.txt` |
| Scratch / memory | 536870912 / 1073741824 bytes |
| Tasks / received output | 128 / 1048576 bytes |
| Start / stopping | 120 / 5 seconds |

The real `EvidenceClosedApplicationCatalogue.Snapshot` validates and defensively freezes this data. It requires exactly one declared native HTTP resource, coverage producers, complete matching declarations, a semantically valid full policy, finite bundle metadata and exact input grants. Do not use an empty conservative fallback policy. `EvidencePlanner.Resolve` re-resolves the fixed changed path `tests/QualificationSubjectTests.cs` and must select `qualification-http`. No existing producer/artifact/gate/obligation declaration is padded, weakened or reconstructed. The root workflow separately pins the actual SDK/DCP payload, all eight kernel account/group identities, source/workflow/tool build and retained roots before any execution; identities are not accepted as this formatter's input data.

## Canonical stdout and fixed failures

Success returns exit0 with exactly one canonical UTF-8 JSON line whose total length, including newline, is at most1MiB:

| Exact output field | Value |
| --- | --- |
| `entrybase64` | Base64 of the validated canonical `EvidenceClosedApplicationDefinition` snapshot |
| `entry_digest` | Real `ComputeEntryDigest(snapshot)` result |
| `catalogue_digest` | Real `ComputeCatalogueDigest([snapshot])` for exactly one candidate entry |
| `policybase64` | Base64 of the snapshot's complete canonical policy |
| `policy_sha256` | SHA256 of those canonical policy bytes |
| `planbase64` | Base64 of the actual re-resolved canonical plan |
| `plan_digest` | The plan's real `PlanDigest` |

Snapshot/entry/catalogue audit APIs are data checks. This executable does not call `Resolve` on a compiled catalogue or issue an acceptance/admission marker. Output fields supply no Claim/Eligibility/status authority. Invalid input, failed structural audit, output overflow or ordinary I/O failure returns exit65 and one fixed safe stderr diagnostic, `ASEVD404: Private qualification metadata input is invalid or unsupported.` No input value, path, canary, exception or inner exception is echoed. If the output stream fails after a partial write, that partial output is invalid; the parent must require exit0 and the whole valid envelope before using any metadata. A failed stderr write cannot change exit65.

## Compile-owned registration template

[EvidenceClosedApplicationQualificationRegistration.cs.in](../../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationQualificationRegistration.cs.in) contains exactly one expansion marker. Its `.cs.in` extension means it is not included by the normal `*.cs` compile glob. The parent may expand it only after reviewing/pinning the real canonical entry output; write the finished `.cs` only into the separately built private Planner assembly. A partial source in the formatter/reference assembly cannot modify the Planner table.

The expanded partial decodes only its literal constant, uses the actual bounded canonical parser and snapshot audit, checks all fixed ID/version/profile/grant values and requires byte-for-byte canonical form before adding exactly one entry to an initially empty list. It never loads an environment variable, file, request, transport, callback or caller factory. Invalid compiled literal data throws fixed ASEVD404 without its bytes or an inner exception. This template neither supplies `EvidencePrivateQualificationBinding` nor fabricates an accepted proof. Render/bind the parent's separate admission/root templates with exact reviewed source/run/workflow/plan/entry/catalogue and actual kernel facts before compiling or executing that variant.

The original four-file handoff was source preparation only. The parent has since executed this formatter locally on bounded bundle metadata and compiled generated binding/catalogue source for both private entry shapes with zero compiler warnings/errors. Those local data and compile checks establish neither root execution nor a qualified result. Subject evaluation, native platform acceptance and production enrollment remain unclaimed.
