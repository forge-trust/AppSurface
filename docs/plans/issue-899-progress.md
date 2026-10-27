# Issue #899 implementation and validation

Scope: [ASCOV201 volume identity diagnosis](https://github.com/forge-trust/AppSurface/issues/899).
Decision and native observations: [domain identity design](../designs/issue-899-domain-identity.md).

- Work branch: `codex/make-it-so-899-ascov201`, based on `ebb562be618c2649c1ef0ade12095e56468882c7` (`origin/main`). Initial worktree was clean.
- Schema v1 cannot prove continuity after a native identity change. Exact marker comparison, permissions, retained handles, no-follow traversal, locking, registry bounds and inventory validation remain required. No marker/schema migration, in-place rebind or recovery deletion is implemented.
- Bounded C# diagnostic reasons distinguish identity/volume mismatch, missing/invalid/unsupported marker, invalid nested registry, invalid policy, private access and unavailable native capability. Historical bytes/identifiers are not printed.
- Explicit C# / installed CLI `coverage storage new-domain --output ABSOLUTE [--apply]` previews or initializes a separate fresh private root. It never opens the old configured domain, transfers registrations/pins, changes configuration, or adopts an existing destination. Partial initialization stays preserved.
- Review identified the Windows idempotent directory-create race. Native `CreateDirectoryW` now atomically refuses an existing destination while setting the same private owner/ACL; the Unix no-replace primitive remains intact. Real-host race tests and managed native-call decision tests are separate.
- Native macOS ARM64 focused lifecycle tests: 6 passed; expanded identity/new-domain focus: 45 passed. An earlier broad affected storage run passed 985 tests with 5 existing Windows-only skips. Later review changes require a fresh broad/full run.
- A disposable APFS remount with changed device allocation reproduced equal inode / differing device and C# `VolumeIdentityMismatch` refusal. This does not establish the cause of the preserved user's original store. Both temporary image mounts were detached.
- The installed candidate consumer proof reuses the existing exact-package installation validation and exercises preview/apply, status, refusal, pinned archive/current-byte preservation using explicit temporary domains. Full execution pending. The CLI prints a generic failure summary to stderr after failed JSON commands; the proof asserts that existing contract.

## Gate record

The unchanged gate is:

```sh
UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 APPSURFACE_ARTIFACT_HOME=/private/tmp/issue899-gate-store ./scripts/coverage-solution.sh
```

The process settings avoid build servers retaining sandbox restrictions. The isolated domain protects the preserved user's live store. Test inventory, instrumentation, exclusions, aggregate/patch 95% line / 85% branch thresholds, 0.5-point tolerance, Codecov patch mode and native evidence requirements are unchanged.

Attempt 1 started 2026-10-27 08:48 UTC, output `/private/tmp/issue899-coverage.log`. An installed-test stderr assertion was corrected after this attempt started, based on the actual CLI refusal observed in the disposable APFS experiment; attempt 1 is diagnostic until a fresh unchanged-command run passes on the final code.

PR not created. Current-head CI not started; CI repair rounds 0/3 and CI waiting 0/60 minutes. Local coverage waiting is separate from the post-PR CI budget.

## Review / QA record

Enhance invocation (budget 4): pass 1 fixed missing-marker classification consistency while retaining the existing IOException contract; pass 2 fixed the Windows idempotent-create race and missing capability-reason documentation. Native in-host reviewer examined production diff with fixture summaries only; that narrower fixture review is supplemented by the parent's full test/helper read. Final review remains pending final verification. No outside checkpoint has run. The gate-integrity scanner listed zero C# hunks because `languages_unlisted=csharp`; direct review confirms no existing assertions, gates, exclusions, instrumentation or CI lanes were removed or relaxed.

Browser QA is not applicable to this CLI/storage change. Closest user-facing proof is the installed exact-package C# test plus the actual CLI APFS experiment. The latter observed status complete across same-tuple remount, refused genuine device reassignment, and exercised preview/apply into a separate fresh domain under a degraded-store environment.

Attempt 1's Aspire default installed-consumer journey emitted `stage=integrity`, `detail=retention`, exit 0. Source inspection establishes that stage is advanced to `integrity` only after coverage/gate/retention validation succeeds; `detail` remains from the preceding check. The failure therefore came from the source/settings integrity comparison. The default nested fixture's `git status` observes the repository state. Source/docs changed concurrently in this diagnostic attempt, so a frozen-source rerun is required; no integrity assertion or retention contract is relaxed.

The native test fixtures were also corrected for Windows: retained replacement tests opt into rename sharing to exercise binding detection; copied metadata is written using private core creation APIs so broad default inherited ACLs cannot mask marker-identity refusal. The new-domain test class is named `CoverageStorageCommandNewDomainTests`, matching the existing native CI filter without changing that filter. Managed Windows create-call tests inspect the pinned SECURITY_ATTRIBUTES and descriptor and assert ERROR_ALREADY_EXISTS remains a refusal.

Enhance pass 3 completed with no new production findings after the Windows publication fix; native reviewer scope was production code plus test summaries. Parent inspected all new regression files and installed helper. Loop stopped before its four-cycle checkpoint. Final focused command (identity, `CoverageStorageCommandNewDomainTests`, lifecycle) passed 49/49 with 0 skips, 0 build warnings/errors in `/private/tmp/issue899-focus-freeze.log`. Final formatting and relative documentation target checks passed. Browser QA remains inapplicable; installed package proof/full gate and native Windows/Linux evidence remain pending. Candidate is now frozen for the next full unchanged-command gate; no source/docs writers may overlap its source-integrity probes.
