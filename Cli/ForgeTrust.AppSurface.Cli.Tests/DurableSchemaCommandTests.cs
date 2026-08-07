using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies durable schema CLI guards, safe output, and deterministic offline script behavior.</summary>
public sealed class DurableSchemaCommandTests
{
    [Fact]
    public async Task Status_reads_connection_from_named_environment_variable_without_printing_it()
    {
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.Compatible, 6, 6, []),
        };
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_SCHEMA_TEST", secretConnection);
        var command = new DurableSchemaStatusCommand(service)
        {
            ConnectionEnvironmentVariable = "APPSURFACE_DURABLE_SCHEMA_TEST",
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        Assert.Equal(secretConnection, service.ConnectionString);
        Assert.True(service.CancellationToken.CanBeCanceled);
        Assert.Contains("Compatibility: Compatible", console.ReadOutputString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, console.ReadOutputString());
    }

    [Theory]
    [InlineData(DurableRuntimeSchemaCompatibility.Missing)]
    [InlineData(DurableRuntimeSchemaCompatibility.UpgradeRequired)]
    [InlineData(DurableRuntimeSchemaCompatibility.StoreTooNew)]
    [InlineData(DurableRuntimeSchemaCompatibility.Inconsistent)]
    public async Task Preflight_fails_with_one_safe_problem_cause_fix_and_docs_block(DurableRuntimeSchemaCompatibility compatibility)
    {
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(compatibility, 2, 6, [3, 4, 5, 6]),
        };
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", "Host=localhost;Password=do-not-print");
        var command = new DurableSchemaPreflightCommand(service);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Equal(1, error.Message.Split("Problem:", StringSplitOptions.None).Length - 1);
        Assert.Contains("Cause:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fix:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs:", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("do-not-print", error.Message);
    }

    [Fact]
    public async Task Apply_requires_explicit_confirmation_before_reading_the_connection_variable()
    {
        var service = new FakeDurableSchemaCommandService
        {
            ApplyResult = new DurableSchemaApplyView(2, 6, [3, 4, 5, 6]),
        };
        var command = new DurableSchemaApplyCommand(service);
        using var console = new FakeInMemoryConsole();

        var confirmationError = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
        Assert.Contains("disabled by default", confirmationError.Message, StringComparison.Ordinal);
        Assert.Null(service.ConnectionString);

        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", "Host=localhost;Password=do-not-print");
        command.Apply = true;
        await command.ExecuteAsync(console);

        Assert.Contains("2 -> 6", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("0003, 0004, 0005, 0006", console.ReadOutputString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("do-not-print", console.ReadOutputString());
    }

    [Fact]
    public async Task Script_is_offline_and_atomically_protects_existing_output_without_force()
    {
        var service = new FakeDurableSchemaCommandService { Script = "-- durable 0001 through 0006\nSELECT 1;\n" };
        var command = new DurableSchemaScriptCommand(service) { FromVersion = 2 };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        Assert.Equal(service.Script, console.ReadOutputString());
        Assert.Equal(2, service.FromVersion);
        Assert.Null(service.ConnectionString);
        Assert.False(service.OnlineOperationCalled);

        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-");
        var path = Path.Combine(directory.Path, "reviewed.sql");
        await File.WriteAllTextAsync(path, "existing");
        command.OutputPath = path;

        var existingError = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
        Assert.Contains("already exists", existingError.Message, StringComparison.Ordinal);
        Assert.Equal("existing", await File.ReadAllTextAsync(path));

        command.Force = true;
        await command.ExecuteAsync(console);

        Assert.Equal(service.Script, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("APPSURFACE-NOT-A-VALID-NAME")]
    [InlineData("APPSURFACE_MISSING_DURABLE_CONNECTION")]
    public async Task Online_commands_reject_missing_or_invalid_connection_environment_before_provider_work(string variable)
    {
        using var cleared = variable.Length == 0 ? null : new EnvironmentVariableScope(variable, null);
        var service = new FakeDurableSchemaCommandService();
        var command = new DurableSchemaStatusCommand(service) { ConnectionEnvironmentVariable = variable };
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.False(service.OnlineOperationCalled);
        Assert.DoesNotContain("connection string", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Online_commands_map_provider_failures_without_exposing_provider_text()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        const string serverDetail = "server-sentinel-detail";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            StatusException = new NpgsqlException(serverDetail),
        };
        var command = new DurableSchemaStatusCommand(service);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains("database operation failed", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, error.Message);
        ValueSafeAssert.DoesNotExpose(serverDetail, error.Message);
    }

    [Theory]
    [InlineData(DurableRuntimeSchemaCompatibility.Missing)]
    [InlineData(DurableRuntimeSchemaCompatibility.UpgradeRequired)]
    [InlineData(DurableRuntimeSchemaCompatibility.StoreTooNew)]
    [InlineData(DurableRuntimeSchemaCompatibility.Inconsistent)]
    public async Task Online_commands_map_schema_incompatibility_without_exposing_provider_details(
        DurableRuntimeSchemaCompatibility compatibility)
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        var status = new DurableRuntimeSchemaStatus(
            compatibility,
            Guid.NewGuid(),
            activeRuntimeEpoch: null,
            installedVersion: 2,
            requiredVersion: 6,
            minimumReaderVersion: 1,
            maximumReaderVersion: 6,
            minimumWriterVersion: 1,
            maximumWriterVersion: 6,
            appliedVersions: [1, 2],
            pendingVersions: [3, 4, 5, 6],
            problem: "server-sentinel-detail");
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            StatusException = new DurableRuntimeSchemaException(status),
        };
        var command = new DurableSchemaStatusCommand(service);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains($"Durable schema is {compatibility}", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, error.Message);
        ValueSafeAssert.DoesNotExpose("server-sentinel-detail", error.Message);
    }

    [Fact]
    public async Task Online_commands_map_timeouts_without_exposing_connection_values()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            StatusException = new TimeoutException("server-sentinel-detail"),
        };
        var command = new DurableSchemaStatusCommand(service);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains("timed out after its 30-second deadline", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, error.Message);
        ValueSafeAssert.DoesNotExpose("server-sentinel-detail", error.Message);
    }

    [Fact]
    public async Task Online_commands_map_cancellation_without_exposing_connection_values()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await TestableDurableSchemaOnlineCommand.RunAsync(
                secretConnection,
                cancellationSource.Token,
                static async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.Compatible, 6, 6, []);
                }));

        Assert.Contains("was canceled or exceeded its 30-second deadline", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, error.Message);
    }

    [Fact]
    public async Task Online_commands_map_invalid_connection_configuration_without_exposing_connection_values()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await TestableDurableSchemaOnlineCommand.RunAsync(
                secretConnection,
                CancellationToken.None,
                static (_, _) => ValueTask.FromException<DurableSchemaStatusView>(
                    new ArgumentException("provider-sentinel-detail"))));

        Assert.Contains("connection configuration is invalid", error.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, error.Message);
        ValueSafeAssert.DoesNotExpose("provider-sentinel-detail", error.Message);
    }

    [Fact]
    public async Task Status_and_preflight_render_the_compatible_contract()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.Compatible, 6, 6, []),
        };
        using var statusConsole = new FakeInMemoryConsole();
        using var preflightConsole = new FakeInMemoryConsole();

        await new DurableSchemaStatusCommand(service).ExecuteAsync(statusConsole);
        await new DurableSchemaPreflightCommand(service).ExecuteAsync(preflightConsole);

        Assert.Contains("Compatibility: Compatible", statusConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("Installed: 6", statusConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("Required: 6", statusConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("Pending: none", statusConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("Compatible: durable schema 6; runtime requires 6.", preflightConsole.ReadOutputString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, statusConsole.ReadOutputString());
        ValueSafeAssert.DoesNotExpose(secretConnection, preflightConsole.ReadOutputString());
    }

    [Fact]
    public async Task Apply_renders_a_safe_noop_result()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            ApplyResult = new DurableSchemaApplyView(6, 6, []),
        };
        var command = new DurableSchemaApplyCommand(service) { Apply = true };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        Assert.Contains("Durable schema: 6 -> 6; applied: none.", console.ReadOutputString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, console.ReadOutputString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task Script_rejects_versions_outside_the_current_catalog(int fromVersion)
    {
        var command = new DurableSchemaScriptCommand(new DurableSchemaCommandService()) { FromVersion = fromVersion };
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains("--from-version must be between 0 and the current durable migration version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_at_the_current_version_contains_only_the_deterministic_advisory_lock_boundary()
    {
        var service = new DurableSchemaCommandService();
        var command = new DurableSchemaScriptCommand(service) { FromVersion = 6 };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var script = console.ReadOutputString();
        Assert.Contains("SELECT pg_advisory_lock", script, StringComparison.Ordinal);
        Assert.Contains("SELECT pg_advisory_unlock", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Migration 000", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_output_cancellation_publishes_no_destination_or_temporary_file()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-canceled-");
        var path = Path.Combine(directory.Path, "canceled.sql");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(path, "SELECT 1;", force: false, cancellationSource.Token));

        Assert.Contains("was canceled", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_output_pre_cancellation_does_not_create_a_missing_parent_directory()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-canceled-parent-");
        var missingParent = Path.Combine(directory.Path, "missing");
        var path = Path.Combine(missingParent, "canceled.sql");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(path, "SELECT 1;", force: false, cancellationSource.Token));

        Assert.Contains("was canceled", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missingParent));
    }

    [Fact]
    public async Task Script_output_force_publishes_only_complete_scripts_when_two_writers_race()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-race-");
        var path = Path.Combine(directory.Path, "reviewed.sql");
        await File.WriteAllTextAsync(path, "original");
        const string firstScript = "SELECT 'first';\n";
        const string secondScript = "SELECT 'second';\n";
        using var writersReady = new CountdownEvent(2);
        using var publishRelease = new ManualResetEventSlim(initialState: false);
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var reading = 1;

        Task<string> StartWrite(string script) => Task.Run(async () =>
        {
            using var publishHook = DurableSchemaScriptOutput.UseTemporaryFileWrittenHookForTesting(
                () =>
                {
                    writersReady.Signal();
                    publishRelease.Wait();
                });
            return await DurableSchemaScriptOutput.WriteAsync(path, script, force: true, CancellationToken.None);
        });

        var writes = new[] { StartWrite(firstScript), StartWrite(secondScript) };
        Task? reader = null;
        var publishedPaths = Array.Empty<string>();
        try
        {
            Assert.True(writersReady.Wait(TimeSpan.FromSeconds(5)), "Both writers should reach the final publication window.");
            reader = Task.Run(() =>
            {
                while (Volatile.Read(ref reading) == 1)
                {
                    observations.Enqueue(File.ReadAllText(path));
                }
            });
        }
        finally
        {
            publishRelease.Set();
            try
            {
                publishedPaths = await Task.WhenAll(writes);
            }
            finally
            {
                Interlocked.Exchange(ref reading, 0);
                if (reader is not null)
                {
                    await reader;
                }
            }
        }

        var published = await File.ReadAllTextAsync(path);

        Assert.All(publishedPaths, publishedPath => Assert.Equal(path, publishedPath));
        Assert.Contains(published, new[] { firstScript, secondScript });
        Assert.NotEmpty(observations);
        Assert.All(observations, observed => Assert.Contains(observed, new[] { "original", firstScript, secondScript }));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_output_without_force_preserves_a_target_created_before_publication()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-target-race-");
        var path = Path.Combine(directory.Path, "reviewed.sql");
        using var publishHook = DurableSchemaScriptOutput.UseTemporaryFileWrittenHookForTesting(
            () => File.WriteAllText(path, "concurrent target"));

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(path, "SELECT 1;", force: false, CancellationToken.None));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("concurrent target", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_output_cancellation_after_temporary_write_preserves_the_existing_target()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-post-write-cancel-");
        var path = Path.Combine(directory.Path, "reviewed.sql");
        await File.WriteAllTextAsync(path, "existing");
        using var cancellationSource = new CancellationTokenSource();
        using var publishHook = DurableSchemaScriptOutput.UseTemporaryFileWrittenHookForTesting(cancellationSource.Cancel);

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(path, "SELECT 1;", force: true, cancellationSource.Token));

        Assert.Contains("was canceled", error.Message, StringComparison.Ordinal);
        Assert.Equal("existing", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_output_directory_destination_reports_a_safe_failure_without_orphaning_a_temporary_file()
    {
        using var directory = DurableTestDirectory.Create("appsurface-durable-schema-directory-");
        var outputDirectory = Path.Combine(directory.Path, "directory-output");
        Directory.CreateDirectory(outputDirectory);

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(outputDirectory, "SELECT 1;", force: true, CancellationToken.None));

        Assert.Contains("could not be written", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputDirectory));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), static candidate => candidate.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Durable_schema_command_base_requires_non_null_service()
    {
        Assert.Throws<ArgumentNullException>(() => new TestableDurableSchemaOnlineCommand(null!));
    }

    [Fact]
    public async Task Command_execute_async_guards_null_console()
    {
        var service = new FakeDurableSchemaCommandService();
        IConsole nullConsole = null!;

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new DurableSchemaStatusCommand(service).ExecuteAsync(nullConsole));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new DurableSchemaScriptCommand(service).ExecuteAsync(nullConsole));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new DurableSchemaApplyCommand(service).ExecuteAsync(nullConsole));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new DurableSchemaPreflightCommand(service).ExecuteAsync(nullConsole));
    }

    [Fact]
    public async Task Status_renders_problem_and_pending_versions_when_incompatible()
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var environment = new EnvironmentVariableScope("APPSURFACE_DURABLE_CONNECTION", secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.UpgradeRequired, 2, 6, [3, 4, 5, 6]),
        };
        var command = new DurableSchemaStatusCommand(service);
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Contains("Compatibility: UpgradeRequired", output, StringComparison.Ordinal);
        Assert.Contains("Pending: 0003, 0004, 0005, 0006", output, StringComparison.Ordinal);
        Assert.Contains("Problem: The installed schema is older than this runtime requires.", output, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(secretConnection, output);
    }

    [Theory]
    [InlineData("1INVALID_START", true)]
    [InlineData("INVALID-CHAR!", true)]
    [InlineData("_VALID_ENV_VAR", false)]
    public async Task Online_commands_validate_environment_variable_names(string variableName, bool expectError)
    {
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        using var scope = expectError ? null : new EnvironmentVariableScope(variableName, secretConnection);
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(DurableRuntimeSchemaCompatibility.Compatible, 6, 6, []),
        };
        var command = new DurableSchemaStatusCommand(service) { ConnectionEnvironmentVariable = variableName };
        using var console = new FakeInMemoryConsole();

        if (expectError)
        {
            var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
            Assert.Contains("--connection-env must name a non-empty environment variable", error.Message, StringComparison.Ordinal);
        }
        else
        {
            await command.ExecuteAsync(console);
            Assert.Equal(secretConnection, service.ConnectionString);
        }
    }

    [Fact]
    public async Task Online_commands_reject_whitespace_connection_string_values()
    {
        using var scope = new EnvironmentVariableScope("APPSURFACE_DURABLE_BLANK_TEST", "   ");
        var service = new FakeDurableSchemaCommandService();
        var command = new DurableSchemaStatusCommand(service) { ConnectionEnvironmentVariable = "APPSURFACE_DURABLE_BLANK_TEST" };
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
        Assert.Contains("missing or blank", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_online_async_requires_non_null_operation()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await TestableDurableSchemaOnlineCommand.RunAsync<DurableSchemaStatusView>("Host=localhost", CancellationToken.None, null!));
    }

    [Fact]
    public void Durable_schema_status_view_guards_null_and_projects_compatibility()
    {
        Assert.Throws<ArgumentNullException>(() => DurableSchemaStatusView.From(null!));

        var status = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.Missing,
            Guid.NewGuid(),
            activeRuntimeEpoch: null,
            installedVersion: 0,
            requiredVersion: 6,
            minimumReaderVersion: 1,
            maximumReaderVersion: 6,
            minimumWriterVersion: 1,
            maximumWriterVersion: 6,
            appliedVersions: [],
            pendingVersions: [1, 2, 3, 4, 5, 6],
            problem: null);

        var view = DurableSchemaStatusView.From(status);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, view.Compatibility);
        Assert.False(view.IsCompatible);
    }

    [Fact]
    public void Durable_schema_diagnostics_handles_unknown_enum_values()
    {
        const DurableRuntimeSchemaCompatibility unknownCompatibility = (DurableRuntimeSchemaCompatibility)999;
        var cause = DurableSchemaDiagnostics.Cause(unknownCompatibility);
        Assert.Equal("The installed reader/writer compatibility range does not include this runtime.", cause);

        var preflight = DurableSchemaDiagnostics.PreflightFailure(unknownCompatibility);
        Assert.Contains("999", preflight, StringComparison.Ordinal);

        var incompatible = DurableSchemaDiagnostics.SchemaIncompatible(unknownCompatibility);
        Assert.Contains("999", incompatible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durable_schema_command_service_rejects_missing_connection_strings()
    {
        var service = new DurableSchemaCommandService();
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.GetStatusAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.ApplyAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task Durable_schema_command_service_reads_missing_applies_schema_and_reads_compatible_status()
    {
        await using var container = new PostgreSqlBuilder(
                "postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302")
            .WithDatabase("appsurface_durable")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            .Build();
        await container.StartAsync();

        var service = new DurableSchemaCommandService();
        var connectionString = container.GetConnectionString();
        var missing = await service.GetStatusAsync(connectionString, CancellationToken.None);
        var applied = await service.ApplyAsync(connectionString, CancellationToken.None);
        var compatible = await service.GetStatusAsync(connectionString, CancellationToken.None);

        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, missing.Compatibility);
        Assert.Equal([1, 2, 3, 4, 5, 6], applied.AppliedVersions);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, compatible.Compatibility);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Script_output_write_async_validates_path_and_script(string? invalidPath)
    {
        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(invalidPath!, "SELECT 1;", force: false, CancellationToken.None));
        Assert.Contains("--output must name a SQL file", error.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await DurableSchemaScriptOutput.WriteAsync("output.sql", null!, force: false, CancellationToken.None));
    }

    [Fact]
    public async Task Script_output_write_async_handles_invalid_path_exceptions()
    {
        var invalidPath = "invalid\0path.sql";
        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await DurableSchemaScriptOutput.WriteAsync(invalidPath, "SELECT 1;", force: false, CancellationToken.None));
        Assert.Contains("Durable migration script output could not be written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_output_temporary_file_hook_guards_null_callback()
    {
        Assert.Throws<ArgumentNullException>(() => DurableSchemaScriptOutput.UseTemporaryFileWrittenHookForTesting(null!));
    }

    [Fact]
    public void Production_script_generation_is_deterministic_and_contains_the_current_migration_catalog()
    {
        var service = new DurableSchemaCommandService();

        var first = service.GenerateScript(0);
        var second = service.GenerateScript(0);

        Assert.Equal(first, second);
        Assert.Contains("0001", first, StringComparison.Ordinal);
        Assert.Contains("0006", first, StringComparison.Ordinal);
    }

    private sealed class FakeDurableSchemaCommandService : IDurableSchemaCommandService
    {
        internal DurableSchemaStatusView Status { get; set; } =
            new(DurableRuntimeSchemaCompatibility.Missing, 0, 6, [1, 2, 3, 4, 5, 6]);

        internal DurableSchemaApplyView ApplyResult { get; set; } = new(0, 6, [1, 2, 3, 4, 5, 6]);

        internal string Script { get; set; } = string.Empty;

        internal Exception? StatusException { get; set; }

        internal string? ConnectionString { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        internal int FromVersion { get; private set; }

        internal bool OnlineOperationCalled { get; private set; }

        public ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString, CancellationToken cancellationToken)
        {
            ConnectionString = connectionString;
            CancellationToken = cancellationToken;
            OnlineOperationCalled = true;
            return StatusException is null
                ? ValueTask.FromResult(Status)
                : ValueTask.FromException<DurableSchemaStatusView>(StatusException);
        }

        public string GenerateScript(int fromVersion)
        {
            FromVersion = fromVersion;
            return Script;
        }

        public ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString, CancellationToken cancellationToken)
        {
            ConnectionString = connectionString;
            CancellationToken = cancellationToken;
            OnlineOperationCalled = true;
            return ValueTask.FromResult(ApplyResult);
        }
    }

    private sealed class TestableDurableSchemaOnlineCommand(IDurableSchemaCommandService service)
        : DurableSchemaOnlineCommandBase(service)
    {
        public override ValueTask ExecuteAsync(IConsole console) => ValueTask.CompletedTask;

        internal static ValueTask<T> RunAsync<T>(
            string connectionString,
            CancellationToken cancellationToken,
            Func<string, CancellationToken, ValueTask<T>> operation) =>
            RunOnlineAsync(connectionString, cancellationToken, operation);
    }

    private sealed class DurableTestDirectory(string path) : IDisposable
    {
        internal string Path { get; } = path;

        internal static DurableTestDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new DurableTestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
