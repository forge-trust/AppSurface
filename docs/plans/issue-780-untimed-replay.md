# Untimed EvidenceHost package replay

[Issue #780](https://github.com/forge-trust/AppSurface/issues/780) uses the ordinary CI reference from [PR #960](https://github.com/forge-trust/AppSurface/pull/960). The [replay workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/evidence-profile-replay.yml) checks out that exact fixture, feeds the original PR base and head to the existing validator, and runs the package job on a GitHub-hosted runner. It also rederives the coverage and native plans to confirm that both select zero producers.

The package job preserves the action versions, restore, package prerequisites, validation and artifact upload from the [ordinary package gate](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-gate.yml). Its differences are the pinned checkout and explicit PR input, followed by a separate receipt upload. The replay does not publish packages or affect PR required checks.

Dispatch the workflow manually after it is on the default branch. The rollout branch has a narrow push trigger for the initial hosted proof. Use the exact replay run and attempt when collecting artifacts. A successful job provides a replay observation. The verifier must independently match that observation to the ordinary reference before it establishes parity. Keep timing admission closed while any identity, environment, plan, job, step or proof requirement differs.

[Shared evidence policy and validation](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#shared-policy-and-ci)
