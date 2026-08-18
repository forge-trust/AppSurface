<!-- appsurface:unreleased-entry section="included" -->

### Coverage cleanup

- [`appsurface coverage clean`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-clean) lets maintainers preview and then remove private coverage artifacts left by test runs. Its default mode cleans only files in an AppSurface-owned output directory; `--all --root .` provides a separate, explicit sweep of exact `TestResults` directories in one worktree. Both modes require `--apply` to delete, the broad sweep keeps its scan root, and neither mode follows linked paths, so unrelated build outputs and link targets remain outside the command's reach.
