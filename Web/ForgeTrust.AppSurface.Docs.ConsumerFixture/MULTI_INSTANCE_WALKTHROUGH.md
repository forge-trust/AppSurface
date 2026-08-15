# Public and Internal Docs Consumer Fixture

`ForgeTrust.AppSurface.Docs.ConsumerFixture` is the executable reference host for named Docs composition. It keeps the
legacy `/docs` fixture path for existing browser-layout checks and exposes a separate named mode for the public/internal
proof.

The named mode is deliberately small and mirrors the copyable composition contract:

- `public` reads the fixture's public source root at `/docs` with the `AppSurfaceDark` preset.
- `internal` reads a disjoint contributor source root at `/internal/docs` with the `GraphiteDark` preset.
- The host owns the `ConsumerFixtureInternalDocs` policy. A non-blank `X-Consumer-Fixture-User` request header is the
  fixture-only authentication mechanism used to demonstrate the order of `UseAuthentication`, `UseAuthorization`,
  instance mapping, and finalization. It is not a production authentication recommendation.

## Run the proof

From the repository root, run the real Kestrel-hosted integration test:

```bash
dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj \
  --filter FullyQualifiedName~AppSurfaceDocsMultiInstanceConsumerFixtureTests
```

The walkthrough starts the actual `WebStartup` consumer host, waits for both isolated search indexes, and proves this
sequence over HTTP:

1. Anonymous `GET /docs` returns the public surface and never includes contributor identity or corpus markers.
2. Anonymous `GET /internal/docs` receives an authentication challenge before a Docs view is rendered.
3. `X-Consumer-Fixture-User: contributor-alice` unlocks `GET /internal/docs`, which renders the contributor identity
   and graphite theme without public markers.
4. The public and contributor search-index payloads contain only their own fixture marker.

The test records the measured elapsed time in its test output and fails if the complete fixture walkthrough exceeds five
minutes. The latest measured run on 2026-08-15 completed in 0.71 seconds. Because it is an existing `WebStartup`
consumer host with real routing, middleware, MVC, and Kestrel, that five-minute gate also remains within the ten-minute
existing-host integration target.

## Adapt the shape

Use the [multiple independent Docs products guide](../ForgeTrust.AppSurface.Docs/use-appsurface-docs.md#run-multiple-independent-docs-products)
for the production API and host-authorization responsibilities. Replace the fixture header handler with your real
authentication scheme and keep source roots, route families, and branding prefixes disjoint.
