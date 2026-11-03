# Completed source preflight

[`completed_source.py`](completed_source.py) supplies a read-only source-identity preflight for a future protected `workflow_run` finalizer. The named workflow, `.github/workflows/evidencehost-completed-validation.yml`, does not exist or run yet. This module does not add or enable it. The API currently provides source binding only; it does not download evidence, invoke the [completed-run resolver](completed-run-README.md), replay validators, run builds, or qualify a workflow.

## API

```python
validate_completed_source(
    event_raw,
    environment,
    validator_capture,
    source_capture,
    source_root,
    native_source_root,
    *,
    deadline,
) -> SourceBinding
```

`event_raw` is the exact workflow-run event JSON. `environment` is the runner environment mapping. `validator_capture` is a complete source capture for the validator checkout; its source commit must match both `GITHUB_SHA` and `GITHUB_WORKFLOW_SHA`. `source_capture` describes the completed predecessor source named by `event_raw`. Both captures use the existing `issue779-checkpoint2-source-v1` shape and may be at most 1 MiB.

`source_root` is fixed to the existing Linux runner checkout path used by the protected gate. `native_source_root` is a separate, caller-owned directory with mode `0700`. The validator checkout is derived from this module’s own path. All three roots must be canonical, owned by the current user, and disjoint. The caller remains responsible for holding their directory identities stable across subsequent validation; the finalizer controller should pin them before replay and recheck those pins afterward.

`deadline` is an absolute monotonic deadline. The preflight accepts at most 120 seconds from entry and does not renew the deadline. Direct Git checks use the absolute `/usr/bin/git`, fixed argument vectors, no optional locks, no shell, per-operation timeouts bounded by the remaining deadline, and a 4 KiB output-file ceiling. The captured verifier also invokes Git by name, so the module temporarily replaces process environment with a closed allowlist (`PATH=/usr/bin:/bin`, `HOME=/nonexistent`, fixed locale, disabled system/global Git config, and disabled replacement refs), then restores the complete original environment in `finally`. No inherited credentials, `GIT_*` overrides, or dynamic-loader overrides reach verifier subprocesses. This environment change is synchronous and process-wide; do not call either API concurrently with unrelated environment-sensitive threads.

## Checks and returned facts

The function requires the canonical repository, repository ID, protected `main` ref, `workflow_run` event type, and exact future workflow reference `forge-trust/AppSurface/.github/workflows/evidencehost-completed-validation.yml@refs/heads/main`. It checks that the event identifies the existing completed protected qualification workflow on `main`, with a completed successful run and same-repository head. It distinguishes that completed head from the current validator head and requires the completed head to be an ancestor of the validator head.

The fixed current C1 binding verifier checks the complete validator checkout, completed-source checkout, and native-source checkout against their captures, including Git object IDs, file modes, and real file bytes. The captures must contain the existing qualification/runtime workflow, source-binding, and capture-tool paths. The validator capture must also include the future workflow file. No module or script is imported from either target checkout. Preflight requires every checkout to have no tracked, untracked, or ignored changes.

The frozen `SourceBinding` records `repository_id`, `run_id`, `run_attempt`, the completed `head_sha`, `source_tree`, and `source_capture_sha256`, plus `validator_head_sha`, `validator_tree`, and `validator_capture_sha256`. Its `admission_created`, `native_acceptance`, `qualification_complete`, and `gate_eligible` fields are always false and cannot be supplied to its constructor.

## Rechecking after replay

A later controller may call:

```python
verify_completed_source(
    binding,
    validator_capture,
    source_capture,
    source_root,
    native_source_root,
    *,
    deadline,
)
```

This verifies that the same captures and commits still describe the same checkouts. It continues to require the validator checkout to be entirely clean, including ignored files. The completed and native target checkouts may contain ignored build outputs after replay, but tracked changes and untracked files still fail. The caller must retain and recheck the original three directory descriptors/identities as a separate custody measure. The helper neither expands the capture nor imports target code.

## Limits and pitfalls

This result is only an immutable identity record. It is not proof that the completed source was accepted, that a producer ran, that artifacts passed validation, that native execution succeeded, or that gates passed. The future workflow reference is a fixed context assertion for that future caller; it is not evidence that the workflow exists, is enabled, or has run. Do not use `SourceBinding` as an admission input or a Trusted support record. Continue with the existing [completed archive inventory](completed-validation-README.md) and fixed source-owned validators only after this preflight succeeds.
