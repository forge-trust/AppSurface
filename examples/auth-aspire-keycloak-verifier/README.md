# AppSurface Auth Aspire Keycloak Verifier

This helper is run by the `verify` profile in `examples/auth-aspire-keycloak-apphost`. It probes the AppHost-backed web app and checks generated realm evidence. Run it through the AppHost rather than directly:

```bash
aspire run --non-interactive --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- verify
```

For direct diagnostics, `--target <url>` overrides the AppHost-projected web proof URL and `--timeout-seconds <seconds>` must be between 1 and 300.
