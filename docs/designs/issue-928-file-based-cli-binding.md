# Explicit Config mappings for file-based CLI commands

Status: APPROVED · Builder mode · November 20, 2026 · AppSurface at `3f3126a5` · [Issue #928](https://github.com/forge-trust/AppSurface/issues/928)

## Decision

Start with approach B. A contributor declares a normal CliFx switch and explicitly associates it with an AppSurface.Config logical key. Pilot a command-local attribute beside the switch declaration. The attribute supplies only the key. CliFx owns the switch name and argument parsing. The pilot owns one operation that returns fully validated settings to the handler. The existing ConfigKey attribute describes configuration types. The pilot attribute is internal, and its spelling can change. [Console command authoring](https://github.com/forge-trust/AppSurface/tree/main/Console/ForgeTrust.AppSurface.Console#command-authoring-with-clifx-3) · [ConfigKey contract](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/ConfigKeyAttribute.cs)

Approach A, a hand-written merge per command, would repeat the ordering decision. Approach C, generating switches from settings types, would expand the framework’s responsibilities before a second command demonstrates the need.

## Binding contract

| Explicit switch | Config key | Unscoped environment name | Default |
| --- | --- | --- | --- |
| `--port` | `PwaVerify:Port` | `APP_SURFACE_WEB_PWA_PORT` | 5055, on the child |
| `--evidence-path` | `PwaVerify:EvidencePath` | `APP_SURFACE_WEB_PWA_EVIDENCE` | `examples/web-pwa-install/pwa-verify-v3.json` in the checkout |
| `--startup-timeout-seconds` | `PwaVerify:StartupTimeoutSeconds` | `PWAVERIFY__STARTUPTIMEOUTSECONDS` | 60 |

1. Preserve whether each switch was supplied. An explicit `--port 5055` wins even when JSON supplies a different port. Invalid explicit input fails before the child starts. An omitted switch resolves through Config’s existing providers, then the default if the key is missing. A provider failure remains an error. Use the existing environment mappings for the two legacy names. [Logical keys and environment mappings](https://github.com/forge-trust/AppSurface/blob/main/guides/config-logical-keys.md)
2. Use nullable command inputs for the three optional switches. Resolve missing scalar inputs through `IConfigManager`, then construct one typed settings object. Run native .NET DataAnnotations on that final object. Use property constraints for single values and `IValidatableObject` for relationships. Config’s independent startup checks still validate provider registrations and secret descriptors. [Config manager](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/IConfigManager.cs) · [Config startup validation](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/ConfigCompositionStartupValidator.cs)
3. Port accepts 1–65535, timeout accepts positive seconds, and an evidence path must name a file. Resolve relative user paths from the caller’s working directory. Resolve settings files beside the script using the Config file-location contract. Document the verifier’s active environment and its matching appsettings file separately from the child’s Development environment. Help can name keys using metadata. Credentials use typed secret destinations, and one-invocation actions such as `--apply` require explicit input. [Config file locations](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/DefaultConfigFileLocationProvider.cs) · [Typed secrets](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md)

## What the pilot must prove

- **Portability.** Run `dotnet run --file scripts/web-pwa/verify.cs` on native Windows, Linux and macOS through permanent path-filtered CI. Verify the child owns the requested port before trusting its HTTP responses. Include a case with an unrelated listening process. It must survive, and the verifier must fail. Check manifest readiness, the status endpoint, schema-v3 combined evidence, preservation of failed-report exit codes, and child cleanup on success, timeout, failure and cancellation. Remove both wrappers and update callers only after all three platforms pass. [PWA example](https://github.com/forge-trust/AppSurface/tree/main/examples/web-pwa-install) · [Core host port handling](https://github.com/forge-trust/AppSurface/blob/main/ForgeTrust.AppSurface.Core/AppSurfaceStartup.cs#L140)
- **Contributor experience.** Use checkout project references for the repository pilot and a pinned-package example that runs outside the checkout. Show JSON, environment and flag precedence, invocation from a different directory, a path with spaces, and help without secret reads. Use a synthetic secret only in the package example. The earlier macOS probe demonstrated package composition and precedence. Native lifecycle verification is still outstanding.
- **Cross-property validation.** Evaluate `canary poll` on paper against its existing scheduling predicate. At a five-minute timeout, a configured 500 ms interval plus explicit `1s` must pass at 300 attempts. A configured one-second interval plus explicit `999ms` must fail at 301 with `ASCAN401` before any poll. Preserve the constructor guard for direct callers. The independent review identified early validation of the configured pair as a false-rejection risk. The request factory already checks the same predicate before construction. These mixed-source cases have yet to run. [Canary rule](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/CanaryPollCommand.cs#L649) · [Diagnostic contract](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli.Tests/CanaryPollTests.cs#L179)

## Inventory and follow-up

Reconcile tracked automation, embedded workflow steps, build and package scripts, examples, fixtures and exclusions at a named commit. Record purpose, callers, SDK availability, platform evidence and a reasoned disposition. Publish the inventory and decision guide under `guides/` and link from `CONTRIBUTING.md`. Rank heavier internal and public CLI candidates. A public candidate needs a task someone can perform outside this checkout. The pilot, inventory and guide ship through the repository. Public package changes and heavier migrations require follow-up decisions.

## The assignment

During pilot review, give a contributor the package example in a clean directory and ask them to add one configurable setting. Have them predict the value when JSON and an explicit flag disagree, then run it. Record where they needed to repeat the key, precedence rule or validator. Use that record and the two canary cases to decide whether a reusable API would remove work.

<!-- gstack:office-hours:concerns:start -->
## Reviewer Concerns

Disposition: UNREVIEWED

Stop: UNREVIEWED

Unavailable reason: "Document reviewer returned no sealed verdict. Its required file read/write tools were unavailable under the review prompt."

Findings below are retained from the last completed review; the current document remains unreviewed.

No unresolved findings.
<!-- gstack:office-hours:concerns:end -->
