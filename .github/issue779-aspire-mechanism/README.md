# Private native Aspire mechanism preparation

These ignored drafts are not a workflow change, scheduled run, published artifact or validation result.
They prepare the five cases in `tests/evidencehost-consumer/AspireChild/README.md` on native Ubuntu 24.04.
Every receipt states `trust_claim: false` and `shared_admission: false`. No protected-positive,
provider registration, shared Evidence host, coverage-gate or release acceptance is established.

## Parent handoff before use

1. Obtain Beauvoir's source-clear with verified watchdog acknowledgement/liveness, pump EOF/failure latching,
   and main-only cooperative TERM followed by zero-exit normal/cancel controls. Do not launch before these fixes.
2. Create the reviewed immutable private source commit. It must include the child/resource/controller/tests,
   platform locks and repository build/package configuration. Runtime v4/v5 snapshots omit this new mechanism.
3. Copy `source-binding.json.example` to `source-binding.json`, set the full 40-hex source commit and bind
   every required input listed by the wrapper's `REQUIRED` tuple (a full source snapshot map is also accepted).
   Set `controller_fixes_verified: true` only after review, and set `required_fix_receipts` to the three exact
   strings in the wrapper's `FIXES` set. Retain actual verification receipts in the parent's integration ledger;
   these labels are a launch prerequisite, not proof or Trust authority.
4. Replace `PARENT_PROVIDED_FULL_SOURCE_COMMIT` in `candidate-workflow.yml` with that same SHA.
   Place the wrapper and final binding in the **private harness** at `.github/issue779-aspire-mechanism/`.
   The parent owns any subsequent workflow installation/push/launch. Nothing here performs it.

The workflow follows the existing native snapshot pattern: a separate harness checkout, an exact source checkout,
pinned checkout/setup/upload actions, read-only repository permission, `ubuntu-24.04`, .NET `10.0.x`,
and a 20-minute job timeout. Setup obtains the native Linux SDK; the source pins Aspire SDK and AppHost packages
to **13.4.4**. No Node, pnpm, Docker or production Evidence packages are needed by this isolated mechanism.

## Execution model

Run after the source binding is final, from a disposable native host with passwordless noninteractive sudo:

```sh
python3 -B run-aspire-mechanism.py run \
  --source /absolute/path/to/pinned/source \
  --binding /absolute/path/to/source-binding.json \
  --output /absolute/new/path/to/mechanism-receipts
```

The source commit and hashes are checked before restore, after the zero-warning build, and after execution.
Locked native restore selects `packages.linux-x64.lock.json`; the build uses `--no-restore` and
`UseSharedCompilation=false`. NuGet audit stays enabled. Fresh worker-owned build caches retain writable HTTP
audit cache space; recognized sandbox markers remain unchanged. Restore/build each have a 300-second bound,
bounded private logs, exit capture and warning detection. The build's zero-warning/error summary is required.

The build's `CopyProofPayload` must provide the actual Linux DCP tree and native resource output. The wrapper
checks DCP's ELF64 little-endian x86-64 header, executable mode and hash, records the pinned orchestration package/lock,
canonical dotnet path, SDK version, kernel/systemd and source/run/workflow metadata. It never uses a macOS bundle.

The root helper creates a fresh `/var/tmp/issue779-aspire-prep-*` directory and dedicated non-login subject account
with its own nonroot UID/GID. It copies the bounded controller and built bundle into root-owned preparation space.
For each sequential case, it creates new tool/output fixture directories and installs the built
`NativeHttpResource.dll` as the real managed `tools/protected-tool.dll` with root ownership/read-only mode.
The wrapper inspects and records selected dotnet-path symlink ancestors and its canonical executable. It fails
explicitly if any resolved component is under `/home`, `/root` or `/run/user`; it does not weaken `ProtectHome`
or assume setup-dotnet chose `/usr/share/dotnet`. If the parent selects an installation hidden by that guard,
the parent must first stage the complete real SDK/runtime tree, root-owned, outside those paths and select its
canonical executable. This draft does not perform that staging. DCP copy operations preserve executable bits.
The canonical platform dotnet lives outside denied tool roots and protected home paths. The existing controller
owns the actual restricted AppHost/DCP/resource systemd topology and private HTTP readiness.

Cases run in this order: **normal, readiness-failure, factory-stall, cancel, stuck-descendant**.
Each controller invocation is under GNU `timeout 75s` with a five-second kill reserve; the capture owner adds
an 85-second hard bound. The source controller retains its own 45-second independent watchdog/systemd runtime.
Every case captures its real exit and bounded receipt. Normal/cancel additionally require process exit zero and
no escalation; factory-stall requires escalation. All five require the controller's exact successful mechanism
conditions and absence of the protected output probe. The wrapper independently checks new case cgroups;
unconfirmed exit prevents the next case and prevents account removal. Only the created account/group is removed.
No failed subject scratch or quarantined proof tree is reused.

## Receipts and private diagnostics

- Safe JSON: source binding, native metadata, command exits/counters, five-case summary and terminal decision.
- Build logs: bounded to 4 MiB combined per command, mode 0600, in the separate diagnostics artifact.
- Root diagnostics: controller output is bounded to 256 KiB per case. Existing root-only child stdout/stderr,
  receipt and quarantine files are copied only from this run's newly created proof roots, regular root-owned
  files capped at 1 MiB each. The root helper creates a mode-0600 archive; the runner copies it with a 16 MiB cap
  into a mode-0600 `private-root-logs.tar.gz`. Original `/run` diagnostics stay root-only on the disposable host.
- The workflow uploads safe JSON separately from diagnostic logs/archive. GitHub artifact visibility follows
  repository access; "private" here describes local file protection and separate diagnostic handling, not a new
  independently enforced GitHub audience. Raw diagnostics must not be copied into issue comments/public receipts.

`case-summary.json` requires all five cases; partial execution cannot pass. The terminal decision and every
artifact remain mechanism-candidate observations. `ProtectedGateConsumer` is intentionally not run here: its
synthetic API positive and stdin transport do not establish an actual root-parent gate channel or authenticate
expected plans, facts or artifact bytes. The full protected workflow remains a separate integration.

## Current assumptions and validation boundary

Requires native x86-64 Ubuntu 24.04/systemd 255+, cgroup v2, a canonical .NET 10 runtime outside `/home`, `/root` and `/run/user`,
GNU timeout, standard account tools, and a disposable runner with no parallel `issue779-child-*` proof controllers.
The explicit root setup does not run against real verifier/output roots. Preparation retains private trees for
diagnosis; the disposable runner is discarded after the job. Parent-provided source/hashes and verified fixes are
still pending. Static syntax validation of these drafts is not native execution or build evidence.
