# Product Readiness Lab AppHost

This AppHost runs `examples/product-readiness-lab` with a local Postgres resource for product/domain state.

```bash
aspire run --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- local
```

The Postgres resource proves product state persistence only. Durable Task worker/client hosting and storage provider registration remain host-owned and are not provided by AppSurface.

Use the bounded verification profile when you want the report to prove local Postgres persistence instead of merely stating that Postgres is not configured:

```bash
aspire run --non-interactive --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- verify
```

The `verify` profile starts the web app with local Postgres, probes `/readiness`, fails unless `postgres-product-state` is `proven-locally`, and then stops the AppHost resources. The Postgres resource proves product state persistence only. Durable Task worker/client hosting and storage provider registration remain host-owned and are not provided by AppSurface.

If the Aspire CLI stops at `Checking certificates...` or `Trusting certificates...`, trust the local ASP.NET Core development certificate from an interactive shell:

```bash
aspire certs trust
```

On macOS this can require keychain approval before the noninteractive `verify` command can continue. If the Aspire CLI is not yet on `PATH`, `dotnet dev-certs https --trust` is the equivalent fallback.
