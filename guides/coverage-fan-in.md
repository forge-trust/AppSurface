# Native coverage fan-in

AppSurface dogfoods complete primary solution execution together with independently required Linux, Windows and macOS evidence. The reusable [Coverage facade](../Coverage/ForgeTrust.AppSurface.Coverage/README.md) and [installed CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-fan-in) provide the native path. AppSurface supplies its explicit policy rather than embedding its solution name, project count, platform set or CI provider in the library.

The primary aggregate and composed patch gates remain 95% line / 85% branch with the existing 0.5-point tolerance and Codecov patch mode. Native supplements never replace missing or failed primary projects. The [storage contract](coverage-storage.md) governs native ownership, leases, history and recovery. Separate downstream consumer adoption and onboarding measurements are deferred; current acceptance focuses on AppSurface dogfooding.

## Native schema-v2 workflow

The neutral [Coverage package](../Coverage/ForgeTrust.AppSurface.Coverage/README.md) supplies asynchronous collection/capture, composition, acceptance and independent read-only verification. The [AppSurface CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-fan-in) exposes the same operations. The [EvidenceHost adapter](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#coverage-fan-in-producer) requires independently accepted current-run evidence and successful result delivery before it returns an assertion. Capture/compose success is never an acceptance result.

Use ordinary `coverage merge` to combine compatible reports when source/run-bound acceptance is unnecessary. Fan-in adds exact intended project/native inventories, clean commit/tree and SDK/configuration identity, explicit instrumentation/run policy, conservative class/method composition, a separate primary aggregate gate, and independent semantic verification. It does not sign artifacts or prove that arbitrary caller-supplied tests actually ran. `CollectedExecution` originates in supervised collection; `TrustedProducer` is an explicit trust boundary. Local import preserves either observation and never claims new execution.

The public contracts and defaults are in the [package reference](../Coverage/ForgeTrust.AppSurface.Coverage/README.md). Every request supplies a complete intended primary inventory and exact supplementary IDs/platforms/filters. Supplementary project inventories may be narrower than the primary only when explicitly declared. Paths use slash-relative identities, not a project-name suffix convention. The reusable engine never substitutes AppSurface's project count or CI variables for consumer declarations. AppSurface supplies its own complete policy in the [dogfood adapter](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/Program.cs), including all solution projects and Linux/Windows/Darwin native evidence.

For copyable calls to all five facade operations, the shipped Evidence producer, a generated strict-v2 CLI request/result walkthrough, and the exact installed package proof command, run the [coverage dogfood examples](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/README.md). The examples use runtime source/run identity and actual producer artifact paths. They do not embed reusable-looking hashes or claim that a platform's native matrix passed.

### C# operations and ordering

All facade methods return `ValueTask<CoverageFanInResult>` and accept the optional cancellation token plus caller-owned progress and diagnostic writers. The concrete calls and parameters are in [CoverageFanInApiExample.cs](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/CoverageFanInApiExample.cs); use the exact result contract to distinguish completed work from acceptance.

1. `CoverageFanIn.CollectAndCaptureAsync(CoverageCollectRequest, token, progress, diagnostics)` runs the declared inventory through the supervised collector and captures only success. Supply a collection directory and explicit 1–86,400 second deadline. Alternatively `CaptureAsync(CoverageCaptureRequest, ...)` validates a trusted successful normalized primary plus complete timings, a native normalized report plus original collector bytes, or an imported portable capture plus its exact original source root. Fresh output is the default; `ReplaceOwned` is explicit.
2. `ComposeAsync(CoverageComposeRequest, ...)` requires the primary and exact supplementary set. It retains all class and method occurrences, overloads and native-only points. Matching-denominator individually full observations may promote a branch; partial observations never add together to invent full coverage.
3. `AcceptAsync(CoverageAcceptRequest, ...)` checks the complete primary aggregate, the composed patch when explicitly requested, then independent facts and gate arithmetic. It commits an exact accepted generation only after required work completes. Numeric gate failures retain rejected gate reports without an acceptance receipt.
4. `VerifyAsync(CoverageVerifyRequest, ...)` checks existing retained captures/composition/acceptance against trusted caller expectations. It creates no coordination, repairs nothing, and makes no historical evidence fresh. `Outcome == Succeeded` and `Verified == true` establish consistency under the supplied expectations; neither property authenticates a producer or independently proves arbitrary test execution.

Requests snapshot caller collections before mutation; subsequent caller edits do not change a running policy. Callers own writers and cancellation sources, which are not disposed or retained. The .NET SDK, Git, consumer test SDK and supported Coverlet collector prerequisites must be installed. The private support package owns the complete pinned ReportGenerator runtime; fan-in refuses missing/version-skewed assets before collection and does not fall back to a globally installed tool, NuGet cache, or source checkout.

`CoverageGateMode.AggregateOnly` is an explicit baseline with `PatchNotRequested`. It creates no patch-pass artifact and cannot satisfy a patch-required obligation. `AggregateAndPatch` requires the declared diff base, the identical frozen diff for primary/composed analysis, nonempty measurable changed lines and line/branch denominators no smaller than the primary reference. This choice never silently follows from an absent diff. AppSurface's configured thresholds/tolerance, Codecov mode, complete primary and native requirements remain authoritative.

### Installed commands and request schema

The four parser-registered commands are `appsurface coverage fan-in capture`, `compose`, `accept`, and `verify`. The `capture` command has a `--collect` selector for the fifth facade operation, collection-and-capture. Each takes `--request <file>`; writing commands require `--result <new-file>` in an existing safe directory. Add `--json` for exactly one bounded terminal document on stdout; progress and diagnostics use stderr. Verify's result destination is optional and only records adapter delivery; it does not authorize modification of evidence. Every requested result file must be fresh.

Collection uses a strict-v2 request whose envelope has `operation: "collect"` and whose `request` is a complete `CoverageCollectRequest` (including policy, clean repository root, fresh capture and collection destinations, producer ID, and deadline). The exact parser invocation is:

```sh
"$APPSURFACE_CLI" coverage fan-in capture \
  --request "$COLLECT_REQUEST_JSON" \
  --result "$COLLECT_RESULT_JSON" \
  --json --collect
```

The same command without `--collect` requires `operation: "capture"` and a producer report or portable import. Do not change only the envelope or only the selector: they must agree. The installed headless fixture's [`--package-proof` walkthrough](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/README.md#run-the-installed-headless-package-proof) demonstrates real supervised collection using the packed neutral packages and records its generated stage evidence.

The request, request-file and result-file paths must pass the CLI's regular, link-free filesystem checks. Request JSON is limited to 1 MiB and depth 64, rejects comments/trailing commas, and rejects duplicate property names at every depth. JSON property names and enum tokens are case-sensitive; decimal values are read losslessly. Missing optional members take the public record defaults, but the complete policy members and operation-required selectors must be present. See the exact requests generated by the [dogfood walkthrough](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/examples/README.md#generate-exact-cli-requests-and-results).

Requests use the exact envelope `{ "schemaVersion": 2, "operation": "capture", "request": { ... } }`, with camelCase request members and string enums. Collection uses `operation: "collect"` together with `capture --collect`. Unknown, duplicate, missing required, wrongly typed or contradictory fields fail before execution. API/package versions, CLI request schema, capture schema, local coordination and policy encoding are separate version domains; `coverage-policy-c14n-1` identifies canonical policy bytes, not an execution claim.

Exit 0 means the requested operation succeeded; 1 means rejection, failed/incomplete work or delivery failure; 2 means invalid input; 130 means cancellation; 124 preserves the terminal watchdog/deadline result. Inspect both the requested operation/outcome and delivery status. A composed capture may succeed while no accepted coverage exists. A committed accepted generation may survive failed adapter delivery; reconcile that exact generation before retrying delivery. Never blindly rerun or reuse the previous invocation's success.

The CLI JSON result has its own outer `schemaVersion: 2`, `operation`, and `status`, along with nullable `operationOutcome`, known effects, safe operation result, delivery state, and bounded diagnostics. A succeeded command requires operation success and result delivery; a `deliveryFailed` status can coexist with `operationOutcome: "succeeded"` and `generationCommitted: true`. Its artifact projection omits host paths. For Evidence, the shipped [`EvidenceCoverageFanInProducer`](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#coverage-fan-in-producer) writes a bounded JSON acceptance result to its declared artifact slot before it may return the declared assertion; its registration/declaration example also requires observed primary collection and patch acceptance.

### Portable inputs, replay and replacement

A downloaded v2 capture is transported data, not receiving-host ownership. Use `CaptureAsync`/`capture` with `importDirectory`, the exact `originalSourceRoot` and a fresh receiving destination. The receiver validates original bytes, source/run/attempt/policy and relative identities, creates new local custody, and preserves original producer observations. Copying lock files or inode metadata never grants authority. The same current trusted run's imported native evidence may contribute to acceptance only after exact caller expectations and independent checks pass. Historical replay remains historical, even if a caller changes its label or local directory.

Outputs default to fresh-only. Explicit `ReplaceOwned` additionally requires compatible private registration, purpose, current physical identity, exclusive custody and exact retained inventory/hashes. Unknown, altered, linked or foreign contents survive refusal. A populated legacy tree is never marked to manufacture ownership. Stage validation precedes prior retirement. A completion marker is the commit boundary: cancellation before it yields no complete generation; cancellation after it reports actual committed state. Required cleanup occurs before commit, and incomplete effects remain explicit.

V1 captures and the original [Aspire verifier API](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceCoverageFanIn.cs) retain their original strict read-only contract. V1 and v2 cannot mix. There is no in-place conversion that relabels old reports as new execution; recapture a new run and use fresh destinations when custody cannot be established.

### Diagnostics

`ASCOV301` identifies invalid explicit input; `ASCOV302` identifies failed evidence/custody checks; `ASCOV306` identifies cancellation/deadline or terminal watchdog; `ASCOV307` identifies rejected numeric gates; `ASCOV309` identifies post-operation delivery failure; `ASCOV310` identifies an unmet Evidence assurance requirement. CLI admission/delivery diagnostics use `ASCOVF001`–`ASCOVF006`, and runtime closure uses `ASCOV140`.

Read the stable code, operation/stage, field role, policy digest, operation/generation identity and recovery action. Expected rejection and cancellation do not include raw exception traces. Preserve failure artifacts, inspect the exact scope/identity/report that disagrees, correct the declared input or add honest tests, then retry once into a fresh safe destination. Unknown effects require reconciliation before retrying. Diagnostics must not copy arbitrary report data, secrets, raw child logs or dependency exception text into terminal output.

The current acceptance target is AppSurface dogfooding with its own solution and native matrix. Separate downstream consumer adoption and onboarding measurements are deferred by user direction; they are not established by local fixture tests or this guide.

## Historical v1 evidence

The original Python producer and shell policy checks are the schema-v1 behavioral baseline. Historical captures use their original strict source/SDK/configuration/filter/run rules and read-only [Aspire verification API](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceCoverageFanIn.cs). They are neither generic v2 captures nor new execution. Preserve old downloaded evidence; do not add ownership markers, mix schemas, rewrite manifests or relabel old reports to satisfy a new run. Recapture through the native path and select fresh output when custody cannot be established.

The independent v2 regressions preserve class/method occurrence and overload records, native-only points, conservative individually full branch promotion, and patch denominator scope. The [native test families](../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageFanInIndependentVerifierTests.cs) use hand-authored expected facts and altered-output cases; a producer receipt is never its own oracle.
