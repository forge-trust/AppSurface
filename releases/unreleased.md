# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.6`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

<!-- appsurface:unreleased-entries section="taking-shape" -->

- PostgreSQL compatibility and evidence floor clarified to 16+ for durable operations; strict verification now uses pinned
  PostgreSQL 16.5 digest `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`.
- Migration workflow now includes `0009_work_contract_discovery.sql` and documents registry-scoped Work discovery through
  `appsurface_durable.discover_work_dispatch(text[], text[], integer)` in place of direct `dispatch` reads.
- Operational docs now explain `ASDUR119` as an in-memory PostgreSQL Work registry-snapshot activation failure, with
  exact custom-registry recovery guidance.

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here as they land.

<!-- appsurface:unreleased-entries section="included" -->

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.

- `#747`: `PostgreSQL` compatibility floor is 16+ and strict default test gate is pinned to PostgreSQL 16.5 digest above;
  historical 17.5 evidence remains preserved in existing JSON manifests.
- `#747`: Added and documented migration `0009_work_contract_discovery.sql` (registry-scoped Work discovery function),
  replacing raw dispatch reads and adding `work_contract_discovery_owner` gate policy for dispatcher visibility.
- `#747`: Operational docs now prescribe canonical migration-owner->role recipe->schema status/epoch checks before enabling
  dispatcher/runtime host; rollout is drain-first, including stopping every pre-`0009` worker before the role recipe removes
  raw dispatcher `dispatch` access.

<!-- appsurface:unreleased-entries section="migration-watch" -->
