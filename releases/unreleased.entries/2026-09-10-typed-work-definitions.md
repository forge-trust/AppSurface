<!-- appsurface:unreleased-entry section="included" -->
### Typed Durable Work definitions

- Add immutable typed Durable Work definitions with ordinary, reconciled and exit-aware bindings. Define contract facts once, then create requests with explicit caller identities, retry overrides and due times. Guarded codec metadata and registry/Flow compatibility preserve existing request fingerprints and legacy registration APIs. See the [migration guide](../../Durable/migrations/typed-work-definitions-v1.md) and [#800](https://github.com/forge-trust/AppSurface/issues/800).
