<!-- appsurface:unreleased-entry section="taking-shape" -->

- The [PostgreSQL integration test project](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/README.md) can compare fresh databases with template-backed clones for two Durable workloads. Contributors can reproduce the [#931 experiment](../../docs/designs/issue-931-durable-postgresql-fixtures.md) with a fixed test inventory, per-process coverage and checked cleanup. Fresh setup remains the default.
