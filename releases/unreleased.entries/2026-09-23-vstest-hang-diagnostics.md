<!-- appsurface:unreleased-entry section="included" -->
### Coverage hang diagnostics

- [`appsurface coverage run`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) forwards each `--test-argument` as one literal VSTest token, including tokens that begin with `-`. In fail mode, the supported collector and MSBuild drivers request a bounded, no-dump VSTest hang sequence and report scoped, safe “last started test” observations in `timings.json` and the terminal summary. The first-party [Evidence coverage producer](../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md) uses the same private policy within its producer deadline. See the [#815 diagnostic and migration note](../issue-815-vstest-hang.md) for timing, artifact, and manual-override details.
