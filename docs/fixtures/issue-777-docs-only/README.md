# Immutable docs-only gate fixture

This fixture contains a tiny Git bundle, its exact fixed-options source diff, a frozen canonical [AppSurface policy snapshot](policy.json), and full commit IDs in [`fixture.json`](fixture.json). It supports a copyable, local rehearsal of the [revision-bound Evidence CLI](../../../Cli/ForgeTrust.AppSurface.Cli/README.md). It is a **planning and host** fixture, not a PR check: the final [trusted verifier](../../../tools/ForgeTrust.AppSurface.EvidenceGate/README.md) still needs live current-head and subject-job authority. The frozen policy is rehearsal input only; a real gate must load policy from its protected base revision.

From a normal AppSurface checkout, run:

```bash
tmp_dir="$(mktemp -d)"
git clone -q docs/fixtures/issue-777-docs-only/repository.bundle "$tmp_dir/repository"
base_revision="$(python3 -c 'import json; print(json.load(open("docs/fixtures/issue-777-docs-only/fixture.json"))["baseRevision"])')"
head_revision="$(python3 -c 'import json; print(json.load(open("docs/fixtures/issue-777-docs-only/fixture.json"))["headRevision"])')"
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- evidence explain \
  --policy docs/fixtures/issue-777-docs-only/policy.json \
  --repository "$tmp_dir/repository" \
  --base-revision "$base_revision" \
  --head-revision "$head_revision" \
  --diff-file docs/fixtures/issue-777-docs-only/source.diff \
  --gate-mode \
  --output "$tmp_dir/explain"
dotnet run --project tools/ForgeTrust.AppSurface.EvidenceGate -- run \
  --plan "$tmp_dir/explain/evidence-plan.json" \
  --policy docs/fixtures/issue-777-docs-only/policy.json \
  --repository "$tmp_dir/repository" \
  --output-dir "$tmp_dir/host"
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- evidence verify \
  "$tmp_dir/host/evidence-manifest.json" \
  --plan "$tmp_dir/host/evidence-plan.json" \
  --policy docs/fixtures/issue-777-docs-only/policy.json \
  --repository "$tmp_dir/repository"
```

The expected selected profile is `documentation-only`, with `NoEvidenceRequired` and `PullRequestGate` on the host manifest. The `explain` and host plan digests must match. Reversing the commit IDs, changing a diff byte, or substituting the policy must fail before an eligible final verdict. This fixture uses no remote PR and therefore does not establish current-head check association or permit [ruleset activation](../../evidence-gate-rollout.md).

The [non-claiming shadow workflow](../../../.github/workflows/evidence-gate-shadow.yml) runs this same three-command rehearsal on a branch checkout and asserts matching plan digests and the explicit empty claim. The [fixture integrity test](../../../scripts/tests/test_evidence_gate_fixture.py) separately verifies the bundle, frozen policy and fixed-options Git diff against their recorded digests.
