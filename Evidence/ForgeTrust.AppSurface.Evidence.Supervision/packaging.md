# Packaged EvidenceHost deployment

The [C# runtime](README.md#process-roles) is deployed inside the `ForgeTrust.AppSurface.Cli` NuGet tool. `Evidence.Supervision` is internal, nonpackable implementation code bundled into that tool; consumers do not install a separate supervisor service or Python runtime. The CLI package must contain `ForgeTrust.AppSurface.Evidence.Supervision.dll`, Contracts, Planner, Coverage, the pinned `Tmds.DBus.Protocol.dll`, and `reportgenerator/net10.0/ReportGenerator.dll` beneath its `tools/net10.0/any` image. The installed `.deps.json` and runtime configuration are part of the protected inventory too.

Use the ordinary build selection `EvidencePrivateQualification=None` (the default). Private N/C2/C3/C3Sdk images intentionally reject `Pack`; their generated registration tables, rendezvous and mutation hooks are validation fixtures and must never be deployed as production packages. A regular `dotnet publish` output does not automatically include the protected ReportGenerator subdirectory; prefer the tool package, or explicitly prepare and audit every deployment dependency. There is no package-cache reporter fallback.

## CLI and SDK consumer check

From a clean source checkout with locked CLI/SDK restore complete, run:

```bash
bash scripts/evidencehost-package-smoke.sh /absolute/fresh/package-smoke
```

The [smoke wrapper](../../scripts/evidencehost-package-smoke.sh) runs bounded [package preparation](../../.github/issue779-checkpoint4/package-smoke.py). The directory must be fresh and absolute. It packs the ordinary CLI, checks the package dependency files, installs the exact local prerelease into a private tool directory, executes ordinary and reserved-role help, and requires unsupported/unprivileged supervisor and omitted-mode failures. It then packs Contracts, Planner, Coverage and Aspire, restores a fresh .NET 10 consumer from the local feed and NuGet's external dependencies, writes/locks the consumer dependency graph, builds and loads the public SDK types. The same consumer resolves the actual installed CLI's starter policy, checks canonical supported-schema round trips, rejects an unsupported contract version, requires the legacy Boolean/status-enum builder to reject with `ASEVD400`, and verifies that an explicit Observation request without an authenticated socket fails before configuration. It changes no repository lock file, threshold or audit policy.

The same installed CLI also follows the [documented planning journey](../../start-here/evidencehost.md#the-first-five-minutes): it creates the marked starter, reports protected facts as unverified, resolves an explicit changed path, preserves the starter on repeat invocation, and rejects local Observation execution without authenticated control. The legacy `--observation-only` alias must produce the same refusal as explicit Observation; conflicting Trusted/legacy mode selection must reject `ASEVD401` before work. The driver reads the actual generated policy, verifies the conservative profile and exact planning file set, and hashes those files into `planning_journey` in the receipt. These are planning and migration checks; the native producer and application controls remain the execution proof. The automated trace is not independent human usability research.

The private `receipt.json` records source revision, package SHA-256, command/log digests, exact consumer dependency versions, exit status, and `tool_image`, the installed managed image directory. That path is deployment input for the [native producer fixture](../../.github/issue779-checkpoint2/README.md), which independently copies/audits it and runs the actual same-product C# root/worker/producer controls. This data field grants no admission. The installed tool is not writable by the restricted worker after protected staging.

The producer workflow also retains the 22 original command logs, the three generated starter files, the plan, the summary and their original receipt in `package-smoke-details`. The [bounded evidence helper](../../.github/issue779-checkpoint4/package_smoke_evidence.py) validates every actual file against that receipt before copying it into a fresh directory. Each leaf is limited to 1 MiB and the complete set to 4 MiB; the existing 128-file/32-MiB producer-artifact limit still applies. Links, hard links, changed files, missing logs, unexpected command decisions and occupied destinations reject. Directory mode is `0700` and file mode is `0600`. A partial copy is not a passing smoke result.

After downloading the matching source-bound artifact, revalidate its raw installation and planning outputs with:

```bash
python3 -B .github/issue779-checkpoint4/package_smoke_evidence.py verify --output /absolute/artifact/package-smoke-details --source FULL_SOURCE_COMMIT
```

`retain --work /absolute/fresh/package-smoke --output /absolute/fresh/details --source FULL_SOURCE_COMMIT` is the corresponding workflow preparation operation. Both commands require absolute paths and the exact 40-character source commit. Verification compares raw command-log hashes, six expected rejection decisions, starter/plan/summary hashes and the planning-only messages. It cannot establish runtime isolation or human usability. Earlier artifacts that retained only hashes cannot be retroactively revalidated as raw output.

The smoke driver has one cumulative nine-minute process bound; callers should also apply their own external bound. Failure retains its fresh directory and original nonzero result. A smoke pass proves installation and dependency consumption, not kernel isolation, physical cleanup, native application registration or Trusted acceptance. The SDK consumer has no accepted admission or application startup; its configuration callback must remain uncalled on the authentication rejection. The separate [application workflow](../../.github/issue779-checkpoint3/README.md) proves both callers with explicitly private compiled registrations.

## SDK deployment boundary

Applications reference `ForgeTrust.AppSurface.Evidence.Aspire`; its declared package dependencies provide Planner, Contracts, Coverage, CliWrap and the pinned Aspire hosting range. The SDK does not create an independent privileged root from an in-process callback. A supported protected run still requires the separately armed same-product CLI image and its authenticated socket, root-owned deployment, retained metadata, original job bounds and accepted compiled application definition. See [application execution](application-execution.md) and the [cutover/claim boundary](../../docs/evidence/issue779-csharp-runtime-cutover.md).

Before starting .NET, the OS bootstrap must sanitize startup hooks, clear unreviewed probing/profiler configuration, pin the actual runtime executable and entry assembly, arm independent lifetime enforcement and consume a fresh generation. Installing a package does none of those things. Windows/macOS, direct in-process workers and arbitrary long-lived hosts do not become supported by restoring the SDK. Missing dependencies and unsupported owner/platform facts fail closed; do not repair them with a public backend, environment bypass or fabricated descriptor.
