# Examples

This directory contains sample applications that use **ForgeTrust.AppSurface**.

- [Auth ASP.NET Core bridge example](auth-aspnetcore-bridge/README.md) – proves an ASP.NET Core host-owned auth stack can flow named policy results into AppSurface auth contracts.
- [Auth ASP.NET Core DevAuth example](auth-aspnetcore-dev-auth/README.md) – retains the responsive selectable persona marker and pairs an in-process host regression with a staged real-loopback verifier that distinguishes build, child-process, Kestrel-readiness, and DevAuth HTTP failures while retaining only bounded safe evidence.
- [Auth Aspire Keycloak AppHost proof](auth-aspire-keycloak-apphost/README.md) – starts real local Keycloak for AppSurface OIDC proof without making runtime web apps reference Keycloak packages.
- [Auth Web/RazorWire proof](auth-web-razorwire-proof/README.md) – shows a browser-first proof that one ASP.NET Core policy drives both a Minimal API response and a RazorWire-facing state.
- [Aspire AppHost example](aspire-apphost/README.md) – shows local Aspire AppHost composition with AppSurface profiles and reusable Aspire components.
- [Native Aspire deployment example](aspire-deployment-apphost/README.md) – publishes deterministic GCP migration-job artifacts and exposes read-only parity through native Aspire pipeline commands.
- [Console app example](console-app/README.md) – shows how to build a simple console application using [CliFx](https://github.com/Tyrrrz/CliFx) for command definitions.
- [LocalSecrets example](local-secrets/README.md) – shows how to set one local development secret, resolve it through AppSurface Config, and inspect diagnostics without printing the value.
- [Web app example](web-app/README.md) – demonstrates starting a minimal ASP.NET Core web application.
- [Web error-page proof](web-error-pages/README.md) – verifies AppSurface Web browser status pages, production exception pages, and API-friendly non-HTML behavior.
- [Web PWA runtime foundation proof](web-pwa-install/README.md) – demonstrates AppSurface Web install metadata, independent accessible application badging, combined offline and push-worker behavior, explicit browser registration, optional protected Web Push subscription and sender proof, development diagnostics, and CLI verification.
- [Config validation example](config-validation/README.md) – shows scalar validation on a strongly typed config wrapper and the startup failure shape.
- [Flow approval local example](flow-approval-local/README.md) – shows a typed flow that waits for an approval event and resumes through the in-memory runner.
- [Flow generated authoring example](flow-generated-authoring/README.md) – shows generated outcome cases, typed input/output ports, inferred and explicit graph mapping, and generated lowering into the in-memory runner.
- [Product readiness lab](product-readiness-lab/README.md) – runs a report-first local evaluator that composes AppSurface Web, Auth.AspNetCore, Flow, DurableTask-facing host-shape guidance, Aspire, and Postgres product-state proof. Its paired AppHost `verify` profile starts local Postgres and requires the product-state row to become `proven-locally`.

---
[🏠 Back to Root](../README.md)
