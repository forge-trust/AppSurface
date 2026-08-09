# Package README release-guidance templates

This file is the canonical source for generated `## Release Guidance` regions in
package READMEs. The
[`PackageIndex maintainer guide`](./README.md#release-guidance) defines variant
eligibility and marker grammar. Follow the `generate`/`verify` workflow.
Keep each finite variant within its exact marker pair. Each variant must contain
exactly one `{{PackageChooserUrl}}` and one `{{ReleaseHubUrl}}` token; the
renderer expands both into canonical absolute GitHub Markdown links so the
README remains usable after NuGet packages are separated from the repository
tree.

<!-- markdownlint-disable MD024 -->
<!-- appsurface-release-guidance-template: default begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the {{PackageChooserUrl}} and {{ReleaseHubUrl}}
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance-template: default end -->

<!-- appsurface-release-guidance-template: apphost begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the {{PackageChooserUrl}} and {{ReleaseHubUrl}} for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance-template: apphost end -->

<!-- appsurface-release-guidance-template: experimental begin -->
## Release Guidance

This package has an explicitly experimental or publication-held contract. Do not
treat it as a normal prerelease install; use the {{PackageChooserUrl}} and
{{ReleaseHubUrl}} for the current publication decision, proof requirements, and
migration guidance.
<!-- appsurface-release-guidance-template: experimental end -->
<!-- markdownlint-enable MD024 -->
