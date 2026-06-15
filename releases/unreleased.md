# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.3`. It stays provisional until the next tag is cut.

## What is taking shape

- AppSurface CI can now prove the default full-solution coverage lane through the `appsurface coverage run` command, running from source via `dotnet run --project`, without waiting on matrix fan-in workflows.
- Patch coverage gates can now use Git refs, unified diff files, or piped unified diff text without forcing full-history checkout.
- Reader-intent relevance for AppSurface Docs search.
- Product-readiness evaluation now has a report-first lab and an Aspire AppHost verifier that proves local Postgres product-state persistence without claiming Durable Task backend ownership.
- More trustworthy AppSurface Docs search typing for multi-word queries.
- CodeQL JavaScript/TypeScript analysis now ignores the intentionally malformed parser-decision fixture while the docs tests still exercise the same parse-failure bytes.

## Included in the next coordinated version

### Release and docs surface

- AppSurface CI coverage now dogfoods the `appsurface coverage run` command, running from source via `dotnet run --project`, for the default full-solution lane. The lane preserves the existing merged Cobertura, managed JUnit, slow-test diagnostics, Codecov, and `coverage gate` evidence paths while `scripts/coverage-solution.sh` keeps legacy compatibility for grouped runs, group listing, merge-only runs, `TEST_GROUP`, and `BUILD_SOLUTION=false`.
- Patch coverage gates now accept exactly one diff source: `--diff-base` for local Git history, `--diff-file` for CI-produced unified diff artifacts, or `--diff-stdin` for piped unified diff text. External diff artifacts are bounded, empty external diffs are treated as valid empty patches, malformed non-empty external diffs fail closed before coverage evaluation, and JSON plus Markdown reports record patch diff provenance.
- `appsurface coverage run` now supports `--test-results junit` for AppSurface-managed top-level JUnit artifacts. `--slow-test-diagnostics` implies managed JUnit results and writes diagnostics from those files; `junit` is the only managed result format in this release, with TRX/TUnit compatibility reserved for #491.
- Package artifact validation now runs a pre-publish consumer proof for the packed `ForgeTrust.AppSurface.Cli` tool. The proof installs the local artifact into an isolated clean fixture, runs `coverage run`, `coverage merge`, a passing `coverage gate`, and an intentionally failing `coverage gate`, then writes `coverage-cli-consumer-proof.md` so publish manifests are blocked when packaged consumer behavior breaks.
- Coverage runs now emit `slow-test-diagnostics.md` and `slow-test-diagnostics.json` next to the merged coverage artifacts. The diagnostics rank project and JUnit test-case timings, preserve best-effort parser warnings without changing coverage exit codes, record metadata completeness, and report diagnostic aggregation overhead in seconds and as a percent of elapsed runner time at diagnostics generation.
- AppSurface Docs search now hydrates MiniSearch candidates from the normalized docs payload and applies deterministic reader-intent ranking before both sidebar and full-page rendering. Exact title, path, source, alias, keyword, and entry-point matches stay protected; broad task queries prefer reader-facing guides; explicit API/internal filters override broad-task boosts; and contributor/internal docs are demoted unless the query asks for them directly.
- The package chooser now resolves the public Intelligence package start-here link inside hosted AppSurface Docs by keeping top-level Intelligence READMEs inside the standalone harvest boundary.
- `examples/product-readiness-lab` now gives adopters a SaaS-shaped local evaluator whose readiness report is the primary artifact. The paired `examples/product-readiness-lab-apphost` verifier starts local Postgres, probes the public readiness endpoint, and fails unless product/domain state becomes `proven-locally`; Durable Task worker/client startup, hosting, timers, late-event handling, and storage-provider boundaries stay documented as host-owned.
- AppSurface Docs search now preserves multi-word spacing while readers type, so pausing after a separator in either the full-page search workspace or sidebar search no longer joins words together.
- RazorWire now includes a hybrid-hosting guide for split-origin deployments that serve exported static pages from one origin while Cloud Run or another container host serves RazorWire streams, islands, and lazy anti-forgery forms from a live origin.
- Package validation now treats redistributed package payload provenance as an enforced release gate. `verify-packages` reads `packages/third-party-payloads.yml`, proves notice, generated-first-party, or audited coverage for suspicious payloads, and renders package report rows with notice paths, evidence kind, version source, and suspicious payload counts.
- AppSurface Docs parser-decision fixtures now keep the deliberate malformed JavaScript sample under a non-JavaScript suffix so repository-level JavaScript/TypeScript scanners do not treat the negative parser test as product source.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
