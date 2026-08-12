<!-- appsurface:unreleased-entry section="taking-shape" -->

- [`appsurface coverage run`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) now starts exclusive projects before ordinary parallel batches. This prevents resource-sensitive suites from waiting for unrelated work to drain; `--schedule longest-first` continues to order only the remaining non-exclusive projects.
