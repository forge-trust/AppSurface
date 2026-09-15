<!-- appsurface:unreleased-entry section="included" -->

### Structural coverage classifier pilot

- The CLI coverage test suite now contains an isolated, fail-closed Roslyn structural-line classifier pilot. It records
  audit evidence for one narrow auto-property shape while proving that the existing coverage calculation, CLI outcomes,
  and report artifacts are unchanged. The classifier does not run in [`appsurface coverage gate`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate), add a policy option, or change coverage obligations; any production use requires a separate review after broader evidence collection.
