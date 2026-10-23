# Issue 806 coverage attempt 2

Date: October 4, 2026

The coverage attempt exited 1 (`ASCOV120`) because `ForgeTrust.RazorWire.IntegrationTests` had two failures:

- `AppSurfaceDocsStyleTokenPlaywrightTests.FixedAppSurfaceLightPreset_ShouldMatchCommittedMacVisualBaselines`
- `AppSurfaceDocsGraphiteThemePairPlaywrightTests.GraphiteDocs_ShouldMatchCommittedDesktopVisualBaselines`

Both report visual-baseline mismatches. RazorWire baselines were regenerated during the attempt after these tests ran, so treat these failures as stale-baseline results and rerun the two tests against the regenerated baselines after active changes settle. `ForgeTrust.AppSurface.PackageIndex.Tests` passed 1,832, skipped 3, and had no failures.

The attempt-2 aggregate is **95.06% line coverage (143,563/151,021)** and **89.01% branch coverage (48,473/54,457)**. This is diagnostic data from a failed attempt, not a gate pass.

The largest already-measured residuals among issue-806 sources are:

| Source | Uncovered lines |
| --- | ---: |
| `PackageIndexGenerator.cs` | 46 |
| `DurableTemplateArtifactContract.cs` | 29 |
| `PackageArtifactValidation.cs` | 17 |
| `Program.cs` | 12 |
| `DurableTemplateConsumerProof.cs` | 4 |

The report gave source-level counts, not names of uncovered methods. Several other relevant files changed during or after the coverage run, so their reported residuals are stale and should not drive patches. No code patch is justified from this failed run alone. Next, after the active template-gate, timing, cache, cleanup, and native changes settle, run focused coverage on these stable-source residuals and verify whether any gaps remain; refresh the two visual tests against the regenerated baselines.
