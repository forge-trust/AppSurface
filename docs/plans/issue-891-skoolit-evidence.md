# Issue 891: recovered execution evidence

Status: historical recovery assessment. The owner subsequently authorized the [execution amendment](../designs/issue-891-cloud-run-execution-observation.md#execution-amendment-authorized-on-2026-10-24), which reconciles numeric identity and implicit v1 SQL projection without reducing full configuration parity. Current offline mapping, fixtures and exact SDK evidence are recorded in the [mapping assessment](issue-891-provider-mapping.md); current validation is in the [progress record](issue-891-execution-progress.md). This report preserves what the source search itself established.

This supersedes the missing-capture premise in the [initial feasibility assessment](issue-891-gcloud-feasibility.md). The user explicitly requested a read-only search of Skoolit worktrees and history. No provider request, deployment, submission or mutation was made.

## Search and provenance

- Enumerated 25 existing Git worktrees sharing the Skoolit repository, plus its shared engineering evidence archive and deployment backup directory. Inventoried filenames using `rg`, including ignored/hidden evidence while excluding build/dependency caches.
- Found 52 JSON files containing 521 v1 Execution entries. The entry count includes list responses and repeated captures; it is not a count of distinct executions.
- The substantive raw readbacks were in the ignored engineering archive, principally the October deployment evidence for issues 653 and 735. A separate native history search found parser code, configuration templates and synthetic fixtures in Git, not committed raw provider readbacks.
- Exact local source paths, original hashes and structural observations are retained in ignored evidence indexes under `.gstack/tmp/make-it-so-891/`. Raw source responses were not copied into this repository or printed in chat.
- The best matching pair is a final migration execution readback and its rendered Job definition. Their original SHA-256 values are `74091e348d5c7b373fedcd1ffd3466f80c3710faa9a2f8a42f09ee430e0cff5b` and `5effcd93e12612067c7cf05fa44af53897e590ba2b064e39564a42dc1a8bf5d9`, respectively.
- Provider metadata includes creator client-version annotations such as `576.0.0` and `568.0.0`. Those identify the client that created the resource; they do **not** independently prove which gcloud version later described it. No supported reader-version claim follows from those annotations.

## Pairwise configuration comparison

The following checks compare fields of the recovered execution and intended Job directly. They are an evidence-retrieval check, not a test of the unimplemented AppSurface observer or a proof of a published AppSurface bundle.

| Field comparison | Result |
| --- | --- |
| Execution parent label / intended Job name | Equal |
| Execution / Job namespace | Equal, both numeric |
| Full immutable image | Equal |
| Command | Equal |
| Arguments, treating absent as an empty comparison input | Equal; this comparison does not establish a new allowed omission rule |
| Environment entries | Equal |
| Task count | Equal |
| Parallelism | Equal |
| Retries | Equal |
| Task timeout | Equal |
| Service account | Equal |
| Cloud SQL annotation | Equal |
| Direct VPC annotation | Equal |
| Egress annotation | Equal |

The selected capture has `Completed=True`, an immutable image reference and matching execution generation/observedGeneration. Region and parent Job labels are present. Missing zero counters and absent arguments remain explicit compatibility questions under the original R/D rules; the comparison above does not approve defaults.

## What the captures change

1. **A representative response is available locally.** A new cloud read is no longer needed merely to obtain an example. The prior request for an existing execution and bundle is not the immediate next action.
2. **Region and parent carriers are demonstrated.** The inspected responses contain `metadata.labels["cloud.googleapis.com/location"]` and `metadata.labels["run.googleapis.com/job"]`; v1 must not be rejected merely because region/parent are absent at the root.
3. **Project identity needs an explicit rule.** Every parsed Execution entry has a numeric `metadata.namespace`, while the current AppSurface binding profile accepts project IDs. The original plan forbids guessing ID/number equivalence. The captured service account, Cloud SQL instance or command input cannot silently supply an alias mapping.
4. **The explicit Cloud SQL mount requirement is not met.** None of the inspected entries contains a `cloudsql` or `/cloudsql` volume mount. Some contain unrelated mounts. The selected successful single-container execution and intended v1 Job both omit volumes/mounts while retaining the Cloud SQL annotation. This is useful evidence about the actual representation; it does not prove an explicit mount field that is absent.
5. **No canonical AppSurface bundle was recovered.** Matching legacy Job definitions exist, but the search found none of the exact three AppSurface artifact filenames. Those definitions can inform independently rendered neutral expectations; they must not be relabeled as an AppSurface published bundle.

## Required design reconciliation

The original plan's full-parity decision remains approved. These findings do not authorize reducing configuration proof, inventing an execution field, using today's Job to fill missing captured settings, or accepting a numeric project as an arbitrary project ID.

Before integration, decide how to establish the expected numeric project identity and how to prove the v1 implicit Cloud SQL socket projection. A supported, documented normalization may preserve the semantic proof, but it must be explicit, independently evidenced and reviewed against the current R/D matrix. If that cannot satisfy full parity, evaluate a different read adapter through affected design/engineering review. Merely collecting another similar v1 payload will not manufacture the missing explicit fields.

Both neutral worksheets, negative controls, exact reader-version/OS/argv provenance and named maintainer acceptance remain required. The source readbacks can now ground that work, but no compatibility manifest is certified and no observer, CLI or package validation is claimed complete.
