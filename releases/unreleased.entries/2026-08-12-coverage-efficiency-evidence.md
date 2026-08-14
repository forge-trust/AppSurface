<!-- appsurface:unreleased-entry section="taking-shape" -->

### Coverage maintainer evidence

- AppSurface maintainers can dispatch the private [coverage-efficiency workflow](https://github.com/forge-trust/AppSurface/actions/workflows/coverage-efficiency.yml) to capture the full coverage lane’s exact step wall time, resolved scheduler evidence, JUnit, diagnostics, logs, coverage reports, and an environment manifest in one retained artifact. This is an investigation-only evidence path: it preserves the existing [coverage-efficiency evidence guidance](../../README.md#coverage-efficiency-evidence-for-issue-728), keeps ordinary pull-request validation unchanged, and requires comparable before/after samples before any #728 time claim or isolation-boundary change.
