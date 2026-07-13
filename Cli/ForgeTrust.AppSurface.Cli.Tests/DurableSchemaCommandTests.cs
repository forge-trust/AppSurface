using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class DurableSchemaCommandTests
{
    [Fact]
    public async Task Status_reads_connection_from_named_environment_variable_without_printing_it()
    {
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView("Compatible", 4, 4, [], null, true),
        };
        var variable = $"APPSURFACE_TEST_{Guid.NewGuid():N}";
        const string secretConnection = "Host=secret.example;Password=do-not-print";
        Environment.SetEnvironmentVariable(variable, secretConnection);
        try
        {
            var command = new DurableSchemaStatusCommand(service)
            {
                ConnectionEnvironmentVariable = variable,
            };
            using var console = new FakeInMemoryConsole();

            await command.ExecuteAsync(console);

            Assert.Equal(secretConnection, service.ConnectionString);
            Assert.Contains("Compatibility: Compatible", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-print", console.ReadOutputString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Preflight_fails_with_problem_cause_fix_and_docs_when_incompatible()
    {
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView(
                "UpgradeRequired",
                2,
                4,
                [3, 4],
                "Pending migrations are required.",
                false),
        };
        var command = new DurableSchemaPreflightCommand(service);
        using var environment = new TemporaryEnvironmentVariable(
            command.ConnectionEnvironmentVariable,
            "Host=localhost;Database=test");
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains("Problem:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cause:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fix:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_requires_explicit_confirmation_then_prints_versions()
    {
        var service = new FakeDurableSchemaCommandService
        {
            ApplyResult = new DurableSchemaApplyView(2, 4, [3, 4]),
        };
        var command = new DurableSchemaApplyCommand(service);
        using var environment = new TemporaryEnvironmentVariable(
            command.ConnectionEnvironmentVariable,
            "Host=localhost;Database=test");
        using var console = new FakeInMemoryConsole();

        await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
        command.Apply = true;
        await command.ExecuteAsync(console);

        Assert.Contains("2 -> 4", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("0003, 0004", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_prints_by_default_and_refuses_existing_file_without_force()
    {
        var service = new FakeDurableSchemaCommandService { Script = "SELECT 1;\n" };
        var command = new DurableSchemaScriptCommand(service) { FromVersion = 2 };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);
        Assert.Equal("SELECT 1;\n", console.ReadOutputString());
        Assert.Equal(2, service.FromVersion);
        Assert.Null(service.ConnectionString);

        var path = Path.Combine(Path.GetTempPath(), $"appsurface-durable-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "existing");
        try
        {
            command.OutputPath = path;
            await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
            command.Force = true;
            await command.ExecuteAsync(console);
            Assert.Equal("SELECT 1;\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("APPSURFACE_MISSING_DURABLE_CONNECTION")]
    public async Task Commands_reject_missing_connection_environment(string variable)
    {
        if (variable.Length > 0)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        var command = new DurableSchemaStatusCommand(new FakeDurableSchemaCommandService())
        {
            ConnectionEnvironmentVariable = variable,
        };
        using var console = new FakeInMemoryConsole();

        await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));
    }

    [Fact]
    public async Task Preflight_prints_compatible_version()
    {
        var service = new FakeDurableSchemaCommandService
        {
            Status = new DurableSchemaStatusView("Compatible", 4, 4, [], null, true),
        };
        var command = new DurableSchemaPreflightCommand(service);
        using var environment = new TemporaryEnvironmentVariable(
            command.ConnectionEnvironmentVariable,
            "Host=localhost;Database=test");
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        Assert.Contains("Compatible: durable schema 4", console.ReadOutputString(), StringComparison.Ordinal);
    }

    private sealed class FakeDurableSchemaCommandService : IDurableSchemaCommandService
    {
        internal DurableSchemaStatusView Status { get; set; } =
            new("Missing", 0, 4, [1, 2, 3, 4], "Schema missing.", false);

        internal DurableSchemaApplyView ApplyResult { get; set; } = new(0, 4, [1, 2, 3, 4]);

        internal string Script { get; set; } = string.Empty;

        internal string? ConnectionString { get; private set; }

        internal int FromVersion { get; private set; }

        public ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString)
        {
            ConnectionString = connectionString;
            return ValueTask.FromResult(Status);
        }

        public string GenerateScript(int fromVersion)
        {
            FromVersion = fromVersion;
            return Script;
        }

        public ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString)
        {
            ConnectionString = connectionString;
            return ValueTask.FromResult(ApplyResult);
        }
    }

    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        internal TemporaryEnvironmentVariable(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
