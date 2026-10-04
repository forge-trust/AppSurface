# #806 follow-up review — 2026-10-04

**Disposition: reviewed planning remedies PASS; approved plan ready for implementation.** Overall planning remains DONE_WITH_CONCERNS because actual candidate/OS, timing, adopter and doctor evidence is still required. This targeted pass addresses concerns in the [approved design](../designs/issue-806-durable-worker-template.md); it does not replace the earlier full Autoplan or claim implementation completion.

The user requested another review pass after approving final gate D1=A. The pass preserved the three commands/four checkpoints, exact-artifact/SQL identity, restricted authority, passive startup, all original release gates and the approved CE-02 receipt/CE-08 human trial. No feature source was implemented.

## Findings and dispositions

| Concern | Current disposition | Evidence / change |
| --- | --- | --- |
| Earlier CEO S1: provisional receipt/trial status | Resolved | Fresh spec reviewer confirmed approval; current implementation now explicitly requires CE-02/CE-08. Final engineering recheck confirmed consistency. |
| Earlier CEO S2: missing ingress owner/bound | Resolved | BodyReadBudgetSeconds governs the read phase, default 5 seconds and valid integer range 1–300. Default/non-default and deadline/caller-abort tests are required. |
| Fresh P2: configurable setting versus fixed total five seconds | Fixed and rechecked | Deadline uses the configured setting, separate from service/discovery budgets. |
| All-OS smoke lacked real PostgreSQL setup | Design contract resolved; candidate execution required | PackageIndex owns a private native cluster on Ubuntu24.04 x64, macos15 arm64 and Windows2025 x64, exact published-provider provisioning/role SQL and restricted host startup. Acquisition, phase budgets, identity and cleanup are explicit. |
| Blanket no-host-psql wording conflicted with native smoke | Fixed and rechecked | Generated Docker FirstDurableWork uses container psql; native psql is confined to repository-owned OS smoke setup. |
| initdb prompt may prefer an attached terminal/console | Contract clarified; local macOS feasibility PASS | No-console/no-controlling-TTY redirected input twice, fail-closed prompt deadline and no credential argument/file. Windows remains a required runtime check. |
| pg_ctl server may inherit capture pipes or outlive a wait timeout | Contract clarified; ordinary local stop PASS | Private bounded -l log, safe projection, actual owned-process termination and no deletion before verified stop; late-start/timeout regressions remain required. |

## Actual review coverage

| Pass | Actual result | Limits |
| --- | --- | --- |
| Fresh native specification review | DONE_WITH_CONCERNS: S1/S2 resolved, one new P2 found | Bounded approval/body-contract review; no platform experiment. |
| First native engineering attempt | UNAVAILABLE / PARTIAL | Read spec and landed reference; primary web output was not inspectable. Its completion receives no source-verification/PASS credit. |
| Final native engineering recheck | PASS for reviewed remedies | Full frozen input and locally supplied official source excerpts; no generated candidate or Windows/Linux execution. |
| Parent primary-source inspection | Completed | GitHub runner inventories, Homebrew formula, PostgreSQL initdb/prompt source and pg_ctl docs; inventories must still be checked in actual jobs. |
| Initial native experiment | UNAVAILABLE | macOS sandbox denied shmget before server startup. |
| Approved native experiment retry | PASS on local macOS arm64 | [Native feasibility report](issue-806-native-postgresql-feasibility.md); narrow setup/auth/stop/cleanup only. |
| Monitoring agent | UNAVAILABLE | Could not access parent process session. Parent subsequently observed terminal exit0 and the complete receipt. |
| Claude Code outside review | Unavailable | No outside-provider completion or cross-model consensus claimed. |
| CEO/design/DX full re-review | Not rerun | Targeted follow-up; earlier phase findings/scores retained, Design remains inapplicable. |

The exact final reviewer input was `94b8899d323641f70242307fc26b7524c803dbbe4998b08ff0db4432075d227a`. The native route requested combo/sub; provider/model identity was not reported. The full final verdict and immutable inputs are saved under [follow-up archive](/Users/andrew/.gstack/projects/forge-trust-AppSurface/issue806-review-followup-20261004T064120Z). The full final verdict follows with line-break spacing normalized; its original /tmp references identify the frozen input. Archived copies are the durable handoff.

# Issue #806 final engineering remedy recheck

**INPUT:** `followup-final 94b8899d323641f70242307fc26b7524c803dbbe4998b08ff0db4432075d227a`
**SHA-256:** verified against `/tmp/issue806-followup-final-spec.md`.
**Disposition:** **PASS — remedy wording is internally consistent against the supplied primary excerpts.** This is a bounded spec-remedy recheck, not a Linux/Windows, generated-candidate, provider/role, or release-matrix pass. The input records those as outstanding release evidence.

## Remedy dispositions

- **Body deadline / earlier fixed-five-second concern — RESOLVED.** Final spec lines 280–281 define `DurableActivation:BodyReadBudgetSeconds`, default 5 seconds, range 1–300, and explicitly isolate it to the pre-service body-read phase. Its configurable value does not alter service or pump budgets. The prior concern no longer conflicts with the design.
- **Generated no-host-psql contract / native OS smoke — RESOLVED.** Line 284 now explicitly keeps the generated Docker-backed `FirstDurableWork` fixture on container `psql` and confines native host `psql` to the separately documented repository-owned OS smoke. Lines 128, 132, and 171–183 keep the original three commands and strict pinned-16.5 container proof distinct. No remaining scope contradiction found.
- **CE-02 / CE-08 approval status — RESOLVED.** Lines 4, 251, 256, 276, 288, and 289 consistently say D1=A approved the receipt and outside-checkout trial and that they are required. The earlier provisional/pending language is gone. Historical office-hours `UNREVIEWED` at lines 239–245 is explicitly a separate historical review state, so it does not reverse the final gate.
- **`initdb --pwprompt` stdin/console behavior — RESOLVED as a design contract; macOS native feasibility demonstrated.** The supplied PostgreSQL 16.5 source shows `initdb` calls `simple_prompt` twice for `--pwprompt`; `simple_prompt` attempts Windows `CONIN$`/`CONOUT$` or Unix `/dev/tty`, then falls back to stdin/stderr when that terminal is unavailable. Line 181 now requires no Windows console and verifies no controlling Unix TTY, redirected input, and fail-closed prompt/timeout handling. The parent’s safe JSON records a successful local **macOS arm64** PostgreSQL 16.5 `initdb`, authenticated query exit 0, wrong-password query exit 2, fast stop exit 0, stopped status 3, and removal of the owned root. The parent reports the query returned `server_version_num=160005`; the JSON itself records the query exit but not its stdout. The experiment times (initdb 1.522s, start 0.119s, query 0.024s, stop 0.112s) are local experiment timings only, not product-performance evidence.
- **`pg_ctl` startup log / late cleanup — RESOLVED in wording; lifecycle remains an implementation gate.** Line 181 uses `pg_ctl -l <owned-log>` so server stdout/stderr do not inherit the verifier’s captured pipes, and restricts the log to private, bounded, non-uploaded diagnostics. Line 187 acknowledges timeout may leave startup/shutdown running, requires postmaster/process and port observation, forbids removing data before owned termination is established, and fails evidence on ambiguous or unfinished cleanup. These requirements are compatible with the supplied PostgreSQL documentation excerpt; no contradictory cleanup instruction remains.

## Remaining boundary

The macOS arm64 experiment is narrow native-cluster feasibility evidence. It is not the GitHub `macos-15` candidate smoke and does not verify the template, migration/epoch/role recipe, generated host, or three-command proof. Linux and Windows runtime setup, including Windows no-console prompt delivery and teardown, remain untested. The supplied runner excerpts support the stated inventory at capture time (Ubuntu PostgreSQL 16.15; Windows PostgreSQL 17.11/PGBIN; macOS arm64 Homebrew PostgreSQL 16.15 bottle), but the actual jobs must still record resolved binary paths, hashes, versions, and outcomes as the spec requires. No further spec contradiction in the reviewed remedies is blocking this bounded recheck.

The spec’s line 4 status phrase saying the two CEO remedies “lack a confirming recheck verdict” is now stale because this report supplies that verdict. Refresh that status when carrying the verdict into the design record; it does not reopen either remedy.


## Implementation handoff

Use the refreshed [test plan](issue-806-durable-worker-template-test-plan.md). Existing task E1/D2 owns configured body-deadline behavior; E3 owns native setup and every child/cluster ownership failure; E4 binds actual three-OS results at the existing fail-closed publish boundary. C4 must run the approved earlier human trial; it is no longer conditional. The 15-task aggregate remains the same scope.

Before promotion, execute real native smoke on all three actual runner jobs, generated exact-nupkg/feed/FirstDurableWork proof, complete provider/schema/prior-binary/preflight cases, timeout/late-start/cleanup schedules, every generated-boundary test family, five-run clocks, separate adopter certificate and doctor disposition. The local native experiment receives no credit for those gates. Existing solution suites were not run for these planning-only edits.

## Follow-up decision audit

| ID | Decision | Classification | Why |
| --- | --- | --- | --- |
| FR-01 | Configured body-read phase deadline with default/non-default tests | Mechanical clarification | Removes the fresh fixed/default ambiguity without changing service/discovery behavior. |
| FR-02 | PackageIndex-owned private native PostgreSQL setup for each OS ordinary smoke | Mechanical feasibility resolution | Gives existing all-OS startup obligation a concrete owner and fail-closed lifecycle. |
| FR-03 | CE-02/CE-08 are required approved obligations | Mechanical status correction | Carries forward the user's recorded D1=A; selects no new scope. |
| FR-04 | Container/native psql scope, no-console prompt and pg_ctl log/cleanup contracts | Mechanical consistency clarification | Resolves contradictory tool scope and source-backed process pitfalls. |

The original 48 decisions are unchanged. These four follow-up clarifications are recorded separately. Original restore point, close packets, phase task JSONL and historical review records remain intact.

## Parent final readback correction

The native shutdown command synopsis omitted its explicit `stop` action. Final readback corrected it to `pg_ctl -D <owned-data> -m fast -w -t <remaining-seconds> stop`, matching the [PostgreSQL16 pg_ctl stop reference](https://www.postgresql.org/docs/16/app-pg-ctl.html) and the command that actually passed in the local experiment. The native reviewer verdict/input above remains immutable; this single command-token correction was checked by the parent against the official synopsis after that verdict. It changes no shutdown budget, ownership rule, runtime/API behavior or release gate.
