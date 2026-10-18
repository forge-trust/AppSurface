# Packaged EvidenceHost deployment

The [C# runtime](README.md#process-roles) is deployed inside the `ForgeTrust.AppSurface.Cli` NuGet tool. `Evidence.Supervision` is internal, nonpackable implementation code bundled into that tool; consumers do not install a separate supervisor service or Python runtime. The CLI package must contain `ForgeTrust.AppSurface.Evidence.Supervision.dll`, Contracts, Planner, Coverage, the pinned `Tmds.DBus.Protocol.dll`, and `reportgenerator/net10.0/ReportGenerator.dll` beneath its `tools/net10.0/any` image. The installed `.deps.json` and runtime configuration are part of the protected inventory too.

Use the ordinary build selection `EvidencePrivateQualification=None` (the default). Private N/C2/C3/C3Sdk images intentionally reject `Pack`; their generated registration tables, rendezvous and mutation hooks are validation fixtures and must never be deployed as production packages. A regular `dotnet publish` output does not automatically include the protected ReportGenerator subdirectory; prefer the tool package, or explicitly prepare and audit every deployment dependency. There is no package-cache reporter fallback.

## CLI and SDK consumer check

From a clean source checkout with locked CLI/SDK restore complete, run:

```bash
bash scripts/evidencehost-package-smoke.sh /absolute/fresh/package-smoke
```

The [smoke wrapper](../../scripts/evidencehost-package-smoke.sh) runs bounded [package preparation](../../.github/issue779-checkpoint4/package-smoke.py). The directory must be fresh and absolute. It packs the ordinary CLI, checks the package dependency files, installs the exact local prerelease into a private tool directory, executes ordinary and reserved-role help, and requires unsupported/unprivileged supervisor and omitted-mode failures. It then packs Contracts, Planner, Coverage and Aspire, restores a fresh .NET 10 consumer from the local feed and NuGet's external dependencies, writes/locks the consumer dependency graph, builds and loads the public SDK types. It changes no repository lock file, threshold or audit policy.

The private `receipt.json` records source revision, package SHA-256, command/log digests, exact consumer dependency versions, exit status, and `tool_image`, the installed managed image directory. That path is deployment input for the [native producer fixture](../../.github/issue779-checkpoint2/README.md), which independently copies/audits it and runs the actual same-product C# root/worker/producer controls. This data field grants no admission. The installed tool is not writable by the restricted worker after protected staging.

The smoke driver has one cumulative nine-minute process bound; callers should also apply their own external bound. Failure retains its fresh directory and original nonzero result. A smoke pass proves installation and dependency consumption, not kernel isolation, physical cleanup, native application registration or Trusted acceptance. The SDK consumer compile/load check has no application callback or admission path. The separate [application workflow](../../.github/issue779-checkpoint3/README.md) proves both callers with explicitly private compiled registrations.

## SDK deployment boundary

Applications reference `ForgeTrust.AppSurface.Evidence.Aspire`; its declared package dependencies provide Planner, Contracts, Coverage, CliWrap and the pinned Aspire hosting range. The SDK does not create an independent privileged root from an in-process callback. A supported protected run still requires the separately armed same-product CLI image and its authenticated socket, root-owned deployment, retained metadata, original job bounds and accepted compiled application definition. See [application execution](application-execution.md) and the [cutover/claim boundary](../../docs/evidence/issue779-csharp-runtime-cutover.md).

Before starting .NET, the OS bootstrap must sanitize startup hooks, clear unreviewed probing/profiler configuration, pin the actual runtime executable and entry assembly, arm independent lifetime enforcement and consume a fresh generation. Installing a package does none of those things. Windows/macOS, direct in-process workers and arbitrary long-lived hosts do not become supported by restoring the SDK. Missing dependencies and unsupported owner/platform facts fail closed; do not repair them with a public backend, environment bypass or fabricated descriptor.
