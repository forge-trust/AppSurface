# Completed protected-run evidence staging

[`completed_run.py`](completed_run.py) binds a completed protected qualification attempt to its fixed provider job set and fixed raw artifact archives. It returns only a `CompletedRunStage` with source, job, and archive hashes plus private staged ZIP filenames. The result is transport metadata. It is not a qualification verdict, source-ownership proof, native-acceptance claim, admission, or Trusted support record.

## Call contract

`resolve_completed_run(event_raw, environment, checkout_head, current_capture, workflow_bytes, token, stage_directory, total_timeout=1200)` is intended for a later protected `workflow_run` finalizer. The caller obtains `event_raw` from that runner's `GITHUB_EVENT_PATH`, `token` from its read-only `GH_TOKEN`, and the independent checkout values from the exact completed run head. Before calling, the caller must use the existing `capture-source.py ... --verify` procedure against that checkout. `workflow_bytes` must contain exactly the qualification workflow and reusable runtime workflow read from that checkout. The module independently compares both workflow bodies with GitHub's contents API at the completed run head and checks each body against the full source capture.

The resolver additionally checks the repository, workflow name/path/id, protected-main dispatch, completed-success state, run attempt, head repository, and all source-bound job rows. The required set is the existing `entrypoint.REQUIRED_RUNTIME_JOBS` (35 runtime leaves plus the protected-context check) plus the final partial-retention job. Every selected job must be completed successfully for the same head and attempt.

The stage retains these exact validated provider fields in immutable
`CompletedJob` records: `name`, `id`, `head_sha`, `run_attempt`, `status`, and
`conclusion`. Existing positional `CompletedRunStage` construction remains
compatible and defaults `jobs` to an empty tuple; that legacy shape is not
sufficient for consumers that need job-name-to-ID attribution. Call
`require_closed_jobs(stage)` before selecting a fixed job ID. It rechecks the
37 required names, unique IDs, equality with `stage.job_ids`, and common head,
attempt, completed status, and successful conclusion, returning the immutable
job tuple in deterministic name order. These metadata facts bind provider job
records to this staged run; they do not independently prove source execution,
protected admission, native acceptance, or qualification.

## Fixed inventory and bounds

`expected_artifact_names` is a closed 37-name inventory derived from the existing workflows: the parent controls, 14 original C1 selector artifacts, C1 aggregate, C2 producer, C3 application and lifecycle, two account variants, 14 measured C1 segments, the unchanged coverage-gate output, and the partial qualification artifact. The provider artifact list must contain exactly this set. Each item is selected by its exact run/attempt-bound name and must be unexpired, associated with the same run/head, and carry a lowercase SHA-256 digest. The downloaded raw ZIP is streamed to an exclusive mode-0600 file and hashed before the function returns; no ZIP member is opened here.

The shared `github_parent` request and archive limits are not changed. This module uses a separate, finite staging budget: one 20-minute total deadline, a 15-second cap per HTTP request, at most 155 requests (six fixed API reads plus at most four redirect hops for each of 37 downloads, with one request of headroom), 512 MiB total declared and received archive bytes, and role-specific staging ceilings: 8 MiB for parent-control and partial-qualification records, 8 MiB for original selector artifacts, 24 MiB for each measured C1 segment, 16 MiB for the C1 aggregate, C2, C3 application/lifecycle, 12 MiB for each account artifact, and 32 MiB for the coverage-gate artifact. These are deliberately stricter new staging ceilings; an oversized current artifact fails closed rather than increasing the shared transport limit or falling back to a larger allowance. The theoretical sum of all role maxima is 584 MiB, so the full inventory's declared sizes must fit the 512 MiB stage cap before any download begins; received bytes are charged against that cap too. Each ZIP is streamed to an exclusive mode-0600 file, then read back and rehashed before it is returned. The open descriptor and the final named path are both checked as the same regular single-link inode. On an error, cleanup unlinks the destination only if it still names that inode; a substituted path is rejected and left untouched. Redirects are HTTPS-only and use the existing restricted GitHub artifact-host allowlist; the bearer token is sent only to `api.github.com`, never to redirect targets. The stage directory must already be a fresh absolute mode-0700 directory owned by the caller.

## Next validation step and limits

After staging, a separate source-owned validator must safely inspect the fixed archive members and invoke the existing C1 aggregate/measurement-chain and Native.Tests data validators against those bytes. This module intentionally does not parse archive members or decide whether the full evidence chain passed. The current in-progress `entrypoint.collect` remains partial and must not consume this object as a callback or accepted receipt. No finalizer workflow is included in this preparation.

The downloaded archives are untrusted data even after their raw digests match provider metadata. Keep the ZIP member allowlist, size limits, source/head binding, and exact validator inputs in the later validation layer. Never execute code extracted from an archive.
