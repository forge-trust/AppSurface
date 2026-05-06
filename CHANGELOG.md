# Changelog

This changelog is the compact release ledger for Runnable. The monorepo ships in unison, so each tagged version covers packages, CLI tooling, examples, and docs-facing behavior from this repository together.

## Reading guide

- `Unreleased` tracks the next coordinated version and points to the living release note.
- Future tagged sections will use the shape `## x.y.z - YYYY-MM-DD`.
- Every tagged section will link to a matching narrative release note in [`releases/`](./releases/README.md).
- Breaking or behavior-changing updates must record migration guidance here and in the matching release note.

## Unreleased

- Narrative release note: [Upcoming release note](./releases/unreleased.md)
- Upgrade policy: [Pre-1.0 upgrade policy](./releases/upgrade-policy.md)
- Authoring workflow: [Release authoring checklist](./releases/release-authoring-checklist.md)

### Added

- Runnable now has a repo-level release contract: a public release hub, an unreleased proof artifact, a pre-1.0 upgrade policy, and a tagged-release template for future versioned notes.
- RazorWire now has a package-level generated UI design contract that defines ownership scope, data-attribute and CSS custom-property styling surfaces, accessibility expectations, override levels, and anti-patterns for package-owned UI.
- RazorDocs pages can now render a top-of-page trust bar from structured metadata so release notes and upgrade guidance can show status, safety context, and provenance without custom page code.
- RazorDocs now supports metadata-driven page wayfinding: harvested outlines, explicit proof-path previous/next links, related pages, and sidebar anchor navigation.
- RazorWire forms now have convention-based failed-submission UX with default form-local fallbacks, server helpers for handled validation errors, development anti-forgery diagnostics, runtime events, and sample coverage.
- The root README now includes a single hello-world web quickstart with an explicit local port and a concrete expected response.
- Runnable now ships GitHub issue templates for bug reports and documentation feedback.
- Runnable Config now supports first-class scalar value validation on `Config<T>` and `ConfigStruct<T>` wrappers with `ConfigValueNotEmpty`, `ConfigValueRange`, `ConfigValueMinLength`, and a `ValidateValue` override for custom rules.

### Changed

- Runnable now treats the whole monorepo as one coordinated release surface. Packages, CLI tools, examples, and docs-facing behavior all roll into the same upcoming version.
- Pull requests are expected to use Conventional Commits titles and to update `releases/unreleased.md` unless maintainers explicitly opt out.
- Markdown-only changes on `main` now trigger the build-and-export workflow so release-note and policy updates publish with the docs surface.
- RazorWire CLI validation errors now include a concrete next command and `razorwire export --help` hint so failed exports are easier to recover from.
- Tailwind development watch mode now logs a warning, not a startup error, when the standalone CLI is unavailable and the app can continue serving existing CSS.
- RazorDocs search now keeps failure recovery markup out of the active search shell until the index actually fails to load.
- The RazorWire MVC sample counter button now has an accessible name while preserving the compact icon-only UI.
- RazorDocs source-path, canonical-path, fragment, and docs-root-prefixed lookups now share one resolver across page details, landing curation, related-page links, and search fallback links.
- RazorDocs landing curation now renders reader-intent `featured_page_groups` instead of one flat featured list.
- RazorDocs authored Markdown pages now use a tighter prose layout while generated API pages keep the wider reference treatment.
- The PackageIndex generator now has a successful `--help`/`-h` path with command and option guidance instead of a bare usage failure.
- The conventional browser 404 page now favors user recovery, including documentation search for missing docs routes and a home link for other missing pages.
- `StartupContext.ApplicationName` is now treated as a display label while `IHostEnvironment.ApplicationName` stays assembly-backed for static web asset manifest discovery.

### Migration

- Runnable has not cut `v0.1.0` yet, so there is no tagged migration guide today.
- Before `v0.1.0`, any breaking or behavior-changing update should record provisional guidance in [`releases/unreleased.md`](./releases/unreleased.md) and move finalized steps into the tagged release note once the version ships.
- Existing `rw-active` forms opt into failed-form request markers and automatic fallback UI by default. Set `options.Forms.EnableFailureUx = false`, `options.Forms.FailureMode = RazorWireFormFailureMode.Manual`, or per-form `data-rw-form-failure="off"` if an app already owns all failure rendering.
- RazorDocs authors using `featured_pages` should migrate to `featured_page_groups`; the old flat field now logs a warning and no longer renders.
- Code that expected custom Runnable labels from `IHostEnvironment.ApplicationName` should use `StartupContext.ApplicationName`; host environment application names now remain tied to assembly identity so ASP.NET static web assets continue to resolve.

## No tagged releases yet

Runnable is still defining its first release boundary. The first tagged section will be added when the project is ready to cut `v0.1.0`.
