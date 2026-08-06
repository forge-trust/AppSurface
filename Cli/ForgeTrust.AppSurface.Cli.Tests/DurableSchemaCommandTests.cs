using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

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

        internal int FromVersion { get; private set; }

        internal bool OnlineOperationCalled { get; private set; }

        public ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString, CancellationToken cancellationToken)
        {
            ConnectionString = connectionString;
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
            OnlineOperationCalled = true;
            return ValueTask.FromResult(ApplyResult);
        }
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
