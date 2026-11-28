# Read package-only pull request evidence

The [package gate](../.github/workflows/package-gate.yml) uses the shared [evidence policy](../.github/evidence.policy.json). For a package-only change, open the package-gate run for the PR head and download its validation diagnostics artifact from the matching attempt.

- Check that the retained plan selects `package-checks` and that the manifest records `TargetedComplete`. The package producer checks the package index, verifies generated documentation, and validates package archives and installed consumers.
- Check the sibling coverage and native plans before interpreting their results. A verified `NoEvidenceRequired` manifest means the policy selected no producers for that workflow. The workflow can still run its regular setup and independent checks.
- Keep the workflow run and attempt with the plan and manifest when citing the result. A later attempt can resolve a different PR base or use a different runner image.

Use the [repository validation guide](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#shared-policy-and-ci) to reproduce the selected checks locally. Use the [package readiness dashboard](readiness.md) to review package-index evidence and the [release guide](../releases/README.md) for publication status.
