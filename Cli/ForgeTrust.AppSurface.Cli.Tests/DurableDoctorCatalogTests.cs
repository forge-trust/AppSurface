using ForgeTrust.AppSurface.Cli;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies bounded doctor projections and their PostgreSQL catalog predicates.</summary>
public sealed class DurableDoctorCatalogTests
{
    [Fact]
    public void Credential_projection_maps_the_fixed_order_and_returns_a_read_only_copy()
    {
        var failures = DurableDoctorCatalog.MapCredentialProjection([false, true, false, true, false, true]);

        Assert.Equal(
            ["caller-role", "role-membership", "role-grant-options"],
            failures);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)failures)[0] = "changed");
    }

    [Fact]
    public void Retention_projection_maps_the_fixed_order_and_returns_a_read_only_copy()
    {
        var failures = DurableDoctorCatalog.MapRetentionProjection([false, true, false, true, false, true, false, true, false]);

        Assert.Equal(
            ["function-signature", "function-security-definer", "function-execute", "function-grant-option", "retention-index-shape"],
            failures);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)failures)[0] = "changed");
    }

    [Fact]
    public void Missing_or_malformed_projections_fail_with_one_fixed_unexpected_contract_error()
    {
        var cases = new (object?[]? Projection, bool Credential)[]
        {
            (null, true),
            ([], true),
            ([true, true, true, true, true, true, true], true),
            ([true, true, null, true, true, true], true),
            (["secret catalog value", true, true, true, true, true], true),
            (null, false),
            ([true, true, true, true, true, true, true, true], false),
            ([true, true, true, true, true, true, true, true, true, true], false),
            ([true, true, true, true, true, true, true, null, true], false),
            ([true, true, true, true, true, true, true, true, "secret index definition"], false),
        };

        foreach (var (projection, credential) in cases)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                if (credential)
                {
                    DurableDoctorCatalog.MapCredentialProjection(projection);
                }
                else
                {
                    DurableDoctorCatalog.MapRetentionProjection(projection);
                }
            });

            Assert.Equal("The Durable doctor catalog projection was malformed.", exception.Message);
            Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Canonical_runtime_credential_and_schema_eleven_retention_catalog_pass()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();

        Assert.Empty(await ReadCredentialAsync(fixture));
        Assert.Empty(await ReadRetentionAsync(fixture));
    }

    [Fact]
    public async Task Missing_exact_retention_signature_is_the_only_function_finding()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync(
            "ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) RENAME TO issue801_missing_prune");

        Assert.Equal(["function-signature"], await ReadRetentionAsync(fixture));
    }

    [Fact]
    public async Task Runtime_role_ownership_of_another_database_fails_role_ownership()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var databaseName = $"doctor_owned_{Guid.NewGuid():N}";
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        try
        {
            await fixture.MutateAsync(
                $"CREATE DATABASE {quotedDatabaseName} OWNER {QuoteIdentifier(fixture.RuntimeRole)}");

            Assert.Equal(["role-ownership"], await ReadCredentialAsync(fixture));
        }
        finally
        {
            await fixture.MutateAsync($"DROP DATABASE IF EXISTS {quotedDatabaseName}");
        }
    }

    [Theory]
    [InlineData("CREATEDB")]
    [InlineData("CREATEROLE")]
    [InlineData("REPLICATION")]
    [InlineData("BYPASSRLS")]
    [InlineData("SUPERUSER")]
    public async Task Unsafe_runtime_role_attributes_fail_the_fixed_attribute_check(string attribute)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync($"ALTER ROLE {QuoteIdentifier(fixture.RuntimeRole)} {attribute}");

        Assert.Contains("role-attributes", await ReadCredentialAsync(fixture));
    }

    [Fact]
    public async Task Runtime_without_login_still_reports_the_attribute_failure_on_its_open_session()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await fixture.MutateAsync($"ALTER ROLE {QuoteIdentifier(fixture.RuntimeRole)} NOLOGIN");

        Assert.Equal(["role-attributes"], await ReadCredentialAsync(connection));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Membership_in_either_direction_is_rejected(bool runtime_is_member)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var grantedRole = QuoteIdentifier(runtime_is_member ? fixture.DispatcherRole : fixture.RuntimeRole);
        var memberRole = QuoteIdentifier(runtime_is_member ? fixture.RuntimeRole : fixture.DispatcherRole);
        await fixture.MutateAsync($"GRANT {grantedRole} TO {memberRole}");

        Assert.Equal(["role-membership"], await ReadCredentialAsync(fixture));
    }

    [Fact]
    public async Task Set_role_is_detected_as_a_session_and_current_user_mismatch()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync(
            $"GRANT {QuoteIdentifier(fixture.DispatcherRole)} TO {QuoteIdentifier(fixture.RuntimeRole)}");
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await using (var setRole = new NpgsqlCommand($"SET ROLE {QuoteIdentifier(fixture.DispatcherRole)}", connection))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        Assert.Equal(["caller-role", "role-membership"], await ReadCredentialAsync(connection));
    }

    [Fact]
    public async Task Missing_retention_index_fails_presence_only()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync("DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention");

        Assert.Equal(["retention-index-presence"], await ReadRetentionAsync(fixture));
    }

    [Theory]
    [InlineData("descending", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at DESC, worker_id)", "retention-index-shape")]
    [InlineData("unique", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE UNIQUE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id)", "retention-index-shape")]
    [InlineData("include", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id) INCLUDE (draining)", "retention-index-shape")]
    [InlineData("predicate", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id) WHERE draining IS FALSE", "retention-index-shape")]
    [InlineData("expression", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, (pg_catalog.length(worker_id)))", "retention-index-shape")]
    [InlineData("wrong-key-order", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (worker_id, last_heartbeat_at)", "retention-index-shape")]
    [InlineData("nondefault-opclass", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id pg_catalog.text_pattern_ops)", "retention-index-shape")]
    [InlineData("nondefault-collation", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id COLLATE \"C\")", "retention-index-shape")]
    [InlineData("null-order", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at NULLS FIRST, worker_id)", "retention-index-shape")]
    [InlineData("wrong-table", "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.store_metadata (singleton)", "retention-index-presence")]
    [InlineData("renamed", "ALTER INDEX appsurface_durable.ix_runtime_heartbeat_retention RENAME TO issue801_renamed_retention_index", "retention-index-presence")]
    public async Task Retention_index_mutations_report_only_the_fixed_presence_or_shape_check(
        string mutationName, string mutation, string expectedCategory)
    {
        Assert.False(string.IsNullOrWhiteSpace(mutationName));
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync(mutation);

        Assert.Equal([expectedCategory], await ReadRetentionAsync(fixture));
    }

    [Fact]
    public async Task Canonical_retention_catalog_is_independent_of_session_search_path()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();

        Assert.Empty(await ReadRetentionAsync(fixture, "-c search_path=appsurface_durable,pg_catalog"));
    }

    [Fact]
    public async Task Complete_preflight_accepts_the_canonical_index_with_a_nondefault_session_search_path()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var manifestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            pairs = new[]
            {
                new
                {
                    dispatcher = fixture.DispatcherRole,
                    runtime = fixture.RuntimeRole,
                    dispatcher_profile = "full",
                },
            },
        });
        var request = new DurablePreflightRequest(
            DurableRoleManifest.Parse(manifestBytes), fixture.OwnerRole);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString)
        {
            Options = "-c search_path=appsurface_durable,pg_catalog",
        }.ConnectionString;

        var result = await DurableSchemaPreflightVerifier.VerifyAsync(
            connectionString, request, CancellationToken.None);

        Assert.Empty(result.FailedChecks);
    }

    [Fact]
    public async Task Effective_column_grant_option_is_detected_for_durable_relations()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var runtime = QuoteIdentifier(fixture.RuntimeRole);
        await fixture.MutateAsync(
            $"GRANT UPDATE (last_heartbeat_at) ON appsurface_durable.runtime_heartbeat TO {runtime} WITH GRANT OPTION");

        Assert.Equal(["role-grant-options"], await ReadCredentialAsync(fixture));
    }

    [Fact]
    public async Task Effective_delete_or_truncate_on_the_heartbeat_table_is_rejected()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var runtime = QuoteIdentifier(fixture.RuntimeRole);
        await fixture.MutateAsync(
            $"GRANT DELETE ON appsurface_durable.runtime_heartbeat TO {runtime}");

        Assert.Equal(["heartbeat-table-privileges"], await ReadCredentialAsync(fixture));
    }

    [Fact]
    public async Task Retention_runtime_acl_separates_public_and_effective_grant_option_findings()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync(
            "GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) TO PUBLIC");

        Assert.Equal(["function-public-acl"], await ReadRetentionAsync(fixture));

        await fixture.MutateAsync($"""
            REVOKE EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)
                TO {QuoteIdentifier(fixture.RuntimeRole)} WITH GRANT OPTION
            """);

        Assert.Equal(["function-execute", "function-grant-option"], await ReadRetentionAsync(fixture));
    }

    [Fact]
    public async Task Null_function_acl_expands_postgresql_default_public_execute_privilege()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync($"""
            DROP FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid);
            CREATE FUNCTION appsurface_durable.prune_runtime_heartbeats(
                p_retention interval,
                p_maximum_rows integer,
                p_current_worker_id text,
                p_current_worker_instance_id uuid)
            RETURNS integer
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = pg_catalog, appsurface_durable, pg_temp
            AS $$ SELECT 0::integer $$;
            ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)
                OWNER TO {QuoteIdentifier(fixture.OwnerRole)}
            """);

        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync();
        await using var inspect = new NpgsqlCommand("""
            SELECT routine.proacl IS NULL
            FROM pg_catalog.pg_proc routine
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace
            WHERE namespace.nspname = 'appsurface_durable'
              AND routine.proname = 'prune_runtime_heartbeats'
            """, connection);
        Assert.True((bool?)await inspect.ExecuteScalarAsync());

        Assert.Equal(["function-execute", "function-public-acl"], await ReadRetentionAsync(fixture));
    }

    [Fact]
    public async Task Doctor_compares_function_owner_to_schema_owner_without_preflight_authority()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.MutateAsync($"""
            ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)
                OWNER TO {QuoteIdentifier(fixture.DispatcherRole)};
            GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)
                TO {QuoteIdentifier(fixture.RuntimeRole)}
            """);

        Assert.Equal(["function-owner"], await ReadRetentionAsync(fixture));
    }

    private static async Task<IReadOnlyList<string>> ReadCredentialAsync(DurableDoctorFixture fixture)
    {
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        return await ReadCredentialAsync(connection);
    }

    private static async Task<IReadOnlyList<string>> ReadCredentialAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await SetReadOnlyAsync(connection, transaction);
        var failures = await DurableDoctorCatalog.ReadCredentialAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
        return failures;
    }

    private static string QuoteIdentifier(string identifier)
    {
        using var commandBuilder = new NpgsqlCommandBuilder();
        return commandBuilder.QuoteIdentifier(identifier);
    }

    private static async Task<IReadOnlyList<string>> ReadRetentionAsync(DurableDoctorFixture fixture, string? options = null)
    {
        var connectionString = fixture.RuntimeConnectionString;
        if (options is not null)
        {
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Options = options }.ConnectionString;
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await SetReadOnlyAsync(connection, transaction);
        var failures = await DurableDoctorCatalog.ReadRetentionAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
        return failures;
    }

    private static async Task SetReadOnlyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
