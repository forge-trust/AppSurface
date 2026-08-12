<!-- appsurface:unreleased-entry section="included" -->

### Delegated agent task approval

- [`ForgeTrust.AppSurface.Auth`](../../Auth/ForgeTrust.AppSurface.Auth/README.md#delegated-agent-task-approval) now gives local agentic harnesses typed contracts for one human-approved workflow transition: a bound action proposal, confirmation request, opaque one-use receipt, terminal result, and audit description. Start with the package's deterministic local proof, then implement host-owned grant checks, atomic consumption, current authority/state validation, and audit delivery. The package remains a passive vocabulary: it does not provide an agent runtime, policy engine, receipt store, approval inbox, or remote endpoint.
