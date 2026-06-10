# Product Readiness Lab AppHost

This AppHost runs `examples/product-readiness-lab` with a local Postgres resource for product/domain state.

```bash
aspire run --apphost examples/product-readiness-lab-apphost/ProductReadinessLabAppHost.csproj -- local
```

The Postgres resource proves product state persistence only. Durable Task worker/client hosting and storage provider registration remain host-owned and are not provided by AppSurface.
