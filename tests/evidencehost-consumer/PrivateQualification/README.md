# Private resource-backed qualification

This separately compiled variant implements the human-approved [qualification proposal](authorization-proposal.md).
The user approved that proposal on 2026-10-03. Its immutable SHA256 is
`ab4028706a095510f4808ed32119d8063fb07937e6b959cf2e0b7fec5a028881`; its final pending-status paragraph records
the earlier proposal state, not the later approval. The approval permits this private variant and measured
execution only, and does not enroll a consumer proof or enable Trusted execution.
It is absent from the production build. Production catalogues, the accepted-consumer proof registry and
Trusted allowlist stay empty. Qualification cannot enroll a result or issue a gate/release claim.

## Compiled admission and result handling

The root workflow renders [`ContractsBinding.cs.in`](ContractsBinding.cs.in) with the exact current run,
workflow, frozen source, tested subject, canonical policy/plan and application entry/catalogue digests.
It hashes the generated sources and finished tooling independently before arming. Tool hashes are root-pinned
after compilation; they are not self-referential literals embedded in the assembly being hashed.

The private admission exception requires the actual root-authenticated `EvidenceLinuxWorkerSupervisor`, the
same armed run, a complete exact compiled plan and descriptor, three distinct fixed UIDs and five distinct
fixed GIDs, the closed bundle/catalogue and seven capability bounds. Only resource-backed Observation may
use this path. Public factories, local writers, JSON, environment flags, fake supervision and verifier
substitution cannot enable it.

Private completed admission carries an internal, non-serialized issuer marker. Structural finalization can
record a genuinely successful resource-backed execution, but always emits `ClaimKind=None` and
`Eligibility=None`; normal production Observation/Trusted semantics are unchanged. Structural verification
can reconstruct only the exact compiled private plan and None/None result; verifying data grants no capability.

The ordinary protected CLI worker uses the shared restricted coverage registration. A second binary built
with the compile-only `EVIDENCE_PRIVATE_QUALIFICATION_HOST` constant calls the real public
`EvidenceHostBootstrap.RunAsync(request)` with complete compiled declarations and the same sealed producer.
Its metadata callback declares Aspire health resource names; the root-owned adapter establishes actual
readiness. Neither binary installs an application factory or invokes a test-core entry.

## Ordering and immutable bindings

Only trusted metadata/build preparation precedes arming. Subject restore, MSBuild/assets evaluation,
AppHost/DCP startup, readiness and producer work begin through the actual armed root broker and its existing
deadline/stage budgets. The launcher retains bundle and artifact handles, distinct account reservations,
bounded scratch/capability/output grants, actual kernel UID/GID/cgroup facts and root-authenticated UDS HTTP
readiness. All producer/application groups and pumps must join before collection and strict cleanup.

The metadata formatter is data-only and uses existing test-friend visibility to produce canonical bytes from
a verified baseline, not runtime registration. Its output must be reviewed and embedded into private source
before compilation. An unresolved template, changed source, build hash, bundle byte/mode, policy/plan digest,
identity, deadline, HTTP peer or cleanup state must fail closed. No filename, assertion or generated receipt
is a substitute for measured execution. This preparation document makes no native success or coverage claim.

## Root build and consumer procedure

[`prepare.py`](prepare.py) requires the workflow's exact clean source commit and a fresh root-owned workspace.
It binds every tracked source byte and Git executable mode, archives pristine `5d325bb` for the
[metadata formatter](../PrivateQualificationMetadata/README.md), and builds the pinned 13.4.4 AppHost/DCP payload.
It never evaluates the [subject project](../PrivateQualificationSubject/README.md). Canonical policy, plan,
entry and catalogue bytes come from the actual C# metadata APIs. The generator expands source templates and
builds a normal protected CLI and a separate binary with the compile-only `QualificationHostEntry` property.
Source/bundle/template and finished-tool hashes remain distinct; tool hashes are selected after compilation.
App deployment directories are sealed `0555`, declared files are `0444` or `0555`, and later worker permission
changes cannot alter the separately pinned deployment copy. Each trusted build command has one bounded
process group, disabled server reuse, finite kill/reap/group-absence checks and an exclusive durable command
receipt. Nonzero exits, oversized logs, expired deadlines and incomplete ownership remain failures.

[`run-qualification.py`](run-qualification.py) calls the actual launcher's `launch_with_completion` for both
entries, passing the compiled profile/application/entry selection and one 900-second deadline per fresh run.
Admission/start/collection/cleanup/stopping allowances are 30/120/60/60/5 seconds; resource readiness is
45 seconds and the one coverage producer has 180 seconds. Stopping is reserved inside cleanup. These are
finite reviewed inputs, not resets of an active allowance. Fixed account creation and pending ownership are
documented in [root bindings](root-bindings-README.md).

The [retained-output collector](retained-output-README.md) duplicates the actual completion handles and reads
only the closed CLI/Host output layouts. After those duplicates close, completion must close its handles and
strictly delete retained accounts. Only then are raw files saved privately and structurally evaluated, with
the actual private CLI's `evidence verify` against the independently compiled expected plan. Saved diagnostics
are not uploaded authority. The controller requires genuine Passed execution, Ready resource, Passed producer,
matching report bytes and closed obligations while forcing `ClaimKind=None` and `Eligibility=None`.

## Failure retention and coverage limits

The root controller emits only a fixed terminal category. Its private archive selects fixed root-owned,
single-link `0600` files beneath retained `0700` diagnostic directories. It includes bounded build/verifier
command receipts, at most 16 KiB per log tail, bounded launcher journal and subject prefixes, and complete
small collected plan/manifest/report copies. The index distinguishes tails, complete files and oversized
omissions; capture failure cannot change the original execution result. Archive data is canonical USTAR,
at most 4 MiB with 3 MiB retained payload, copied to a runner `0700` directory with `0600` files. It supplies
diagnosis, not acceptance. Private portable controls use actual file/process data and metadata seams; they
do not fabricate admission, kernel observations or a protected positive result.

The [workflow template](workflow.yml.in) must be expanded with the frozen source commit in a subsequent
normal harness commit before push. Native execution remains unverified until its exact run, head, workflow,
source/tool/bundle bindings, receipts and artifacts are independently checked. This first qualification
attempt is uninstrumented. External CLI/Host execution is not automatically included in VSTest collection;
future honest measurement must preserve PDB document/line/branch correspondence and feed actual shards
before the unchanged solution coverage gate. Passing qualification alone does not establish that gate.
