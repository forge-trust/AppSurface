<!-- appsurface:unreleased-entry section="taking-shape" -->
- Integration tests can share a prepared PostgreSQL schema and give each case its own database. The [source-only fixture](https://github.com/forge-trust/AppSurface/blob/main/PostgreSql/ForgeTrust.AppSurface.PostgreSql.Testing/README.md) verifies catalog identity before cleanup and supports retrying a failed drop.
