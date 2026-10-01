using System.Text;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises complete role-set preflight against the pinned PostgreSQL catalog implementation.</summary>
public sealed class DurableSchemaPreflightIntegrationTests
{
    private const string Owner = "preflight_migration_owner";
    private const string DispatcherA = "preflight_dispatcher_a";
    private const string RuntimeA = "preflight_runtime_a";
    private const string DispatcherB = "preflight_dispatcher_b";
    private const string RuntimeB = "preflight_runtime_b";
    private const string RolePassword = "durable-preflight-test-password";
    private const string Image = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";

    [Fact]
    public async Task Two_pair_recipe_passes_for_each_runtime_and_owner_is_only_diagnostic()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.ReadStatusAsync();
        var catalogBefore = await fixture.ReadCatalogSignatureAsync();
        var first = await fixture.RunAsync(RuntimeA);
        var second = await fixture.RunAsync(RuntimeB);
        var owner = await fixture.RunAsync(Owner);
        var catalogAfter = await fixture.ReadCatalogSignatureAsync();
        var after = await fixture.ReadStatusAsync();

        Assert.Equal("runtime", first.CallerEvidenceKind);
        Assert.Equal(1, first.PairIndex);
        Assert.Equal(RuntimeA, first.CallerRole);
        Assert.Equal("runtime", second.CallerEvidenceKind);
        Assert.Equal(2, second.PairIndex);
        Assert.Equal(RuntimeB, second.CallerRole);
        Assert.Equal("owner-diagnostic", owner.CallerEvidenceKind);
        Assert.Null(owner.PairIndex);
        Assert.Equal(Owner, owner.CallerRole);
        Assert.NotEqual(Guid.Empty, first.StoreId);
        Assert.Equal(first.StoreId, second.StoreId);
        Assert.Equal(first.ActiveRuntimeEpoch, second.ActiveRuntimeEpoch);
        Assert.Null(first.ActiveRuntimeEpoch);
        Assert.Equal(before.StoreId, first.StoreId);
        Assert.Equal(before.StoreId, after.StoreId);
        Assert.Null(before.ActiveRuntimeEpoch);
        Assert.Equal(before.ActiveRuntimeEpoch, after.ActiveRuntimeEpoch);
        Assert.Equal(catalogBefore, catalogAfter);
        Assert.Empty(first.FailedChecks);
        Assert.Empty(second.FailedChecks);
        Assert.Empty(owner.FailedChecks);
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Schema_ten_owner_preflight_reports_pending_0011_without_structural_findings_or_catalog_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DowngradeToSchemaTenAsync();
        var before = await fixture.ReadStatusAsync();
        var catalogBefore = await fixture.ReadCatalogSignatureAsync();

        var result = await fixture.RunAsync(Owner);
        var commandError = await fixture.RunCommandAsync(Owner);

        var after = await fixture.ReadStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, before.Compatibility);
        Assert.Equal(10, before.InstalledVersion);
        Assert.Equal(11, before.RequiredVersion);
        Assert.Equal([11], before.PendingVersions);
        Assert.Equal(before.Compatibility, result.Status.Compatibility);
        Assert.Equal(before.InstalledVersion, result.Status.InstalledVersion);
        Assert.Equal(before.RequiredVersion, result.Status.RequiredVersion);
        Assert.Equal([11], result.Status.PendingVersions);
        Assert.Empty(result.FailedChecks);
        Assert.Equal("unresolved", result.CallerEvidenceKind);
        Assert.Contains("pending migration 0011", commandError.Message, StringComparison.Ordinal);
        Assert.Contains("not a passing gate", commandError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("structural preflight failed", commandError.Message, StringComparison.Ordinal);
        Assert.Equal(before.Compatibility, after.Compatibility);
        Assert.Equal(before.StoreId, after.StoreId);
        Assert.Equal(before.ActiveRuntimeEpoch, after.ActiveRuntimeEpoch);
        Assert.Equal(catalogBefore, await fixture.ReadCatalogSignatureAsync());
    }

    [Fact]
    public async Task Missing_schema_preflight_stays_incompatible_and_does_not_create_catalog_objects()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync("DROP SCHEMA appsurface_durable CASCADE");
        var before = await fixture.ReadStatusAsync();
        var catalogBefore = await fixture.ReadCatalogSignatureAsync();

        var result = await fixture.RunAsync(Owner);
        var commandError = await fixture.RunCommandAsync(Owner);

        var after = await fixture.ReadStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, result.Status.Compatibility);
        Assert.Equal(0, result.Status.InstalledVersion);
        Assert.Empty(result.FailedChecks);
        Assert.Contains("preflight is Missing", commandError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("structural preflight failed", commandError.Message, StringComparison.Ordinal);
        Assert.Equal(before.Compatibility, after.Compatibility);
        Assert.Equal(before.StoreId, after.StoreId);
        Assert.Equal(catalogBefore, await fixture.ReadCatalogSignatureAsync());
    }

    [Fact]
    public async Task Thirty_two_pairs_and_large_unrelated_role_catalog_remain_bounded_and_exact()
    {
        await using var fixture = await Fixture.CreateAsync(pairCount: 32, unrelatedRoleCount: 256);

        var first = await fixture.RunAsync(RuntimeA);
        var lastRole = Fixture.RuntimeRole(32);
        var last = await fixture.RunAsync(lastRole);

        Assert.Empty(first.FailedChecks);
        Assert.Equal(1, first.PairIndex);
        Assert.Equal(RuntimeA, first.CallerRole);
        Assert.Empty(last.FailedChecks);
        Assert.Equal(32, last.PairIndex);
        Assert.Equal(lastRole, last.CallerRole);
        Assert.True(last.FailedChecks.Count <= 12 + (32 * 7));
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Dispatcher_retention_and_unknown_callers_are_not_runtime_evidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateUnreviewedCallerAsync();
        await fixture.MutateAsync(
            "GRANT USAGE ON SCHEMA appsurface_durable TO preflight_dispatcher_a, durable_retention, preflight_unrelated; " +
            "GRANT SELECT ON ALL TABLES IN SCHEMA appsurface_durable TO preflight_dispatcher_a, durable_retention, preflight_unrelated");
        var catalogBefore = await fixture.ReadCatalogSignatureAsync();

        foreach (var role in new[] { DispatcherA, "durable_retention", "preflight_unrelated" })
        {
            var result = await fixture.RunAsync(role);
            Assert.Equal("unresolved", result.CallerEvidenceKind);
            Assert.Contains("caller_role", result.FailedChecks);
            Assert.Null(result.PairIndex);
        }
        Assert.Equal(catalogBefore, await fixture.ReadCatalogSignatureAsync());
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Set_role_on_the_same_fenced_snapshot_fails_runtime_credential_classification()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync($"GRANT {DispatcherA} TO {RuntimeA} WITH SET TRUE");
        var catalogBefore = await fixture.ReadCatalogSignatureAsync();
        const string applicationName = "issue845-set-role-classifier";
        var connectionSettings = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnection(RuntimeA, pooling: false, maxPoolSize: 1))
        {
            ApplicationName = applicationName,
        };
        await using (var connection = new NpgsqlConnection(connectionSettings.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var acquireFence = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock_shared(@key)", connection))
            {
                acquireFence.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
                await acquireFence.ExecuteNonQueryAsync();
            }
            await using (var setRole = new NpgsqlCommand($"SET ROLE {DispatcherA}", connection))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync();
            }
            await using (var identity = new NpgsqlCommand("SELECT session_user::text, current_user::text", connection, transaction))
            await using (var reader = await identity.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(RuntimeA, reader.GetString(0));
                Assert.Equal(DispatcherA, reader.GetString(1));
            }

            var caller = await DurableSchemaPreflightVerifier.ReadCallerAsync(
                connection, transaction, fixture.Request, CancellationToken.None);
            Assert.Equal(("unresolved", (string?)null, (int?)null, "caller_identity"), caller);
            await using (var stillOwned = new NpgsqlCommand("SELECT 1", connection, transaction))
            {
                Assert.Equal(1, await stillOwned.ExecuteScalarAsync());
            }
            await transaction.CommitAsync();
            await using var releaseFence = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock_shared(@key)", connection);
            releaseFence.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
            Assert.True(await releaseFence.ExecuteScalarAsync() is true);
        }

        Assert.Equal(catalogBefore, await fixture.ReadCatalogSignatureAsync());
        Assert.Equal(0, await fixture.CountSessionsAsync(applicationName));
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Missing_second_pair_and_reviewed_owner_are_reported_without_accepting_the_first_pair()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync("ALTER ROLE preflight_runtime_b RENAME TO preflight_runtime_b_missing");

        foreach (var caller in new[] { RuntimeA, Owner })
        {
            var result = await fixture.RunAsync(caller);
            Assert.Contains("role_resolution", result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
            Assert.NotEmpty(result.FailedChecks);
        }
        await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await fixture.RunAsync(RuntimeB));
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Consistent_reassignment_of_schema_tables_functions_and_owner_policy_does_not_replace_reviewed_owner()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync(
            "CREATE ROLE preflight_reassigned_owner LOGIN PASSWORD 'durable-preflight-test-password'; " +
            "REASSIGN OWNED BY preflight_migration_owner TO preflight_reassigned_owner; " +
            "ALTER POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat TO preflight_reassigned_owner; " +
            "GRANT USAGE ON SCHEMA appsurface_durable TO preflight_migration_owner; " +
            "GRANT SELECT ON ALL TABLES IN SCHEMA appsurface_durable TO preflight_migration_owner");

        foreach (var caller in new[] { RuntimeA, RuntimeB, Owner })
        {
            var result = await fixture.RunAsync(caller);
            Assert.Contains("function_owner", result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
            Assert.NotEmpty(result.FailedChecks);
        }
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Default_public_execute_acl_for_recreated_routines_fails_for_every_caller()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RecreateFunctionWithDefaultAclAsync(
            "appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)",
            "prune_runtime_heartbeats_before_default_acl_test");
        await fixture.RecreateFunctionWithDefaultAclAsync(
            "appsurface_durable.runtime_due_dispatch_health(integer)",
            "runtime_due_dispatch_health_before_default_acl_test");
        Assert.True(await fixture.HasNullFunctionAclAsync("appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)"));
        Assert.True(await fixture.HasNullFunctionAclAsync("appsurface_durable.runtime_due_dispatch_health(integer)"));

        foreach (var caller in new[] { RuntimeA, RuntimeB, Owner })
        {
            var result = await fixture.RunAsync(caller);
            Assert.Contains("function_acl", result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
            Assert.Contains("due_health_acl", result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
        }
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Public_delete_and_truncate_fail_for_both_runtime_pairs_and_owner_diagnostic()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync(
            "GRANT DELETE, TRUNCATE ON appsurface_durable.runtime_heartbeat TO PUBLIC");
        var before = await fixture.ReadCatalogSignatureAsync();

        foreach (var caller in new[] { RuntimeA, RuntimeB, Owner })
        {
            var result = await fixture.RunAsync(caller);
            var failures = result.FailedChecks.Where(check => CategoryOf(check) == "runtime_table_privileges").ToArray();
            Assert.Contains(failures, failure => failure.StartsWith("runtime_table_privileges[pair=1,role=\"preflight_runtime_a\"]", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.StartsWith("runtime_table_privileges[pair=2,role=\"preflight_runtime_b\"]", StringComparison.Ordinal));
        }

        Assert.Equal(before, await fixture.ReadCatalogSignatureAsync());
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Max_pool_size_one_and_repeated_success_release_every_owned_backend_and_lock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.RunAsync(RuntimeA);
        Assert.Empty(result.FailedChecks);

        await using var pooled = NpgsqlDataSource.Create(fixture.RuntimeConnection(RuntimeA, pooling: true, maxPoolSize: 1));
        await using var connection = await pooled.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
        await fixture.AssertNoFenceLocksAsync();
        Assert.Equal(0, await fixture.CountPreflightSessionsAsync());
    }

    [Fact]
    public async Task Shared_fence_waits_for_preceding_writer_then_reads_the_committed_catalog_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var writer = await fixture.OpenOwnerConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(@key)", writer, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }
        await using (var drift = new NpgsqlCommand($"ALTER ROLE {RuntimeB} BYPASSRLS", writer, transaction))
        {
            await drift.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-reader-after-writer";
        var reader = fixture.RunAsync(RuntimeA, applicationName: readerName);
        await fixture.WaitForWaitEventAsync(readerName, "advisory");
        await transaction.CommitAsync();
        var result = await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(result.FailedChecks, failure => failure.StartsWith("runtime_role[pair=2,role=\"preflight_runtime_b\"]", StringComparison.Ordinal));
        await fixture.AssertNoFenceLocksAsync();
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_writer_fence_fails_and_releases_reader_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var writer = await fixture.OpenOwnerConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(@key)", writer, transaction))
        {
            command.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-cancelled-reader";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var read = fixture.RunAsync(RuntimeA, applicationName: readerName, cancellationToken: cancellation.Token);
        await fixture.WaitForWaitEventAsync(readerName, "advisory");
        var error = await Record.ExceptionAsync(async () => await read);
        Assert.NotNull(error);
        if (error is DurablePreflightException preflightError)
        {
            Assert.Equal("timeout", preflightError.Category);
        }
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
        await transaction.CommitAsync();
        await fixture.AssertNoFenceLocksAsync();
    }

    [Fact]
    public async Task Total_deadline_without_caller_cancellation_bounds_writer_wait_and_cleans_up_owned_resources()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var writer = await fixture.OpenOwnerConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(@key)", writer, transaction))
        {
            command.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-deadline-reader";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(async () =>
            await fixture.RunAsync(RuntimeA, applicationName: readerName, cancellationToken: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(35)));
        stopwatch.Stop();

        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Preflight exceeded its 30-second total deadline: {stopwatch.Elapsed}");
        await transaction.CommitAsync();
        await fixture.AssertNoFenceLocksAsync();
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
        Assert.Equal(0, await fixture.CountPreflightSessionsAsync());
    }

    [Fact]
    public async Task Cancellation_during_repeatable_read_catalog_wait_closes_backend_and_releases_fence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var blocker = await fixture.OpenOwnerConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "LOCK TABLE appsurface_durable.store_metadata IN ACCESS EXCLUSIVE MODE", blocker, blockerTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-cancelled-catalog-reader";
        using var cancellation = new CancellationTokenSource();
        var read = fixture.RunAsync(RuntimeA, applicationName: readerName, cancellationToken: cancellation.Token);
        await fixture.WaitForWaitEventAsync(readerName, "relation");
        cancellation.Cancel();

        var error = await Record.ExceptionAsync(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(error);
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
        await fixture.AssertNoFenceLocksAsync();
        await blockerTransaction.CommitAsync();
    }

    [Fact]
    public async Task Writer_waiting_after_shared_fence_proceeds_only_after_read_snapshot_finishes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var blocker = await fixture.OpenOwnerConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "LOCK TABLE appsurface_durable.store_metadata IN ACCESS EXCLUSIVE MODE", blocker, blockerTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-reader-before-writer";
        var reader = fixture.RunAsync(RuntimeA, applicationName: readerName);
        await fixture.WaitForWaitEventAsync(readerName, "relation");

        const string writerName = "issue845-writer-after-reader";
        var writerSettings = new NpgsqlConnectionStringBuilder(fixture.OwnerConnection) { ApplicationName = writerName };
        await using var writer = new NpgsqlConnection(writerSettings.ConnectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await using var fenceAttempt = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(@key)", writer, writerTransaction);
        fenceAttempt.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
        var writerWait = fenceAttempt.ExecuteNonQueryAsync();
        await fixture.WaitForWaitEventAsync(writerName, "advisory");

        await blockerTransaction.CommitAsync();
        var result = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(result.FailedChecks);
        await writerWait.WaitAsync(TimeSpan.FromSeconds(10));
        await writerTransaction.CommitAsync();
        await fixture.AssertNoFenceLocksAsync();
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
    }

    [Fact]
    public async Task Terminated_backend_during_catalog_transaction_releases_snapshot_and_shared_fence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var blocker = await fixture.OpenOwnerConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "LOCK TABLE appsurface_durable.store_metadata IN ACCESS EXCLUSIVE MODE", blocker, blockerTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-terminated-catalog-reader";
        var read = fixture.RunAsync(RuntimeA, applicationName: readerName);
        await fixture.WaitForWaitEventAsync(readerName, "relation");
        var pid = await fixture.WaitForBackendAsync(readerName);
        await fixture.TerminateBackendAsync(pid);
        await Assert.ThrowsAnyAsync<Exception>(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
        await fixture.AssertNoFenceLocksAsync();
        await blockerTransaction.CommitAsync();
    }

    [Fact]
    public async Task Terminated_waiting_backend_never_returns_success_or_leaves_a_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var writer = await fixture.OpenOwnerConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(@key)", writer, transaction))
        {
            command.Parameters.AddWithValue("key", fixture.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
        }

        const string readerName = "issue845-terminated-reader";
        var read = fixture.RunAsync(RuntimeA, applicationName: readerName);
        var pid = await fixture.WaitForBackendAsync(readerName);
        await fixture.TerminateBackendAsync(pid);
        await Assert.ThrowsAnyAsync<Exception>(async () => await read);
        Assert.Equal(0, await fixture.CountSessionsAsync(readerName));
        await transaction.CommitAsync();
        await fixture.AssertNoFenceLocksAsync();
    }

    [Theory]
    [InlineData("ALTER ROLE preflight_runtime_b NOLOGIN", "runtime_role", true)]
    [InlineData("ALTER ROLE preflight_runtime_b SUPERUSER", "runtime_role", true)]
    [InlineData("ALTER ROLE preflight_runtime_b CREATEDB", "runtime_role", true)]
    [InlineData("ALTER ROLE preflight_runtime_b CREATEROLE", "runtime_role", true)]
    [InlineData("ALTER ROLE preflight_runtime_b REPLICATION", "runtime_role", true)]
    [InlineData("ALTER ROLE preflight_runtime_b BYPASSRLS", "runtime_role", true)]
    [InlineData("GRANT preflight_dispatcher_a TO preflight_runtime_b", "role_membership", true)]
    [InlineData("GRANT preflight_runtime_b TO preflight_dispatcher_a", "role_membership", true)]
    [InlineData("GRANT DELETE ON appsurface_durable.runtime_heartbeat TO preflight_runtime_b", "runtime_table_privileges", true)]
    [InlineData("GRANT TRUNCATE ON appsurface_durable.runtime_heartbeat TO preflight_runtime_b", "runtime_table_privileges", true)]
    [InlineData("GRANT DELETE ON appsurface_durable.runtime_heartbeat TO PUBLIC", "runtime_table_privileges", true)]
    [InlineData("GRANT TRUNCATE ON appsurface_durable.runtime_heartbeat TO PUBLIC", "runtime_table_privileges", true)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO PUBLIC", "function_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO preflight_dispatcher_b", "function_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO durable_retention", "function_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO preflight_unrelated", "function_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO preflight_runtime_b WITH GRANT OPTION", "function_acl", true)]
    [InlineData("REVOKE EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) FROM preflight_runtime_b", "function_acl", true)]
    [InlineData("ALTER TABLE appsurface_durable.runtime_heartbeat OWNER TO preflight_runtime_b", "runtime_ownership", true)]
    [InlineData("ALTER TABLE appsurface_durable.runtime_heartbeat DISABLE ROW LEVEL SECURITY", "forced_rls", false)]
    [InlineData("DROP POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch", "runtime_policy_set", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO PUBLIC", "due_health_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO preflight_dispatcher_b", "due_health_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO durable_retention WITH GRANT OPTION", "due_health_acl", false)]
    [InlineData("GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO preflight_runtime_b WITH GRANT OPTION", "due_health_acl", true)]
    [InlineData("REVOKE EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM preflight_runtime_b", "due_health_acl", true)]
    [InlineData("ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO PUBLIC", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO preflight_runtime_a, preflight_dispatcher_b", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO preflight_runtime_a, durable_retention", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat USING (false)", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat WITH CHECK (false)", "heartbeat_policies", false)]
    [InlineData("DROP POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat; CREATE POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat AS RESTRICTIVE FOR ALL TO preflight_runtime_a, preflight_runtime_b USING (true) WITH CHECK (true)", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat TO preflight_runtime_b", "heartbeat_policies", false)]
    [InlineData("ALTER POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch TO preflight_runtime_a", "runtime_policy_set", false)]
    [InlineData("ALTER POLICY schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch TO preflight_runtime_a", "runtime_policy_set", false)]
    [InlineData("ALTER POLICY schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch TO preflight_runtime_a", "runtime_policy_set", false)]
    [InlineData("ALTER POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch TO preflight_runtime_a, durable_retention", "runtime_policy_set", false)]
    [InlineData("DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention", "retention_index", false)]
    [InlineData("ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) OWNER TO appsurface", "function_owner", false)]
    [InlineData("ALTER ROLE preflight_runtime_b RENAME TO preflight_runtime_b_missing", "role_resolution", false)]
    [InlineData("ALTER ROLE preflight_migration_owner RENAME TO preflight_migration_owner_missing", "role_resolution", false)]
    [InlineData("ALTER DATABASE appsurface_durable OWNER TO preflight_runtime_b", "runtime_ownership", true)]
    [InlineData("ALTER SCHEMA appsurface_durable OWNER TO preflight_runtime_b", "runtime_ownership", true)]
    [InlineData("CREATE SEQUENCE appsurface_durable.issue845_runtime_owned_sequence; ALTER SEQUENCE appsurface_durable.issue845_runtime_owned_sequence OWNER TO preflight_runtime_b", "runtime_ownership", true)]
    [InlineData("CREATE FUNCTION appsurface_durable.issue845_runtime_owned_function() RETURNS integer LANGUAGE sql AS 'SELECT 1'; ALTER FUNCTION appsurface_durable.issue845_runtime_owned_function() OWNER TO preflight_runtime_b", "runtime_ownership", true)]
    public async Task Drift_on_second_pair_is_reported_with_stable_pair_and_role(string mutation, string category, bool pairQualified)
    {
        var result = await RunSecondPairMutationAsync(mutation);

        Assert.Contains(category, result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
        if (pairQualified)
        {
            Assert.Contains(result.FailedChecks, failure =>
                failure.StartsWith(category + "[pair=2,role=\"preflight_runtime_b\"]", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) RENAME TO missing_prune", "function_signature")]
    [InlineData("ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) SECURITY INVOKER", "security_definer")]
    [InlineData("ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) SET search_path = public", "search_path")]
    [InlineData("ALTER TABLE appsurface_durable.runtime_heartbeat NO FORCE ROW LEVEL SECURITY", "forced_rls")]
    [InlineData("ALTER ROLE preflight_runtime_a BYPASSRLS", "runtime_role")]
    [InlineData("GRANT TRUNCATE ON appsurface_durable.runtime_heartbeat TO preflight_runtime_a", "runtime_table_privileges")]
    public async Task Existing_singleton_guards_still_fail_closed(string mutation, string category)
    {
        var result = await RunSinglePairMutationAsync(mutation);
        Assert.Contains(category, result.FailedChecks.Select(CategoryOf), StringComparer.Ordinal);
    }

    [Fact]
    public void Catalog_projection_rejects_unknown_duplicate_and_unconstructible_raw_target_shapes()
    {
        var request = CreateRequest();
        Assert.Equal(new[] { "catalog_result" }, DurableSchemaPreflightVerifier.MapCatalogResult(null, request));
        Assert.Equal(new[] { "catalog_result" }, DurableSchemaPreflightVerifier.MapCatalogResult(new[] { "role_resolution", "unexpected" }, request));
        Assert.Equal(new[] { "catalog_result" }, DurableSchemaPreflightVerifier.MapCatalogResult(new[] { "role_resolution", "role_resolution" }, request));

        var mapped = DurableSchemaPreflightVerifier.MapCatalogResult(
            new[] { "due_health_acl:2", "runtime_table_privileges:2", "function_acl:1", "runtime_role:1" }, request);
        Assert.Equal(new[]
        {
            "runtime_role[pair=1,role=\"preflight_runtime_a\"]",
            "function_acl[pair=1,role=\"preflight_runtime_a\"]",
            "runtime_table_privileges[pair=2,role=\"preflight_runtime_b\"]",
            "due_health_acl[pair=2,role=\"preflight_runtime_b\"]",
        }, mapped);
    }

    [Fact]
    public async Task Exact_role_set_predicate_checks_null_empty_public_missing_extra_duplicate_and_ordered_sets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cases = new (uint[]? Targets, uint[] Expected, bool Pass, string Name)[]
        {
            (null, [101, 202], false, "null"),
            ([], [101, 202], false, "empty"),
            ([0, 101, 202], [101, 202], false, "PUBLIC"),
            ([202], [101, 202], false, "missing-first"),
            ([101], [101, 202], false, "missing-second"),
            ([101, 202, 303], [101, 202], false, "extra-unresolved"),
            ([101, 101, 202], [101, 202], false, "duplicate"),
            ([202, 101], [101, 202], true, "unordered-exact"),
        };

        foreach (var item in cases)
        {
            Assert.Equal(item.Pass, await fixture.EvaluateRoleSetAsync(item.Targets, item.Expected));
        }
    }

    [Fact]
    public void Session_settings_disable_pooling_enlistment_and_multiplexing_and_bound_driver_timeouts()
    {
        var settings = DurableSchemaPreflightVerifier.CreateSessionSettings(
            "Host=localhost;Database=appsurface_durable;Username=appsurface;Pooling=true;Enlist=true;Multiplexing=true;Timeout=0;Command Timeout=0");

        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        Assert.False(settings.Multiplexing);
        Assert.Equal(-1, settings.CancellationTimeout);
        Assert.Equal(28, settings.Timeout);
        Assert.Equal(28, settings.CommandTimeout);
    }

    internal static async Task<DurablePreflightResult> RunSinglePairMutationAsync(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync(pairCount: 1);
        await fixture.MutateAsync(mutation);
        return await fixture.RunAsync(RuntimeA);
    }

    internal static async Task<DurablePreflightResult> RunSecondPairMutationAsync(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync(mutation);
        return await fixture.RunAsync(RuntimeA);
    }

    internal static string CategoryOf(string failure) => failure.Split('[', 2)[0];

    private static DurablePreflightRequest CreateRequest()
    {
        const string json = "{\"version\":1,\"pairs\":[{\"dispatcher\":\"preflight_dispatcher_a\",\"runtime\":\"preflight_runtime_a\",\"dispatcher_profile\":\"full\"},{\"dispatcher\":\"preflight_dispatcher_b\",\"runtime\":\"preflight_runtime_b\",\"dispatcher_profile\":\"work_only\"}]}";
        return new DurablePreflightRequest(DurableRoleManifest.Parse(Encoding.UTF8.GetBytes(json)), Owner);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;
        private readonly NpgsqlDataSource _ownerDataSource;
        private readonly TestDirectory _manifestDirectory;
        private readonly string _manifestPath;
        private readonly DurablePreflightRequest _request;

        private Fixture(PostgreSqlContainer container, NpgsqlDataSource ownerDataSource, TestDirectory manifestDirectory, string manifestPath, DurablePreflightRequest request)
        {
            _container = container;
            _ownerDataSource = ownerDataSource;
            _manifestDirectory = manifestDirectory;
            _manifestPath = manifestPath;
            _request = request;
        }

        internal long AdvisoryLockKey => 4_707_181_168_775_217_740;

        internal string OwnerConnection => _container.GetConnectionString();

        internal DurablePreflightRequest Request => _request;

        internal static string DispatcherRole(int pairIndex) => pairIndex switch
        {
            1 => DispatcherA,
            2 => DispatcherB,
            _ => $"preflight_dispatcher_p{pairIndex:00}",
        };

        internal static string RuntimeRole(int pairIndex) => pairIndex switch
        {
            1 => RuntimeA,
            2 => RuntimeB,
            _ => $"preflight_runtime_p{pairIndex:00}",
        };

        internal static async Task<Fixture> CreateAsync(int pairCount = 2, int unrelatedRoleCount = 0)
        {
            Assert.InRange(pairCount, 1, 32);
            Assert.InRange(unrelatedRoleCount, 0, 512);
            var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var recipe = TestPathUtils.PathUnder(repositoryRoot, "Durable", "configure-postgresql-roles.sql");
            var container = new PostgreSqlBuilder(Image)
                .WithDatabase("appsurface_durable")
                .WithUsername("appsurface")
                .WithPassword("appsurface-test-password")
                .WithResourceMapping(recipe, "/tmp")
                .Build();
            await container.StartAsync();
            try
            {
                var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
                await new PostgreSqlDurableRuntimeSchemaManager(dataSource).ApplyAsync();
                var roles = new[] { Owner, "durable_retention" }
                    .Concat(Enumerable.Range(1, pairCount).SelectMany(index => new[] { DispatcherRole(index), RuntimeRole(index) }))
                    .Select(role => $"CREATE ROLE {role} LOGIN PASSWORD '{RolePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
                await using (var command = dataSource.CreateCommand(string.Join(';', roles)))
                {
                    await command.ExecuteNonQueryAsync();
                }

                var pairJson = Enumerable.Range(1, pairCount)
                    .Select(index => $"{{\"dispatcher\":\"{DispatcherRole(index)}\",\"runtime\":\"{RuntimeRole(index)}\",\"dispatcher_profile\":\"{(index == 1 ? "full" : "work_only")}\"}}");
                var json = $"{{\"version\":1,\"pairs\":[{string.Join(',', pairJson)}]}}";
                if (unrelatedRoleCount > 0)
                {
                    var noiseRoles = Enumerable.Range(1, unrelatedRoleCount)
                        .Select(index => $"CREATE ROLE issue845_noise_{index:000} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
                    await using var noise = dataSource.CreateCommand(string.Join(';', noiseRoles));
                    await noise.ExecuteNonQueryAsync();
                }
                var result = await container.ExecAsync([
                    "psql", "-U", "appsurface", "-d", "appsurface_durable",
                    "-v", $"migration_owner_role={Owner}",
                    "-v", "retention_operator_role=durable_retention",
                    "-v", $"role_pairs_json={json}",
                    "-f", "/tmp/configure-postgresql-roles.sql",
                ]).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result.ExitCode == 0, $"Canonical role recipe failed: {result.Stdout}\n{result.Stderr}");

                await using (var unrelated = dataSource.CreateCommand(
                    "CREATE ROLE preflight_unrelated LOGIN PASSWORD 'durable-preflight-test-password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS"))
                {
                    await unrelated.ExecuteNonQueryAsync();
                }

                var temp = TestDirectory.Create();
                var manifestPath = TestPathUtils.PathUnder(temp.Path, "role-pairs.json");
                await File.WriteAllTextAsync(manifestPath, json, new UTF8Encoding(false));
                var manifest = await DurableRoleManifest.ReadAsync(manifestPath, CancellationToken.None);
                return new Fixture(container, dataSource, temp, manifestPath, new DurablePreflightRequest(manifest, Owner));
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }

        internal async Task<DurablePreflightResult> RunAsync(
            string role,
            string? applicationName = null,
            string? options = null,
            CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnectionStringBuilder(OwnerConnection)
            {
                Username = role,
                Password = RolePassword,
                Pooling = true,
                Enlist = true,
                Multiplexing = true,
                MaxPoolSize = 1,
                ApplicationName = applicationName ?? $"issue845-{role}",
                Options = options,
            };
            return await new DurableSchemaCommandService().PreflightAsync(connection.ConnectionString, _request, cancellationToken);
        }

        internal async Task<CommandException> RunCommandAsync(string role)
        {
            const string variable = "ISSUE845_TEST_PREFLIGHT_CONNECTION";
            using var environment = new EnvironmentVariableScope(variable, RuntimeConnection(role, pooling: true, maxPoolSize: 1));
            using var console = new FakeInMemoryConsole();
            var command = new DurableSchemaPreflightCommand(new DurableSchemaCommandService())
            {
                RolePairsFile = _manifestPath,
                MigrationOwnerRole = Owner,
                ConnectionEnvironmentVariable = variable,
            };
            return await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
        }

        internal async Task DowngradeToSchemaTenAsync()
        {
            const string sql = """
                DROP POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat;
                DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention;
                DROP FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid);
                DELETE FROM appsurface_durable.schema_migration WHERE version = 11;
                UPDATE appsurface_durable.store_metadata
                SET schema_version = 10,
                    minimum_reader_version = 10,
                    maximum_reader_version = 10,
                    minimum_writer_version = 10,
                    maximum_writer_version = 10
                WHERE singleton;
                """;
            await using var command = _ownerDataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        internal string RuntimeConnection(string role, bool pooling, int maxPoolSize) => new NpgsqlConnectionStringBuilder(OwnerConnection)
        {
            Username = role,
            Password = RolePassword,
            Pooling = pooling,
            MaxPoolSize = maxPoolSize,
        }.ConnectionString;

        internal async ValueTask<DurableRuntimeSchemaStatus> ReadStatusAsync() =>
            await new PostgreSqlDurableRuntimeSchemaManager(_ownerDataSource).GetStatusAsync();

        internal async Task<string?> ReadCatalogSignatureAsync()
        {
            const string sql = """
                SELECT pg_catalog.md5(concat_ws('|',
                  COALESCE((SELECT string_agg(concat_ws(':', rolname, rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolcanlogin), ';' ORDER BY rolname)
                    FROM pg_catalog.pg_roles WHERE rolname LIKE 'preflight_%' OR rolname = 'durable_retention'), ''),
                  COALESCE((SELECT string_agg(concat_ws(':', relation.relname, policy.polname, policy.polcmd, policy.polpermissive, policy.polroles::text,
                    pg_catalog.pg_get_expr(policy.polqual, policy.polrelid), pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid)), ';' ORDER BY relation.relname, policy.polname)
                    FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class relation ON relation.oid = policy.polrelid
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'appsurface_durable'), ''),
                  COALESCE((SELECT string_agg(concat_ws(':', routine.proname, routine.proowner, routine.proacl::text, routine.proconfig::text, routine.prorettype, routine.proretset), ';' ORDER BY routine.proname, routine.oid)
                    FROM pg_catalog.pg_proc routine JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace WHERE namespace.nspname = 'appsurface_durable'), ''),
                  COALESCE((SELECT string_agg(concat_ws(':', relation.relname, relation.relowner, relation.relrowsecurity, relation.relforcerowsecurity), ';' ORDER BY relation.relname)
                    FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'appsurface_durable'), ''),
                  COALESCE((SELECT string_agg(concat_ws(':', membership.roleid, membership.member, membership.admin_option), ';' ORDER BY membership.roleid, membership.member)
                    FROM pg_catalog.pg_auth_members membership), '')))
                """;
            await using var command = _ownerDataSource.CreateCommand(sql);
            return await command.ExecuteScalarAsync() as string;
        }

        internal async Task<NpgsqlConnection> OpenOwnerConnectionAsync() => await _ownerDataSource.OpenConnectionAsync();

        internal async Task CreateUnreviewedCallerAsync()
        {
            await using var command = _ownerDataSource.CreateCommand(
                "CREATE ROLE preflight_unrelated LOGIN PASSWORD 'durable-preflight-test-password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException exception) when (exception.SqlState == "42710")
            {
                // The fixture creates the unreviewed caller during setup for direct negative-caller checks.
            }
        }

        internal async Task<bool> EvaluateRoleSetAsync(uint[]? targets, uint[] expected)
        {
            var sql = "SELECT COALESCE((" + DurableSchemaPreflightVerifier.ExactRoleSetSql + "), false) " +
                "FROM (SELECT @targets::oid[] AS polroles) policy " +
                "CROSS JOIN (SELECT @expected::oid[] AS oids) runtimes";
            await using var command = _ownerDataSource.CreateCommand(sql);
            var targetParameter = command.Parameters.Add("targets", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Oid);
            targetParameter.Value = targets is null ? DBNull.Value : targets;
            var expectedParameter = command.Parameters.Add("expected", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Oid);
            expectedParameter.Value = expected;
            return await command.ExecuteScalarAsync() is true;
        }

        internal async Task RecreateFunctionWithDefaultAclAsync(string regprocedure, string previousName)
        {
            await using var getDefinition = _ownerDataSource.CreateCommand("SELECT pg_catalog.pg_get_functiondef(@identity::regprocedure)");
            getDefinition.Parameters.AddWithValue("identity", regprocedure);
            var definition = await getDefinition.ExecuteScalarAsync() as string;
            Assert.NotNull(definition);
            Assert.Contains("CREATE OR REPLACE FUNCTION ", definition, StringComparison.Ordinal);
            var create = definition.Replace("CREATE OR REPLACE FUNCTION ", "CREATE FUNCTION ", StringComparison.Ordinal);
            var qualifiedName = regprocedure[..regprocedure.IndexOf('(')];
            var signature = regprocedure[regprocedure.IndexOf('(')..];
            await using (var rename = _ownerDataSource.CreateCommand($"ALTER FUNCTION {regprocedure} RENAME TO {previousName}"))
            {
                await rename.ExecuteNonQueryAsync();
            }
            await using (var createFunction = _ownerDataSource.CreateCommand(create))
            {
                await createFunction.ExecuteNonQueryAsync();
            }
            await using (var setOwner = _ownerDataSource.CreateCommand($"ALTER FUNCTION {qualifiedName}{signature} OWNER TO {Owner}"))
            {
                await setOwner.ExecuteNonQueryAsync();
            }
            await using var dropOld = _ownerDataSource.CreateCommand($"DROP FUNCTION appsurface_durable.{previousName}{signature}");
            await dropOld.ExecuteNonQueryAsync();
        }

        internal async Task<bool> HasNullFunctionAclAsync(string regprocedure)
        {
            await using var command = _ownerDataSource.CreateCommand("SELECT proacl IS NULL FROM pg_catalog.pg_proc WHERE oid = @identity::regprocedure");
            command.Parameters.AddWithValue("identity", regprocedure);
            return await command.ExecuteScalarAsync() is true;
        }

        internal async Task AssertNoFenceLocksAsync()
        {
            const string sql = "SELECT count(*) FROM pg_catalog.pg_locks WHERE locktype='advisory' " +
                "AND classid::bigint=(@key >> 32) AND objid::bigint=(@key & 4294967295) AND objsubid=1";
            await using var command = _ownerDataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        internal async Task<int> CountPreflightSessionsAsync() => await CountSessionsAsync("issue845-%", like: true);

        internal async Task<int> CountSessionsAsync(string applicationName, bool like = false)
        {
            await using var command = _ownerDataSource.CreateCommand(like
                ? "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE application_name LIKE @name"
                : "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE application_name=@name");
            command.Parameters.AddWithValue("name", applicationName);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task WaitForWaitEventAsync(string applicationName, string waitEvent)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                await using var command = _ownerDataSource.CreateCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@name AND wait_event=@wait_event)");
                command.Parameters.AddWithValue("name", applicationName);
                command.Parameters.AddWithValue("wait_event", waitEvent);
                if (await command.ExecuteScalarAsync() is true)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
            throw new TimeoutException($"PostgreSQL backend {applicationName} did not reach wait event {waitEvent}.");
        }

        internal async Task<int> WaitForBackendAsync(string applicationName)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                await using var command = _ownerDataSource.CreateCommand(
                    "SELECT pid FROM pg_catalog.pg_stat_activity WHERE application_name=@name");
                command.Parameters.AddWithValue("name", applicationName);
                var value = await command.ExecuteScalarAsync();
                if (value is int pid)
                {
                    return pid;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
            throw new TimeoutException($"PostgreSQL backend {applicationName} did not connect.");
        }

        internal async Task TerminateBackendAsync(int pid)
        {
            await using var command = _ownerDataSource.CreateCommand("SELECT pg_catalog.pg_terminate_backend(@pid)");
            command.Parameters.AddWithValue("pid", pid);
            Assert.True(await command.ExecuteScalarAsync() is true);
        }

        internal async Task MutateAsync(string mutation)
        {
            await using var command = _ownerDataSource.CreateCommand(mutation);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _ownerDataSource.DisposeAsync();
            await _container.DisposeAsync();
            _manifestDirectory.Dispose();
        }
    }
}
