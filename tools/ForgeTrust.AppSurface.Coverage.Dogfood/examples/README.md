# Coverage fan-in examples

These examples use the installed [headless package proof](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.Coverage.Dogfood/InstalledPackageProof.cs), the checked-in [headless project template](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.Coverage.Dogfood/fixtures/headless/Fixture.csproj.template), and the repository [dogfood adapter](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.Coverage.Dogfood/Program.cs). The proof creates the consumer project in its output directory and fills in the candidate version before restore. The examples exercise the public facade and installed CLI with live source identity and generated request/result documents. The generated JSON contains machine-local paths and run identifiers; inspect it from the output directory rather than copying a stale document from another machine. The C# files in this directory are documentation samples, copied as noncompiled content; they add no helper runtime to the adapter.

Use ordinary [`coverage merge`](../../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-merge) when compatible reports only need to be combined. Use fan-in when the caller needs an explicit complete test inventory, source/run identity, required producer set, conservative composition, aggregate/patch policy, and independent acceptance. The package decision and all defaults are documented in the [Coverage reference](../../../Coverage/ForgeTrust.AppSurface.Coverage/README.md) and [fan-in guide](../../../guides/coverage-fan-in.md).

## Run the installed headless package proof

Build the coordinated candidate packages first. For the complete walkthrough, the same local feed must contain `ForgeTrust.AppSurface.Coverage`, `ForgeTrust.AppSurface.Evidence.Coverage`, and `ForgeTrust.AppSurface.Cli` at the exact `CANDIDATE_VERSION`. The `--package-proof` path validates the two library packages in an isolated headless consumer and exercises the checked-in fixture's real collection path, public capture/composition/acceptance/verification flow, and altered-output rejection. The native-import journey also installs the candidate CLI from that same feed and passes its installed `appsurface` path to the repository adapter. It creates only the fresh proof output directory and retains stage logs there.

The fixture's native producer declares two projects and retains their original collector reports, exact ordered index and successful timings. [`CaptureNativeSetAsync`](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/CoverageFanInApiExample.cs) shows how an existing trusted producer supplies `RawCoveragePaths` and `TimingsPath`; `CaptureNativeAsync` covers the single-project normalized/raw form. The [request reference](../../../Coverage/ForgeTrust.AppSurface.Coverage/README.md#request-and-result-reference) defines their mutually exclusive selectors. Both forms preserve original bytes and require independent checks before acceptance.

Run this PowerShell 7 recipe from the repository root after setting `$CandidateFeed` to the local feed directory and `$CandidateVersion` to the exact package version. It needs no Bash syntax:

```powershell
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CandidateFeed)) { throw 'Set $CandidateFeed to the candidate package feed directory.' }
if ([string]::IsNullOrWhiteSpace($CandidateVersion)) { throw 'Set $CandidateVersion to the exact candidate package version.' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$proofOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("appsurface-coverage-package-proof-" + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $proofOutput) { throw "Proof output already exists: $proofOutput" }

dotnet run --project tools/ForgeTrust.AppSurface.Coverage.Dogfood `
  --configuration Release -- `
  --package-proof `
  --feed $CandidateFeed `
  --version $CandidateVersion `
  --output $proofOutput `
  --dotnet $dotnet
if ($LASTEXITCODE -ne 0) { throw "Installed package proof failed with exit code $LASTEXITCODE" }
```

`proof.json` and per-stage logs are written beneath `$proofOutput`. The fixture uses `net10.0-windows` on Windows and `net10.0` on Linux and macOS. Both build and publish must retain the [pinned ReportGenerator payload](../../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md#reportgenerator-runtime-payload). A successful run validates the packed, headless package path on the host where it ran; the native CI matrix supplies the other platform results.

The carrier installs the candidate CLI into its isolated tool directory automatically and keeps the direct PackageReference graph independent of that tool.

## AppSurface repository-specific native shell adapter (Bash)

The following recipe uses this repository's policy adapter to generate exact CLI requests/results for native capture and imported fan-in. It is AppSurface-specific; a downstream consumer should build its own policy adapter around the neutral API or invoke the installed commands with its own strict-v2 requests. The portable PowerShell proof and same-feed candidate CLI install are above.

## Generate exact CLI requests and results

The adapter requires a clean source checkout and explicit run identity. Supply real artifacts produced by the collector jobs: `NORMALIZED_REPORT` is the collector's normalized Cobertura report, `RAW_REPORT` is the original collector output, and the receiving fan-in input directory must contain one matching `manifest.json` for each required producer. Use the same run scope, run ID, attempt, source, SDK, and configuration across the native jobs and receiving job. Each `--output` value must name a fresh path.

On an actual native runner, capture that runner's supplied report pair:

```sh
: "${REPOSITORY_ROOT:?Set this to the clean source checkout}"
: "${APPSURFACE_CLI:?Set this to the installed appsurface executable}"
: "${RUN_SCOPE:?Set the trusted provider run scope}"
: "${RUN_ID:?Set the trusted provider run ID}"
: "${ATTEMPT:?Set the positive provider attempt number}"
: "${NATIVE_PLATFORM:?Set Linux, Windows, or Darwin from the actual runner}"
: "${NORMALIZED_REPORT:?Set the actual normalized Cobertura report path}"
: "${RAW_REPORT:?Set the retained original collector report path}"
native_output="${TMPDIR:-/tmp}/appsurface-native-capture-$$"
test ! -e "$native_output"
dotnet run --project tools/ForgeTrust.AppSurface.Coverage.Dogfood \
  --configuration Release -- \
  --operation native \
  --repository "$REPOSITORY_ROOT" \
  --cli "$APPSURFACE_CLI" \
  --output "$native_output" \
  --configuration Release \
  --native-platform "$NATIVE_PLATFORM" \
  --report "$NORMALIZED_REPORT" \
  --raw "$RAW_REPORT" \
  --run-scope "$RUN_SCOPE" \
  --run-id "$RUN_ID" \
  --attempt "$ATTEMPT"
```

Run this once per required native producer using that producer's real runner and artifacts. Each invocation demonstrates capture on its current host; it does not stand in for the full platform set or assert a platform pass.

On the receiving job, provide the real complete primary report/timings and the downloaded native capture directories:

```sh
: "${PRIMARY_REPORT:?Set this to the complete primary normalized report}"
: "${PRIMARY_TIMINGS:?Set this to the complete primary timing inventory}"
: "${DOWNLOADED_NATIVE_INPUTS:?Set this to the downloaded native capture root}"
: "${REPOSITORY_ROOT:?Set this to the clean source checkout}"
: "${APPSURFACE_CLI:?Set this to the installed appsurface executable}"
: "${RUN_SCOPE:?Set the trusted provider run scope}"
: "${RUN_ID:?Set the trusted provider run ID}"
: "${ATTEMPT:?Set the positive provider attempt number}"
fanin_output="${TMPDIR:-/tmp}/appsurface-coverage-fanin-$$"
test ! -e "$fanin_output"
fanin_args=()
if [ -n "${DIFF_BASE:-}" ]; then
  fanin_args+=(--diff-base "$DIFF_BASE")
fi
dotnet run --project tools/ForgeTrust.AppSurface.Coverage.Dogfood \
  --configuration Release -- \
  --operation fanin \
  --repository "$REPOSITORY_ROOT" \
  --cli "$APPSURFACE_CLI" \
  --output "$fanin_output" \
  --configuration Release \
  --primary "$PRIMARY_REPORT" \
  --timings "$PRIMARY_TIMINGS" \
  --native-inputs "$DOWNLOADED_NATIVE_INPUTS" \
  --run-scope "$RUN_SCOPE" \
  --run-id "$RUN_ID" \
  --attempt "$ATTEMPT" \
  "${fanin_args[@]}"
```

Run the block with Bash. A nonempty `DIFF_BASE` explicitly selects `AggregateAndPatch`; leaving it unset selects the existing `AggregateOnly` baseline, which returns `PatchNotRequested` and cannot close a patch-required Evidence obligation. The adapter writes the exact strict-v2 requests and terminal JSON results it sends/receives:

```sh
jq . "$fanin_output/primary-request.json"
jq . "$fanin_output/primary-result.json"
jq . "$fanin_output/compose-request.json"
jq . "$fanin_output/compose-result.json"
jq . "$fanin_output/accept-request.json"
jq . "$fanin_output/accept-result.json"
jq . "$fanin_output/verify-request.json"
jq . "$fanin_output/verify-result.json"
```

The primary and supplementary `*-request.json` documents contain the exact current paths and complete v2 policy generated by the adapter. The paired `*-result.json` documents contain the installed CLI's actual terminal result. They are useful execution examples, but should remain in the chosen output area because their request fields intentionally include local filesystem paths. The terminal result projection omits artifact paths.

The adapter invokes these four parser-registered commands (the capture command also has a `--collect` selector for a `collect` envelope):

```sh
"$APPSURFACE_CLI" coverage fan-in capture --request "$native_output/native-request.json" --result "$native_output/native-result.json" --json
"$APPSURFACE_CLI" coverage fan-in compose --request "$fanin_output/compose-request.json" --result "$fanin_output/compose-result.json" --json
"$APPSURFACE_CLI" coverage fan-in accept --request "$fanin_output/accept-request.json" --result "$fanin_output/accept-result.json" --json
"$APPSURFACE_CLI" coverage fan-in verify --request "$fanin_output/verify-request.json" --result "$fanin_output/verify-result.json" --json
```

Those lines show the exact invocations made during the successful adapter run. Writing requests target destinations already used by that run, so do not replay them against the same output. `verify` is read-only; it can be rerun with the saved request and `--json`, omitting `--result` or selecting a new result file.

## Direct C# and EvidenceHost

[CoverageFanInApiExample.cs](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/CoverageFanInApiExample.cs) contains copyable calls for all five facade operations. It demonstrates both producer-artifact capture and portable native-capture import. Pass the operation a frozen policy built from the caller's actual complete inventory and trusted source/run identity; use fresh output directories. `ComposeAsync` success is not acceptance. `AcceptAsync` must return `Outcome == Succeeded`, `Accepted`, `Verified`, a committed generation, and complete cleanup before the caller relies on it. `VerifyAsync` only checks existing local evidence and does not repair custody or make replay fresh.

[EvidenceCoverageFanInProducerExample.md](EvidenceCoverageFanInProducerExample.md) shows the declaration, obligation-facing assertion, and registration for the shipped Evidence adapter. Add that declaration to the selected Evidence profile, make the obligation require its assertion, and keep the declared gate aligned with the request policy. This example sets both `requirePatch` and `requireObservedPrimary`; a trusted-producer capture therefore cannot satisfy it. The producer writes its result artifact before it returns the assertion, and any failed result delivery leaves the obligation unsatisfied.

The strict request root has exactly `schemaVersion`, `operation`, and `request`. `schemaVersion` is the integer `2`; request members use exact camelCase, enums use exact camelCase strings, and duplicate, unknown, missing, wrongly typed, or contradictory fields are rejected before execution. The request envelope's version, capture schema, policy encoding (`coverage-policy-c14n-1`), and local custody registration are separate version domains. See the guide's [operation schemas and exit behavior](../../../guides/coverage-fan-in.md#installed-commands-and-request-schema) before generating requests outside the adapter.
