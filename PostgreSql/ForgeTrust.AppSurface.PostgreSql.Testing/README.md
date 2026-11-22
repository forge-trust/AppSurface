# PostgreSQL fixtures for integration tests

Use one `PostgreSqlFixture` per test assembly or host to reuse a prepared schema across independent test databases. The host supplies an Npgsql connection string for a **disposable PostgreSQL 15+ test cluster** with access to the `postgres` maintenance database and privileges to create and drop databases and terminate fixture connections. The fixture works with any .NET 10 test framework. It has no dependency on EF Core or the [Durable provider](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md).

## Typical use

The following example accepts the application's own setup and test callbacks. Keep the fixture instance across cases, then await its cleanup from the host's teardown hook.

```csharp
using ForgeTrust.AppSurface.PostgreSql.Testing;

var fixture = new PostgreSqlFixture();
try
{
    await fixture.RunFromTemplateAsync(adminConnectionString,
        databaseNamePrefix: "orders_case",
        templateNamePrefix: "orders_current_model",
        initializeTemplateAsync: createAndValidateOrderSchemaAsync,
        action: verifyOrderRoundTripAsync,
        cancellationToken: testCancellation);
}
finally
{
    await fixture.CleanupAllAsync();
}
```

| Operation | Contract |
| --- | --- |
| `ValidateDatabaseNamePrefix` | Checks identifier syntax offline, including before a test host reads optional database configuration. A rejected value raises an argument exception naming the input. |
| `RunFreshAsync` | Creates an empty uniquely named database, invokes the callback, and drops the database on exit. Use it when the test verifies migrations, initial installation, or missing schema. |
| `RunFromTemplateAsync` | Initializes one template per fixture, exact connection string and logical template prefix. Each callback gets a fresh physical clone of that template. The template initializer receives a non-pooled connection string and an independent five-minute setup token. A canceled waiter leaves preparation available to other tests. A failed initializer that cleans up successfully can be retried. |
| `WithClusterInitializationLockAsync` | Runs a callback under a cluster bootstrap advisory lock. Use this for operations such as creating a shared role. The callback receives the initialization deadline. Database cloning alone does not isolate roles or other cluster-wide objects. |
| `CleanupTemplatesAsync` | Selects this instance's registrations by connection string and logical prefix. Intended for an owner that has finished using those schemas. |
| `CleanupAllAsync` | Attempts all templates registered by this owner at the time of the call. Waits for active users and pending initialization, up to five minutes or a shorter caller deadline. Returns a `TemplateCleanupResult` with elapsed time, exact database names and individual outcomes. |

Use a distinct template prefix for every schema, migration head, role configuration or seed that affects its contents. Use ASCII letters, digits and underscores. Catalog identifiers retain a unique suffix within PostgreSQL's 63-byte limit. Avoid keeping connections to the template after initialization. The library serializes database DDL across cooperating processes and uses PostgreSQL's `FILE_COPY` clone strategy. Dispose callback connections normally. Each test's cancellation token controls its work. Database teardown gets its own deadline, including when the callback throws.

Stop admitting new tests before host teardown. An incomplete cleanup raises `TemplateCleanupException` with a `Result` covering every selected registration. `Failed` registrations can be retried and `AlreadyInProgress` identifies another active cleanup attempt. A successful teardown means all selected registrations were released. Explicit teardown is the place to write an application-specific receipt. A bounded process-exit fallback covers ordinary shutdown. Abrupt process termination requires the test-cluster owner to dispose of the cluster or account for residual databases.

The application owns schema creation, migration verification, seed data, logical application identities, and evidence-host receipts. Keep tests that verify these behaviors on the application side. The [package chooser](../../packages/README.md) records package availability.
