using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Tests.Support;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableSchemaContractTests
{
    [Fact]
    public void MigrationCatalog_IsExactlyTwoOrderedChecksummedResources()
    {
        var migrations = DurablePostgreSqlMigrationCatalog.Load();

        Assert.Collection(
            migrations,
            first =>
            {
                Assert.Equal(1, first.Version);
                Assert.Equal("work_shared", first.Name);
                Assert.Equal(64, first.Sha256.Length);
                Assert.Contains("request_fingerprint_schema", first.Sql, StringComparison.Ordinal);
                Assert.Contains("request_fingerprint_sha256", first.Sql, StringComparison.Ordinal);
                Assert.Contains("request_schema text NOT NULL", first.Sql, StringComparison.Ordinal);
                Assert.DoesNotContain("provider_key text", first.Sql, StringComparison.Ordinal);
                Assert.DoesNotContain("UNIQUE (scope_id, provider_key)", first.Sql, StringComparison.Ordinal);
                Assert.DoesNotContain("retry_after", first.Sql, StringComparison.Ordinal);
                Assert.Contains("active_runtime_epoch uuid CHECK", first.Sql, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal(2, second.Version);
                Assert.Equal("forced_rls", second.Name);
                Assert.Equal(64, second.Sha256.Length);
                Assert.Contains("FORCE ROW LEVEL SECURITY", second.Sql, StringComparison.Ordinal);
                Assert.Contains("dispatch_global_discovery", second.Sql, StringComparison.Ordinal);
                Assert.Contains("dispatch_scope_update", second.Sql, StringComparison.Ordinal);
                Assert.Contains("REVOKE ALL", second.Sql, StringComparison.Ordinal);
            });
        Assert.Equal(migrations.Count, DurablePostgreSqlMigrationCatalog.RequiredVersion);
        Assert.Equal(migrations.Count, PostgreSqlDurableRuntimeSchemaManager.RequiredVersion);
        Assert.Equal(migrations.Count, PostgreSqlDurableWorkStore.RequiredSchemaVersion);
    }

    [Fact]
    public void MigrationCatalog_CachesOnlyTheValidatedDefaultAssembly()
    {
        var first = DurablePostgreSqlMigrationCatalog.Load();
        var second = DurablePostgreSqlMigrationCatalog.Load();

        Assert.Same(first, second);
        var cachedList = Assert.IsAssignableFrom<IList<DurablePostgreSqlMigration>>(first);
        Assert.Throws<NotSupportedException>(() => cachedList[0] = first[0]);
        var explicitAssemblyFailure = Assert.Throws<InvalidOperationException>(
            () => DurablePostgreSqlMigrationCatalog.Load(typeof(DurableSchemaContractTests).Assembly));
        Assert.Contains("contains no embedded migrations", explicitAssemblyFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateScript_IsOrderedExplicitAndNeverOpensTheDataSource()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        var script = manager.GenerateScript();
        var pendingOnly = manager.GenerateScript(1);

        Assert.True(
            script.IndexOf("0001_work_shared", StringComparison.Ordinal)
            < script.IndexOf("0002_forced_rls", StringComparison.Ordinal));
        Assert.Contains("pg_advisory_lock", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0001_work_shared", pendingOnly, StringComparison.Ordinal);
        Assert.Contains("0002_forced_rls", pendingOnly, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.GenerateScript(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.GenerateScript(3));
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("actor with spaces", "reason")]
    [InlineData("actor", "reason/unsafe")]
    public async Task EpochOperations_RejectUnsafeAuditCodesBeforeOpeningConnection(string actor, string reason)
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.InitializeRuntimeEpochAsync(Guid.NewGuid(), actor, reason));
    }

    [Fact]
    public async Task EpochOperations_RejectEmptyOrEqualEpochsBeforeOpeningConnection()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var epoch = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.InitializeRuntimeEpochAsync(Guid.Empty, "deploy", "initial"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.RotateRuntimeEpochAsync(Guid.Empty, epoch, "deploy", "restore"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.RotateRuntimeEpochAsync(epoch, epoch, "deploy", "restore"));
    }

    [Fact]
    public void SchemaResults_DefensivelyCopyVersionCollections()
    {
        var applied = new[] { 1 };
        var pending = new[] { 2 };
        var status = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.UpgradeRequired,
            Guid.NewGuid(),
            null,
            1,
            2,
            1,
            2,
            1,
            2,
            applied,
            pending,
            "Upgrade required.");
        var result = new DurableRuntimeSchemaApplyResult(0, 1, applied);

        applied[0] = 99;
        pending[0] = 99;

        Assert.Equal([1], status.AppliedVersions);
        Assert.Equal([2], status.PendingVersions);
        Assert.Equal([1], result.AppliedVersions);
        Assert.Throws<ArgumentNullException>(() => new DurableRuntimeSchemaApplyResult(0, 0, null!));
    }

    [Fact]
    public void RoleRecipe_TreatsRoleNamesAsExactDataAndGrantsRequiredRuntimeTables()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var recipe = File.ReadAllText(TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable/configure-postgresql-roles.sql"));

        Assert.DoesNotContain("to_regrole", recipe, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(recipe, "WHERE rolname = :"));
        Assert.Contains("format('ALTER SCHEMA appsurface_durable OWNER TO %I'", recipe, StringComparison.Ordinal);
        Assert.Contains("appsurface_durable.store_metadata, appsurface_durable.schema_migration", recipe, StringComparison.Ordinal);
        Assert.Contains("appsurface_durable.scope_history", recipe, StringComparison.Ordinal);
        Assert.Contains("appsurface_durable.work_operator_command", recipe, StringComparison.Ordinal);
        Assert.Contains(
            "GRANT SELECT, INSERT ON appsurface_durable.scope_history, appsurface_durable.work_history",
            recipe,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SELECT, INSERT, UPDATE ON appsurface_durable.scope_history",
            recipe,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT UPDATE (status, resulting_state, resulting_revision, completed_at) ON appsurface_durable.work_operator_command",
            recipe,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit",
            recipe,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UPDATE ON appsurface_durable.work_operator_command, appsurface_durable.effect_permit",
            recipe,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsCatalog_ListsEveryImplementedWorkOperatorProblem()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var diagnostics = File.ReadAllText(TestPathUtils.PathUnder(
            repositoryRoot,
            "troubleshooting/durable-diagnostics.md"));

        for (var code = 111; code <= 118; code++)
        {
            Assert.Contains($"`ASDUR{code}`", diagnostics, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PostgreSqlPublicTypes_MatchReviewedBaseline()
    {
        PublicApiSnapshot.AssertMatches(
            typeof(IDurableRuntimeSchemaManager).Assembly,
            "PostgreSql.PublicAPI.Shipped.txt",
            "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PublicAPI.Shipped.txt");
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
