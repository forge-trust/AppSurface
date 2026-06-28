# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.1`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.RazorWire` now includes passive auth projection TagHelpers over `AppSurfaceAuthResult`, and `ForgeTrust.RazorWire.Auth.AspNetCore` connects those helpers to host-owned ASP.NET Core policies without making RazorWire own authentication or endpoint enforcement.

## Included in the next coordinated version

### Release and docs surface

- `ForgeTrust.RazorWire` documents auth projection helpers, including `rw:auth-view`, `rw:auth-gate`, `rw:permission-gate`, `rw:login-link`, and `rw:logout-button`, with a paired endpoint-enforcement example and DevAuth marker guidance that keeps local fake personas separate from reusable UI projection.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
