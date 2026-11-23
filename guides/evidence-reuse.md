# Local Evidence producer reuse

## Coverage report custody

Coverage collection, coverage-gate thresholds, and the producer's required assertions remain mandatory. The [private coverage adapter](../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md#reusable-coverage-report-custody) has a separate, optional cache-custody slot named `coverage-reports`. Its recommended declaration has `Required=false`, `MaximumFiles=128`, and an aggregate `MaximumBytes` of 256 MiB. Inventory metadata is bounded to 2 MiB. Original reports retain their established `runRoot/coverage/` layout and existing managed/full retention policy.

The slot is optional only for validating a fresh manifest. If a complete inventory is oversized, missing, unsafe, or corrupt, a safely passed fresh run keeps its original reports and assertion results, returns no partial custody set, and supplies a bounded receipt-ineligibility reason. The coordinator suppresses receipt publication. A consumer that explicitly declares this slot `Required=true` keeps the stronger contract: missing required custody invalidates its fresh manifest. The framework never silently waives an explicit required slot.

Ordinary report delivery tracks the same explicitly writer-owned files separately from optional storage inventory limits. Successful collection delivers reports for both passing and failing gates. A failed gate keeps its threshold diagnostic. If safe ordinary publication capture is unavailable, the original execution reports and gate result remain available at the summary's `ExecutionRoot`, and receipt publication is suppressed. A required report copy that fails after a complete capture fails the run with a bounded promotion reason while retaining the original reports. Cancellation and the producer deadline still apply to copying. Ordinary delivery closes and verifies each temporary report before atomically promoting it without overwrite; a failed copy removes only its own temporary file, so retries can retain completed reports and deliver the remaining set. Differing existing destination bytes are preserved and rejected. After required delivery, optional custody has a separate cancellation source capped by the original remaining producer budget; no timeout is extended. Expiry of only that optional phase reports `inventory-deadline` and preserves the completed fresh verdict. Caller cancellation still propagates, and a declared required custody slot still fails manifest validation when missing. The CLI adopts fully verified delivered reports without rewriting them; rejected custody cannot poison ordinary delivery. None of these paths can publish partial cache custody.

Receipt admission is stricter than the slot's fresh-manifest validation. Coverage publication and restore require a complete, versioned, validated report inventory, including required coverage/gate outputs, project manifests, timings, and requested retained reports or attachments. An empty optional slot cannot authorize a coverage receipt or hit. The adapter's receipt-eligibility signal must also permit publication; a fresh Passed verdict alone cannot certify receipt completeness.

Reuse additionally requires the explicit cooperative child-lifetime promise and actual invocation-owned child settlement. Parent SDK exit or successful inventory copying does not prove descendant settlement. The coordinator closes enrollment after the whole producer completes and joins admitted children before publication. The [approved design and cooperative amendment](../docs/designs/issue-917-producer-reuse.md#owner-amendment-cooperative-coverage-child-lifetime-2026-11-15) define that supported-workload boundary.

Keep per-run manifests, logs, and receipt stores in ignored scratch outside the fixture repository. Do not infer a cache hit from a passing fresh coverage gate; verify the producer outcome and receipt publication diagnostic.

## Cooperative child-lifetime promise

`EvidenceCoverageExecutionOptions.CooperativeChildLifetime` is an optional init-only boolean, false by default and omitted when false. A coverage producer is eligible for reuse only when its workload explicitly promises the cooperative-v1 protocol. Every test-created asynchronous or process child must enroll before starting work and remain awaited through actual cleanup. An unregistered or detached child violates the workload promise; this protocol is cooperative and is not OS containment. Without the opt-in, coverage runs fresh and cannot publish a reusable receipt. See the canonical [`EvidenceOwnedChild` reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#cooperative-child-lifetime) and the [approved amendment](../docs/designs/issue-917-producer-reuse.md#owner-amendment-cooperative-coverage-child-lifetime-2026-11-15).

Use `EvidenceOwnedChild.RunAsync(Func<CancellationToken, Task>, CancellationToken)` for owned asynchronous work or `EvidenceOwnedChild.RunProcessAsync(ProcessStartInfo, CancellationToken)` for a child process. The overloads taking an `IReadOnlyDictionary<string,string>` allow a prepared host to pass `EvidenceProducerContext.OwnedChildEnvironment` without mutating global process environment. Version 1 supplies that channel only to eligible built-in coverage workloads; custom producers receive null and must own and join their children themselves. Process descendants inherit the channel; each descendant still enrolls independently. When the invocation channel is absent, these helpers simply await the supplied work/process and do not create reuse eligibility.

The prepared-host API is `EvidenceHostBootstrap.PrepareAsync(EvidencePlan, Action<EvidenceHostPreparationRegistration>, EvidenceHostOptions? = null, TimeProvider? = null, CancellationToken = default)`, returning an owned, single-use `EvidencePreparedRun`. Register every selected producer with `AddProducer(IEvidenceProducer producer, string implementationIdentity, IReadOnlyDictionary<string,string>? toolIdentity = null)` and every selected resource identity, including unused resources, with `AddResource(string resourceId, string identity)`; also register an optional verifier with `SetEnvelopeVerifier(IEvidenceExecutionEnvelopeVerifier verifier)`. These declarations start no resources. Read the pinned `RequiredResourceIds` and `Decisions`, then call `RunAsync(Func<IReadOnlyList<string>, CancellationToken, ValueTask<EvidenceHostResourceScope>>, bool observationOnly = false, CancellationToken = default)`. Contract 1.0 lists every selected resource in `RequiredResourceIds`, including unused resources; contract 1.1 lists the transitive resource closure required by producers that will run fresh. This 1.1 closure also applies when reuse is disabled: declare every startup dependency in its producer's `RequiredResources`, including completion work such as migrations. An unreferenced resource is still validated but contributes no assertion or startup requirement. Existing 1.0 plans retain all-resource startup. The factory must create only those exact resources and return `new EvidenceHostResourceScope(IReadOnlyList<IEvidenceResourceReadiness> resources, IAsyncDisposable applicationOwner)`. It owns and cleans partial construction if it throws; after return, the framework owns and disposes the scope after actual settlement. `ArtifactDirectory` defaults to `TestResults/evidence/artifacts`; `ArtifactDirectories` maps opaque producer IDs to absolute per-producer roots. Do not derive filesystem paths from producer IDs. `EvidenceProducerContext.ExecutionId` and `OwnedChildEnvironment` are non-positional, JSON-ignored host capabilities; key-material callbacks do not receive them. `EvidenceHostOptions.CapturedPatch` is nullable `ReadOnlyMemory<byte>?`: null means no captured patch, while an empty value means a captured empty patch, and its existing 20 MiB bound is applied before copying. See the [Aspire lifecycle API reference](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#explicit-bootstrap).

`EvidenceHostOptions.ReceiptWaitProgress` reports `EvidenceReuseWaitProgress(ProducerKeyPrefix, Elapsed, Remaining)`. Elapsed/remaining cover the cumulative monotonic wait budget across keys and the single permitted reprepare; the progress record exposes only a bounded key prefix, never raw keys, paths, or environment values. Pass the host's `TimeProvider` when deterministic lifecycle timing is required; it supplies the private receipt-store clock as well. Defaults remain the existing 30-minute finite wait, 2 GiB store quota, and seven-day receipt retention.

With `--reuse`, an omitted `--output` creates a random exclusive run root under `TestResults/evidence/runs/<random-id>`; an explicit output must also be new and unlinked. The root contains the plan, manifest, and summary. The fresh execution root is `execution/<lowercase-SHA256-of-producer-id>/`; its coverage collector output stays there. For a single selected producer, receipt custody uses the invocation output root itself, preserving reports at the established `--output/coverage/` paths. With multiple selected producers, custody uses `producers/<lowercase-SHA256-of-producer-id>/` per producer to avoid collisions; the summary's `ArtifactRoot` reports the actual selected root. In prepared EvidenceHost runs, use `ArtifactDirectory` and the `ArtifactDirectories` ID-to-absolute-root map, whose per-producer custody roots are under `producers/<lowercase-SHA256-of-producer-id>/`. Producer IDs are opaque; consumers must use the returned map and must not recreate these hash paths themselves. Keep each command's output directory fresh so prior reports cannot masquerade as this invocation's output.

## Input declaration and key boundary

The nullable `EvidenceProducerDeclaration.Inputs` is a producer-owner promise that `Include` and `Exclude` cover every file that can affect execution. Include root build properties/targets, `global.json`, package centralization and lockfiles, test code, fixtures, scripts, tool manifests and any other consumed files. The planner's [declared-input reference](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md#declared-producer-inputs) owns the exact glob grammar and diagnostics: repository-relative forward-slash exact paths, `*` within one segment, and `**` only as a complete segment; include must be nonempty, exclude may be empty. Absolute/drive paths, backslashes, empty/`.`/`..` segments, control characters, embedded `**`, and `?`, bracket, or brace patterns are invalid. Matching is over Git's cached and nonignored untracked files; ignored `bin/`, `obj/`, and other ignored/generated files are invisible. If ignored generated state influences execution, do not assert that the declaration is complete. Preparation requires a nonempty selected set, at most 100,000 files and at most 16 MiB encoded input metadata, and rejects changes during hashing or while waiting.

The content key hashes the selected producer declaration, exact sorted input paths/modes/lengths/bytes, implementation identity, effective tool identity, transitive required-resource identities, bounded extra key material, platform/runtime architecture, and exact patch bytes when required. It does not hash commit IDs, branch names, index state, unrelated files, or historical timing. Thus an unrelated commit/edit preserves a key; an included file addition/removal/rename/content change, or a mode change represented by the current OS, produces a new identity. Unix hashes all file-mode permission bits, including execute bits; Windows hashes the read-only attribute. An actual Unix working-tree mode change affects identity, while an index-only mode change is excluded. Keep fixture inputs and lockfiles stable, restore locked dependencies before establishing the baseline, and treat under-declaration as a correctness defect rather than a cache tuning choice. The [Contracts API reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#local-producer-reuse-contract-candidate-11) covers key-material limits and callback settlement.

### Practical choices and sharp edges

Keep declared inputs stable for the entire producer execution, including owned child work and cleanup.
The [approved input boundary](../docs/designs/issue-917-producer-reuse.md#input-identity)
rehashes current bytes before admitting a hit and before publication; it provides no filesystem snapshot or
complete history of intervening writes. An input changed and restored to its original bytes between those
checks cannot reliably be detected. Producers that modify or consume concurrently modified declared inputs
must remain fresh-only until the owner can guarantee stable execution inputs.

- Keep `coverage-reports` custody optional (`Required=false`) when fresh coverage passing is independently required and an oversized report set should remain usable as a fresh pass. Its bounded inventory must still be complete before any receipt can publish or restore. Declare it `Required=true` only when missing report custody itself should make the normal fresh manifest invalid.
- `Inputs` is a producer-owner completeness promise. Include root shared build configuration, SDK selection, package/lock inputs, tools, test projects and all of `consumer/**` (including nested package configuration); an ignored `bin/` or `obj/` directory is not a declaration mechanism. Restore locked dependencies before making the first stable fixture commit. A later comment-only edit to an included file intentionally invalidates the key; an unrelated commit or edit outside the declared set does not.
- Use fresh output directories for each CLI invocation. `--reuse` controls candidate admission; `--verify-reuse` audits a hit with fresh work and atomically replaces only after a successful matching audit. A reused result is local informational evidence: `verify` checks integrity, while `verify --for-gate` must reject it. Reuse must not contribute historical producer time to fresh timing baselines.
- `ArtifactDirectories` values are the authority for producer roots. Producer IDs are opaque and may have historically valid values that are not safe path components.
- Store selection precedence is the explicit host option or CLI `--receipt-store`, then `APPSURFACE_EVIDENCE_RECEIPT_STORE`, then the per-user LocalApplicationData path `ForgeTrust/AppSurface/evidence-receipts/v1`. A different root is a different coordination domain. Authority metadata requires native directory synchronization on Linux/macOS or an NTFS write-through rename on Windows. Other Windows filesystems are unavailable for reuse; choose a supported private store or run fresh without reuse. The store is capped at 2 GiB; receipts expire seven days after origin completion and least-recently-used eligible entries are evicted under capacity pressure. Active reservations, InFlight/AuditIntent and quarantined entries are protected. Keep this store outside the repository inputs and output roots.
- `--reuse` on `evidence explain` is read-only: it creates no store, lease, LRU update, restore or producer work. Keep its `--output` separate from the effective receipt-store root; overlapping roots (including case variants) are rejected before plan artifacts are written. A read-only hit reports a candidate and `Publication=not-published`, while an execution hit reports `Publication=restored` only after files were restored. An executed audit emits `VerifiedReceiptCompared=true` only after its fresh outcome and assertions match an actually verified receipt; the field is omitted when no such comparison occurred. That comparison does not establish cleanup or publication success, which must still pass independently. Its decisions may include `reuse.audit-quarantined` when the read-only receipt lookup finds pending or mismatched audit authority. A writable ordinary `evidence run --reuse` reports that quarantined key as `reuse.audit_required`; an explicit `--verify-reuse` run can replace it only after a successful fresh audit. Other explain reasons include `reuse.no-comparable-receipt`, `reuse.inputs_required`, `reuse.child_lifetime_required`, `reuse.unresolved_execution`, `reuse.inputs_changed`, `reuse.tool_changed`, `reuse.diff_changed`, and `reuse.configuration_changed`. An input mismatch names the exact first differing repository-relative path (JSON-quoted) in the diagnostic. The [CLI command reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) defines the flags; the [Contracts reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#local-producer-reuse-contract-candidate-11) defines result provenance and claim limits.
- `EvidenceHostOptions.ReceiptWaitTimeout` and CLI `--receipt-wait-timeout` default to 30 minutes and must be finite and positive. The monotonic budget is cumulative across all key leases and the one permitted reprepare, with bounded backoff and cancellation. `ReceiptWaitProgress` is optional and does not expose raw keys. Cancellation before admission starts no producer; after work starts, cancellation does not release execution authority until the owned producer/children actually settle.
- Receipt custody admits at most 256 files, 1,024 aggregate tree entries including its container and metadata, and 32 path components per artifact. Oversized trees are refused before reservation; fresh reports remain available. A committed generated payload name also identifies its paired staging name for rename-rollback recovery. Reclamation releases its charge only after both names are absent; ambiguous or unreadable names stay charged. This recovery rule does not promise persistence of a reusable payload after power loss.
- Expected audit outcome/assertion metadata is limited to 4 KiB UTF-8. If that essential recovery record cannot be represented, admission fails before fresh audit work; it does not silently bypass quarantine or remove the audit requirement.
- If eviction wins before a waiting invocation acquires its key, the new owner can finish the recorded eviction and proceed with a fresh miss. If the store cannot confirm removal, the eviction record retains its quota charge and admission reports unavailable. Existing staging data with unconfirmed ownership can remain charged until absence is verified.
- Unknown InFlight state, missing/invalid child settlement, pending audit intent, or audit mismatch/quarantine refuses ordinary reuse and publication. Never clear authority by PID, age, or store reset. To recover unknown execution, stop the old EvidenceHost and producer environment, verify its processes/containers have stopped, preserve its old store for inspection, and deliberately select a fresh store/domain. Audit quarantine is cleared only by a complete successful explicit fresh audit and atomic receipt replacement. See the [approved recovery contract](../docs/designs/issue-917-producer-reuse.md#receipt-custody-concurrency-and-storage).
- Startup recovery never resets an InFlight/AuditIntent record by age or PID. An unknown/detached child keeps the key unavailable until the execution environment is stopped and recovery moves deliberately to a fresh store/domain. Do not recommend changing stores before the old environment has actually stopped.

## Versioned JSON reference

The Contracts package ships canonical emitted-shape references: [contract 1.0](https://github.com/forge-trust/AppSurface/blob/58f30b019939f4a79bd51685324ec7e070f85c04/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/schemas/evidence-contracts-1.0.schema.json) and [contract 1.1](https://github.com/forge-trust/AppSurface/blob/58f30b019939f4a79bd51685324ec7e070f85c04/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/schemas/evidence-contracts-1.1.schema.json). Version 1.0 excludes `Inputs`, `MaximumFiles`, `CooperativeChildLifetime`, reuse provenance, new timing fields, and derived unused-resource state; it never emits `ReuseCount`. Version 1.1 describes those additions, requires complete reuse provenance and zero current elapsed time for `Reused`, and emits `ReuseCount` only when positive; `LocalReusedComplete` remains informational rather than gate eligible. Constructor-position properties—including nullable artifact, diagnostic, coverage-gate and cleanup-diagnostic values—are emitted even when null. Optional init-only values with null/default omission are absent when unset, and the schemas reject their explicit null/default spellings, including `Inputs: null`, `MaximumFiles: null`, null provenance or patch digests, zero optional timings, and false `CombineProfiles` or `CooperativeChildLifetime`. Enum values are strict named strings, never numbers or numeric strings. These schemas validate shape, not semantic graph closure, digest authenticity, policy authorization or file custody; the runtime reader remains authoritative. Producer implementation/image/fixture identity is supplied by host registration and is not a serialized schema field.

1.1 is a coordinated-upgrade candidate until released-reader compatibility is proven. Upgrade all coordinated tools, readers and hosts before committing 1.1 inputs. No new release version or minimum-version claim is implied here; the actual supported released-reader matrix remains a pre-freeze requirement.

## CLI walkthrough

### Small real fixture

Use Bash, Git, .NET SDK 10.0.402, and a clean AppSurface checkout containing the [two-project consumer fixture](../tools/ForgeTrust.AppSurface.Evidence.Validation/fixtures/consumer/Consumer.slnx). The fixture exercises real coverage and JUnit reporting; its tests create no detached work. Its cooperative promise applies to this fixture, not automatically to your application. Set `APPSURFACE_VERSION` to one exact coordinated candidate version and `CANDIDATE_FEED` to the reviewed feed containing that version. This feature is not supplied by an older released tool. The [package acceptance tool](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md) verifies candidate archive and assembly provenance independently.

From that checkout, create a separate fixture repository and keep all reports and stores beside it:

```bash
APPSURFACE_SOURCE="$PWD"
WORK_ROOT=$(mktemp -d)
FIXTURE="$WORK_ROOT/consumer"
RUN_ROOT="$WORK_ROOT/runs"
RECEIPT_STORE="$WORK_ROOT/receipts"
export APPSURFACE_ARTIFACT_HOME="$WORK_ROOT/coverage-management"
mkdir -p "$FIXTURE" "$RUN_ROOT"
git archive HEAD:tools/ForgeTrust.AppSurface.Evidence.Validation/fixtures/consumer | tar -x -C "$FIXTURE"
cp "$APPSURFACE_SOURCE/global.json" "$FIXTURE/global.json"
dotnet tool install --tool-path "$WORK_ROOT/tool" ForgeTrust.AppSurface.Cli   --version "$APPSURFACE_VERSION" --add-source "$CANDIDATE_FEED"
CLI="$WORK_ROOT/tool/appsurface"
if [ -f "$CLI.exe" ]; then CLI="$CLI.exe"; fi
cd "$FIXTURE"
printf '**/bin/\n**/obj/\n' > .gitignore
cat > NuGet.Config <<'XML'
<configuration><packageSources><clear/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
XML
```

Write this complete `evidence.policy.json` in the fixture. It includes every source, project, shared build file, lockfile, tool selection and policy file consumed by this standalone workload:

```json
{
  "id": "consumer-reuse", "version": "1", "conservativeProfileId": "local",
  "profiles": [{
    "id": "local", "scope": "Targeted", "resources": [],
    "producers": [{
      "id": "coverage", "kind": "coverage", "version": "1",
      "requiredResources": [], "assertionIds": ["coverage/complete@1"],
      "artifactSlots": [{
        "logicalName": "coverage-reports", "relativeRoot": "coverage",
        "mediaType": "application/octet-stream", "required": false,
        "maximumBytes": 268435456, "maximumFiles": 128
      }],
      "timeoutSeconds": 240,
      "coverageGate": { "minLinePercent": 95, "minBranchPercent": 85, "tolerancePercent": 0 },
      "coverageArtifactRetention": "managed",
      "coverageExecution": { "configuration": "Release", "parallelism": 1, "testResults": "junit", "cooperativeChildLifetime": true },
      "inputs": {
        "include": ["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/packages.lock.json", "Consumer.slnx", "NuGet.Config", "global.json", "evidence.policy.json", ".gitignore"],
        "exclude": ["**/bin/**", "**/obj/**"]
      }
    }],
    "obligations": [{
      "id": "coverage", "riskClass": "coverage", "rationale": "Run both consumer projects and meet coverage thresholds.",
      "requiredProducerIds": ["coverage"], "requiredAssertionId": "coverage/complete@1"
    }]
  }],
  "rules": [{ "id": "all", "pattern": "**", "profileId": "local" }]
}
```

Establish this copied fixture's own lock state before its stable baseline. The initial restore intentionally updates the copied locks for the standalone project context; subsequent execution is locked:

```bash
dotnet restore Consumer.slnx --use-lock-file --force-evaluate
printf '<Project><PropertyGroup><RestoreLockedMode>true</RestoreLockedMode></PropertyGroup></Project>\n' > Directory.Build.props
dotnet restore Consumer.slnx --locked-mode
git init --quiet
git add .
git -c user.name='Evidence fixture' -c user.email='evidence@example.invalid' commit -m 'test: establish evidence fixture'
"$CLI" evidence explain --policy evidence.policy.json --path Consumer.Alpha/AlphaDecision.cs   --solution Consumer.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/explain"
"$CLI" evidence run --policy evidence.policy.json --path Consumer.Alpha/AlphaDecision.cs   --solution Consumer.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/first"
"$CLI" evidence run --policy evidence.policy.json --path Consumer.Alpha/AlphaDecision.cs   --solution Consumer.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/second"
"$CLI" evidence verify "$RUN_ROOT/second/evidence-manifest.json" --plan "$RUN_ROOT/second/evidence-plan.json"
```

Expected: explain reports a miss without creating the receipt store; the first run executes both test projects, reports `Passed`, and publishes a receipt. The second reports producer `Reused`, claim `LocalReusedComplete`, and eligibility `Informational`; it restores complete reports under `second/coverage/` without executing those projects again. The integrity check passes, while adding `--for-gate` must reject the reused manifest. Keep the emitted plan, manifest, summary and reports when diagnosing any different result. Receipt ineligibility is a fresh fallback, not a hit. The two-to-five-minute adoption target is unmeasured until a complete walkthrough records reading, setup, first execution and restoration separately; it is not a numeric release gate.

### Your application

After a clean locked restore and a stable fixture commit, point `APPSURFACE_ARTIFACT_HOME` at a new per-run scratch home and select an owned receipt store outside the fixture repository. Keep each invocation's `--output` directory fresh. First resolve and inspect the candidate, then run the same selected solution twice:

```bash
appsurface evidence explain --policy .appsurface/evidence.policy.json --path src/Orders/SubmitOrder.cs \
  --solution ./App.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/explain"
appsurface evidence run --policy .appsurface/evidence.policy.json --path src/Orders/SubmitOrder.cs \
  --solution ./App.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/first"
appsurface evidence run --policy .appsurface/evidence.policy.json --path src/Orders/SubmitOrder.cs \
  --solution ./App.slnx --reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/second"
appsurface evidence verify "$RUN_ROOT/second/evidence-manifest.json"
```

With a compatible workload and complete custody, the first run collects fresh evidence and publishes a receipt; the second restores the producer result and reports local informational completion. Inspect manifest provenance and publication diagnostics, not elapsed time alone. A normal `verify` establishes manifest integrity; `verify --for-gate` must refuse a reused claim. `--verify-reuse` deliberately runs a complete fresh audit and only atomically replaces a matching receipt after success:

```bash
appsurface evidence run --policy .appsurface/evidence.policy.json --path src/Orders/SubmitOrder.cs \
  --solution ./App.slnx --reuse --verify-reuse --receipt-store "$RECEIPT_STORE" --output "$RUN_ROOT/audit"
```

An edit or commit outside the declared input set should preserve a candidate. A comment-only edit inside an included file should make it fresh and publish a new key after successful complete execution. Do not change the fixture's stable baseline commit after editing included lockfiles: restore with `--locked-mode` first and commit the resulting lock state before measuring. Keep machine-specific logs and measurements in ignored scratch; this guide is the reusable procedure, not a performance claim.


### Admission failure and safe retry

The coordinator records the whole fresh key set before exposing a child channel or invoking a resource factory. If admission fails partway through, it joins any admission-only channel and clears only this invocation's execution markers while retaining audit intent and quarantine. Listener creation failure means no new child can enroll; it does not invent an unknown child. A client that was admitted and then lost its completion channel remains unknown and keeps its authority. A store write that cannot confirm compensation remains conservative and reports unavailable.

A prepared host also preserves its producer registrations and transferred application scope when admitted child
settlement is unknown. A completed or disconnected IPC handler does not prove the child stopped. This holds when
a producer raises a fatal failure: the exception propagates without disposing resources that live child work may
still use. Stop and verify the old producer environment before explicitly releasing the retained consumer owners
and selecting a fresh store/domain; disposal alone cannot clear the old execution authority.


`EvidencePreparedRun.DisposeAsync` requests cancellation before joining an active resource factory, including
when `RunAsync` has no caller cancellation token. Factories must observe their supplied token and clean partial
construction before throwing. Disposal still waits for the actual factory task and owned cleanup; cancellation
does not permit early resource release or clear unresolved execution authority.

If a factory returns its scope after cancellation or disposal won the host-admission handoff, the framework
retains and disposes that scope without starting readiness or producers. Admission and disposal share one
handoff; disposal after admission joins the already-owned run. The [prepared lifecycle inventory](evidence-prepared-lifecycle.md)
maps these ownership transitions, failure outcomes, authority-release conditions, and regression tests.

## Migrating an existing prepared host

Move application construction into the prepared factory. In this migration example, `plan`, `producer`, `implementationIdentity`, and `resourceIdentities` are existing consumer values. `CreateBuilderFor(requiredIds)` is a consumer composition helper: it returns an unstarted builder containing exactly those declared resources and no others. The framework does not infer that composition. Health-condition names must match your declarations; use the [HTTP readiness adapter](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#http-readiness-adapter) when health alone is insufficient.

```csharp
await using var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
{
    registration.AddProducer(producer, implementationIdentity);
    foreach (var (resourceId, identity) in resourceIdentities)
        registration.AddResource(resourceId, identity);
}, new EvidenceHostOptions
{
    Reuse = true,
    RepositoryRoot = repositoryRoot,
    ReceiptStore = receiptStore,
    ArtifactDirectory = artifactDirectory,
});

var manifest = await prepared.RunAsync(async (requiredIds, cancellationToken) =>
{
    var builder = CreateBuilderFor(requiredIds);
    var application = await EvidenceAspireApplication.StartAsync(builder, cancellationToken);
    try
    {
        var readiness = requiredIds.Select(id => application.CreateHealthReadiness(id, id)).ToArray();
        return new EvidenceHostResourceScope(readiness, application);
    }
    catch
    {
        await application.DisposeAsync();
        throw;
    }
});
```

Register identities for every selected resource, including unused ones, before preparation. The factory is skipped when no fresh producer needs resources, so an all-hit run builds no application. Otherwise, return exactly `RequiredResourceIds`; the framework owns the returned scope through producer/child settlement, disposal, input revalidation and publication. The factory retains partial-construction cleanup if it throws. Use `prepared.ArtifactDirectories` instead of constructing producer paths. Keep the eager `Create` API for existing fresh-only composition. The [package-only HTTP host proof](../tools/ForgeTrust.AppSurface.Evidence.Validation/EvidenceReusePackageProof.cs) provides a complete executable example and asserts a first factory call followed by zero warm factory calls.
