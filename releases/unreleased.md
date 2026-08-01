# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.5`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Theming` and the explicit `ForgeTrust.AppSurface.Web` Razor adapter now provide immutable semantic theme pairs with native System/Light/Dark browser output. Docs is part of the MVP: its default `AppSurfaceDark` path can consume the shared pair while preserving its public density, chrome, Graphite compatibility, short-hex overrides, static published-tree output, and internal token ownership. RazorWire styling remains limited to generated form-error nodes and uses the nonce-bearing critical stylesheet under strict CSP. Package discovery, quickstart, diagnostics, Docs migration, test commands, and deliberate non-goals ship with the feature; user preference persistence, tenant selection, shared Graphite, remote packs, and adoption telemetry remain separate policy work.
- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here.

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.
