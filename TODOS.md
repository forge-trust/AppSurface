# Deferred Work

## Theme pairs follow-on

### Persisted user theme preference

- Priority: P2
- Why deferred: Browser preference selection can ship without storage. Adding cookies or local storage requires a defined precedence order, privacy model, cache behavior, CSP posture, and no-flash contract.
- Start here: Build a policy adapter above `ForgeTrust.AppSurface.Theming`; do not add request or user state to the neutral package.

### Tenant-to-theme selection policy

- Priority: P2
- Why deferred: Tenant mapping is application data and authorization policy, not a shared semantic palette concern.
- Start here: Define an application-owned selector that chooses a registered pair before the Web document factory runs, with cache and startup-validation rules.

### Shared Graphite pair

- Priority: P3
- Why deferred: Existing `GraphiteDark` is a Docs compatibility preset. It becomes a shared pair only after an accessible light branch passes the full Docs visual matrix.
- Start here: Add a Graphite light branch in the Docs adapter, prove static/browser parity, then decide whether another package needs the shared ID.

### Theme packs and runtime discovery

- Priority: P3
- Why deferred: Remote or generated packs add distribution, versioning, trust, and compatibility policy before configuration-backed pairs have proved insufficient.
- Start here: Revisit after at least two independent package-owned adapters need reusable externally supplied palettes.

### Theme-pair adoption measurement

- Priority: P3
- Why deferred: The MVP must ship complete reference, migration, diagnostics, and release documentation first. Product telemetry or surveys need an explicit privacy posture and a stable public package before they can answer whether onboarding is working.
- Start here: After release, measure documentation completion, time to a successful consumer fixture, and the distribution of `ASTHEME*` diagnostics without collecting pair values, CSP nonces, or app-specific extension settings.
