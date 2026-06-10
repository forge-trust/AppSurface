# AppSurface Product Readiness Lab

This example is a local evaluator for AppSurface package-family adoption. The readiness report is the product; the small SaaS-shaped app exists to generate evidence.

Run the report:

```bash
dotnet run --project examples/product-readiness-lab/ProductReadinessLab.csproj -- --report
```

Expected rows include:

```text
proven-locally
host-owned
deferred
unsafe-to-copy
blocked
```

Run the web app:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project examples/product-readiness-lab/ProductReadinessLab.csproj -- --urls http://127.0.0.1:5061
curl http://127.0.0.1:5061/readiness.md
```

Run the Aspire AppHost with local Postgres:

```bash
aspire run --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- local
```

## What This Lab Proves

- AppSurface Web can host the evaluator endpoints.
- AppSurface Auth.AspNetCore can map local ASP.NET Core policy results into neutral AppSurface auth results.
- AppSurface Flow can run an in-process product workflow.
- `IDurableTaskFlowRunner<TContext>` and `IDurableTaskFlowClient<TContext>` show where a host-owned Durable Task worker/client would connect.
- Postgres stores product/domain state when the AppHost provides `ConnectionStrings:ProductReadiness`.

## What This Lab Does Not Prove

- Durable Task worker or client hosting.
- Durable Task storage provider registration.
- DurableTask orchestration persistence in Postgres.
- Production deployment, secret rotation, real identity, or cloud hosting.

## In-Process Host Shape

The in-process host keeps the evaluator simple:

1. Start a typed flow in the app process.
2. Schedule the next node from `DurableTaskFlowDecisionKind.ScheduleNode`.
3. Wait for `approval-submitted` from `DurableTaskFlowDecisionKind.WaitForExternalEvent`.
4. Authorize resume requests with `IDurableTaskFlowClient<TContext>`.
5. Map approved, denied, timeout, and late events into completion, fault, timeout, and ignore decisions.

A production host would replace this loop with its own Durable Task worker/client, task hub, backend, timers, replay behavior, and external event delivery.

## What To Copy

- The report row taxonomy and exit-code rules.
- The AppSurface Web endpoint/module shape when it names real startup policy.
- The allowlisted config-summary approach.
- The boundary between product state and workflow orchestration state.

## What Not To Copy

- `X-Proof-User` proof authentication.
- Local proof secrets or connection values.
- The in-process host loop as a durability guarantee.
- Any claim that AppSurface provides Durable Task worker/client hosting or storage providers.
