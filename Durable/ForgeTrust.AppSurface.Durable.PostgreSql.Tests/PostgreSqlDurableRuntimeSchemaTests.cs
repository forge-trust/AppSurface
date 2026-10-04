using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimeSchemaTests
{
    [Fact]
    public async Task MigrationTwelveRaisesAppliedAndGeneratedFloorsAndRejectsOlderCatalogs()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var currentMigrations = DurablePostgreSqlMigrationCatalog.Load();
        var current = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, currentMigrations);

        var applied = await current.ApplyAsync();
        var status = await current.GetStatusAsync();
        var migrationTwelveScript = current.GenerateScript(fromVersion: 11);

        Assert.Equal(12, currentMigrations.Count);
        Assert.Equal(12, PostgreSqlDurableRuntimeSchemaManager.RequiredVersion);
        Assert.Equal(12, status.MinimumReaderVersion);
        Assert.Equal(12, status.MinimumWriterVersion);
        Assert.Contains("minimum_reader_version = GREATEST(minimum_reader_version, 12)", migrationTwelveScript, StringComparison.Ordinal);
        Assert.Contains("minimum_writer_version = GREATEST(minimum_writer_version, 12)", migrationTwelveScript, StringComparison.Ordinal);
        Assert.Contains(12, applied.AppliedVersions);

        var oldCatalog = currentMigrations.Take(11).ToArray();
        var oldBinary = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, oldCatalog);
        var refused = await oldBinary.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, refused.Compatibility);
        await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () => await oldBinary.ValidateAsync());
        await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () => await oldBinary.ApplyAsync());

        const string futureSql = "SELECT 1;";
        var futureHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(futureSql)));
        var futureMigration = new DurablePostgreSqlMigration(13, "future_floor_probe", futureSql, futureHash);
        var futureCatalog = currentMigrations.Append(futureMigration).ToArray();
        var future = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, futureCatalog);
        var futureScript = future.GenerateScript(fromVersion: 12);

        Assert.Contains("Migration 0013_future_floor_probe", futureScript, StringComparison.Ordinal);
        Assert.Contains("minimum_reader_version = GREATEST(minimum_reader_version, 12)", futureScript, StringComparison.Ordinal);
        Assert.Contains("minimum_writer_version = GREATEST(minimum_writer_version, 12)", futureScript, StringComparison.Ordinal);

        await future.ApplyAsync();
        await using var metadata = database.DataSource.CreateCommand(
            "SELECT minimum_reader_version, minimum_writer_version FROM appsurface_durable.store_metadata WHERE singleton;");
        await using var reader = await metadata.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(12, reader.GetInt32(0));
        Assert.Equal(12, reader.GetInt32(1));
    }

    [Fact]
    public async Task ExecutionPolicyCheckRejectsNullSchemaAndNullCircuitDuration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        await CreateScopeAsync(database.DataSource);

        var missingSchema = await Assert.ThrowsAsync<PostgresException>(
            async () => await InsertWorkAsync(
                database.DataSource,
                "missing-schema",
                schema: null,
                planVersion: "attempt-plan-v1",
                offsets: [0],
                circuitMicroseconds: 3_600_000_000,
                deadline: new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missingSchema.SqlState);
        Assert.Equal("ck_work_execution_policy_shape", missingSchema.ConstraintName);

        var missingCircuit = await Assert.ThrowsAsync<PostgresException>(
            async () => await InsertWorkAsync(
                database.DataSource,
                "missing-circuit",
                schema: "work-execution-v1",
                planVersion: "attempt-plan-v1",
                offsets: [0],
                circuitMicroseconds: null,
                deadline: null));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missingCircuit.SqlState);
        Assert.Equal("ck_work_execution_policy_shape", missingCircuit.ConstraintName);

        await InsertWorkAsync(
            database.DataSource,
            "valid-deadline-only",
            schema: "work-execution-v1",
            planVersion: null,
            offsets: null,
            circuitMicroseconds: null,
            deadline: new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        await InsertWorkAsync(
            database.DataSource,
            "valid-legacy",
            schema: null,
            planVersion: null,
            offsets: null,
            circuitMicroseconds: null,
            deadline: null);
    }

    [Fact]
    public async Task FixtureClockIsIsolatedAndRestrictedUnderRealCredentialsAndHostileSearchPath()
    {
        await using var firstDatabase = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var secondDatabase = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(firstDatabase.DataSource).ApplyAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(secondDatabase.DataSource).ApplyAsync();

        var firstTime = new DateTimeOffset(2026, 10, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560);
        var secondTime = firstTime.AddHours(3);
        await firstDatabase.SetExecutionTimeAsync(firstTime);
        await secondDatabase.SetExecutionTimeAsync(secondTime);

        Assert.Equal(firstTime, await ReadClockAsync(firstDatabase.DataSource));
        Assert.Equal(secondTime, await ReadClockAsync(secondDatabase.DataSource));

        var reader = await ReadClockOwnerAsync(firstDatabase.DataSource);
        Assert.False(reader.CanLogin);
        Assert.False(reader.IsSuperuser);
        Assert.False(reader.CanCreateRole);
        Assert.False(reader.CanCreateDatabase);
        Assert.False(reader.BypassesRowLevelSecurity);
        Assert.True(reader.CanSelectClock);
        Assert.False(reader.CanUpdateClock);
        Assert.False(reader.CanCreateInDurableSchema);
        Assert.False(reader.PublicCanExecute);
        Assert.True(reader.IsSecurityDefiner);
        Assert.Equal('v', reader.Volatility);
        Assert.Contains("search_path=pg_catalog", reader.Configuration);

        var runtimeRole = $"clock_probe_{Guid.NewGuid():N}";
        var runtimePassword = $"pw{Guid.NewGuid():N}";
        NpgsqlDataSource? runtimeDataSource = null;
        try
        {
            await ExecuteAsync(
                firstDatabase.DataSource,
                $"CREATE ROLE {runtimeRole} LOGIN PASSWORD '{runtimePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;");
            await ExecuteAsync(firstDatabase.DataSource, $"GRANT USAGE ON SCHEMA appsurface_durable TO {runtimeRole};");
            await ExecuteAsync(
                firstDatabase.DataSource,
                $"GRANT EXECUTE ON FUNCTION appsurface_durable.work_execution_now() TO {runtimeRole};");

            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(firstDatabase.ConnectionString)
            {
                Username = runtimeRole,
                Password = runtimePassword,
            };
            runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString.ConnectionString);
            Assert.Equal(firstTime, await ReadClockAsync(runtimeDataSource));

            await using (var hostile = await runtimeDataSource.OpenConnectionAsync())
            {
                await using (var temp = new NpgsqlCommand(
                    "CREATE TEMP TABLE execution_clock (clock_value timestamp with time zone); INSERT INTO pg_temp.execution_clock VALUES ('2000-01-01 00:00:00+00'); SET search_path = pg_temp, public;",
                    hostile))
                {
                    await temp.ExecuteNonQueryAsync();
                }

                await using var read = new NpgsqlCommand("SELECT appsurface_durable.work_execution_now();", hostile);
                Assert.Equal(firstTime, new DateTimeOffset((DateTime)(await read.ExecuteScalarAsync())!, TimeSpan.Zero));
            }

            var deniedRead = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ScalarAsync(runtimeDataSource, "SELECT clock_value FROM appsurface_test_clock.execution_clock WHERE singleton;"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedRead.SqlState);
            var deniedUpdate = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecuteAsync(runtimeDataSource, "UPDATE appsurface_test_clock.execution_clock SET clock_value = clock_timestamp();"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedUpdate.SqlState);
            var deniedAlter = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecuteAsync(
                    runtimeDataSource,
                    $"ALTER FUNCTION appsurface_durable.work_execution_now() OWNER TO {runtimeRole};"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedAlter.SqlState);
            var deniedSetRole = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecuteAsync(runtimeDataSource, $"SET ROLE {reader.Name};"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedSetRole.SqlState);
        }
        finally
        {
            if (runtimeDataSource is not null)
            {
                await runtimeDataSource.DisposeAsync();
                await ExecuteAsync(firstDatabase.DataSource, $"DROP OWNED BY {runtimeRole};");
                await ExecuteAsync(firstDatabase.DataSource, $"DROP ROLE {runtimeRole};");
            }
        }
    }

    private static async Task CreateScopeAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', 'execution-schema-tests', true);",
            connection,
            transaction))
        {
            await setScope.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO appsurface_durable.scope (scope_id) VALUES ('execution-schema-tests');",
            connection,
            transaction);
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task InsertWorkAsync(
        NpgsqlDataSource dataSource,
        string workId,
        string? schema,
        string? planVersion,
        long[]? offsets,
        long? circuitMicroseconds,
        DateTimeOffset? deadline)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var setScope = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', 'execution-schema-tests', true);",
                connection,
                transaction))
            {
                await setScope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO appsurface_durable.work
                (
                    scope_id, work_id, activity_id, command_id, idempotency_key, work_name, work_version,
                    contract_id, payload_schema_version, codec_id, payload, payload_sha256, payload_classification,
                    payload_retention, request_fingerprint_schema, request_fingerprint_sha256, state, provider_safety,
                    due_at, scope_generation, runtime_epoch, maximum_attempts, maximum_elapsed, backoff_algorithm,
                    initial_retry_delay, maximum_retry_delay, lease_duration, lease_renewal_cadence,
                    maximum_lease_lifetime, execution_policy_schema, attempt_plan_version, attempt_plan_offsets,
                    maximum_circuit_microseconds, execution_not_after
                )
                VALUES
                (
                    'execution-schema-tests', @work_id, @activity_id, @command_id, @idempotency_key,
                    'schema-test', 'v1', 'tests.payload', 'v1', 'tests.codec', decode('01', 'hex'),
                    decode(repeat('00', 32), 'hex'), 'approved', 'tests.retention', 'fingerprint-v1',
                    repeat('0', 64), 'pending', 'idempotent', pg_catalog.clock_timestamp(), 1, @epoch,
                    @maximum_attempts, interval '4 hours', 'exponential-v1', interval '1 second',
                    interval '10 seconds', interval '30 seconds', interval '5 seconds', interval '2 minutes',
                    @schema, @plan_version, @offsets, @circuit_microseconds, @deadline
                );
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("work_id", workId);
            command.Parameters.AddWithValue("activity_id", $"activity-{workId}");
            command.Parameters.AddWithValue("command_id", $"command-{workId}");
            command.Parameters.AddWithValue("idempotency_key", $"key-{workId}");
            command.Parameters.AddWithValue("epoch", Guid.NewGuid());
            command.Parameters.AddWithValue("maximum_attempts", offsets?.Length ?? 1);
            command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema ?? (object)DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("plan_version", NpgsqlDbType.Text) { Value = planVersion ?? (object)DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("offsets", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = offsets ?? (object)DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("circuit_microseconds", NpgsqlDbType.Bigint) { Value = circuitMicroseconds ?? (object)DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("deadline", NpgsqlDbType.TimestampTz) { Value = deadline?.ToUniversalTime() ?? (object)DBNull.Value });
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<DateTimeOffset> ReadClockAsync(NpgsqlDataSource dataSource)
    {
        var value = (DateTime)(await ScalarAsync(dataSource, "SELECT appsurface_durable.work_execution_now();"))!;
        return new DateTimeOffset(value, TimeSpan.Zero);
    }

    private static async Task<ClockOwner> ReadClockOwnerAsync(NpgsqlDataSource dataSource)
    {
        const string sql = """
            SELECT owner.rolname, owner.rolcanlogin, owner.rolsuper, owner.rolcreaterole, owner.rolcreatedb,
                   owner.rolbypassrls,
                   pg_catalog.has_table_privilege(owner.oid, 'appsurface_test_clock.execution_clock', 'SELECT'),
                   pg_catalog.has_table_privilege(owner.oid, 'appsurface_test_clock.execution_clock', 'UPDATE'),
                   pg_catalog.has_schema_privilege(owner.oid, 'appsurface_durable', 'CREATE'),
                   pg_catalog.has_function_privilege('public', routine.oid, 'EXECUTE'),
                   routine.prosecdef, routine.provolatile, routine.proconfig
            FROM pg_catalog.pg_proc AS routine
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
            JOIN pg_catalog.pg_roles AS owner ON owner.oid = routine.proowner
            WHERE namespace.nspname = 'appsurface_durable' AND routine.proname = 'work_execution_now';
            """;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ClockOwner(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10),
            reader.GetChar(11),
            reader.GetFieldValue<string[]>(12));
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ClockOwner(
        string Name,
        bool CanLogin,
        bool IsSuperuser,
        bool CanCreateRole,
        bool CanCreateDatabase,
        bool BypassesRowLevelSecurity,
        bool CanSelectClock,
        bool CanUpdateClock,
        bool CanCreateInDurableSchema,
        bool PublicCanExecute,
        bool IsSecurityDefiner,
        char Volatility,
        string[] Configuration);
}
