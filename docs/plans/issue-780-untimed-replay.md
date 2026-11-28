# Untimed EvidenceHost package replay

[Issue #780](https://github.com/forge-trust/AppSurface/issues/780) uses the ordinary CI reference from [PR #960](https://github.com/forge-trust/AppSurface/pull/960). The [replay workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/evidence-profile-replay.yml) checks out that exact fixture, feeds the original PR base and head to the existing validator, and runs the package job on a GitHub-hosted runner. It also rederives the coverage and native plans to confirm that both select zero producers.

The package job preserves the action versions, restore, package prerequisites, validation and artifact upload from the [ordinary package gate](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-gate.yml). Its differences are the pinned checkout and explicit PR input, followed by a separate receipt upload. The replay does not publish packages or affect PR required checks.

Dispatch the workflow manually after it is on the default branch. The rollout branch has a narrow push trigger for the initial hosted proof. Use the exact replay run and attempt when collecting artifacts. A successful job provides a replay observation. The verifier must independently match that observation to the ordinary reference before it establishes parity. Keep timing admission closed while any identity, environment, plan, job, step or proof requirement differs.

## Verify an exact replay attempt

Run the repository verifier from the checkout that contains it. It uses Python 3 and the authenticated GitHub CLI. Supply the locally retained ordinary reference for PR #960, its qualified SHA-256, and the reviewed commit that defined the replay workflow. The report destination must be new so retries preserve earlier results.

```sh
python3 .github/scripts/verify_evidence_profile_replay.py \
  --ordinary-reference "$ordinary_reference" \
  --ordinary-sha256 ef83f594244f3b02a757ef67d908f91abb60b2c727a7e6910503145009f3e0be \
  --run "$replay_run" --attempt "$replay_attempt" \
  --workflow-sha "$replay_workflow_commit" --report "$report_path"
```

The verifier checks exact provider attempt and archive identities, the deploy revision, the original and replay runner image, effective SDK and tooling, pnpm cache observation, selected steps, scoped plans, package manifest and declared proof bytes. It compares proof roles and command outcomes. Fresh executions can have different generated package versions, manifest digests and artifact hashes. Each run's archive and proof bytes must still verify against its own retained evidence.

Exit status zero means this untimed replay matched the qualified reference. Exit status one records a mismatch or missing evidence and leaves parity unverified. The report always records zero admitted timing samples. A replay that needs a different image, checkout identity, or restored cache state must qualify a matching ordinary reference before its observation can establish parity. Ordinary-run logs and archives must still be available to independently verify the report.

[Shared evidence policy and validation](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#shared-policy-and-ci)
