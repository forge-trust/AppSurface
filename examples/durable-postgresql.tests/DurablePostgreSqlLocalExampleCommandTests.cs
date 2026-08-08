/// <summary>Verifies the local PostgreSQL proof command guards without a database connection.</summary>
[Collection(DurablePostgreSqlLocalExampleCollection.Name)]
public sealed class DurablePostgreSqlLocalExampleCommandTests
{
    [Fact]
    public async Task Commands_ProvideHelpAndRejectUnknownArguments()
    {
        Assert.Equal(0, await DurablePostgreSqlLocalExample.RunAsync(["--help"], CancellationToken.None));
        Assert.Equal(2, await DurablePostgreSqlLocalExample.RunAsync(["not-a-local-proof-command"], CancellationToken.None));
    }

    [Theory]
    [InlineData("schema-bootstrap-dev")]
    [InlineData("verify-local")]
    public async Task ProofCommands_RequireDevelopmentConfirmationBeforeReadingConnectionSettings(string command)
    {
        using var environment = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", null);
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", null);

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync([command], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("schema-bootstrap-dev")]
    [InlineData("verify-local")]
    public async Task ProofCommands_RequireExplicitConfirmationAfterDevelopmentIsSelected(string command)
    {
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", null);

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync([command], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("schema-bootstrap-dev")]
    [InlineData("verify-local")]
    public async Task ProofCommands_RejectInvalidRuntimeEpochBeforeOpeningPostgreSqlConnections(string command)
    {
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var migrationConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_MIGRATION_CONNECTION",
            "Host=localhost;Database=durable_example;Username=appsurface_durable_owner");
        using var dispatcherConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_DISPATCHER_CONNECTION",
            "Host=localhost;Database=durable_example;Username=appsurface_durable_dispatcher");
        using var runtimeConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_RUNTIME_CONNECTION",
            "Host=localhost;Database=durable_example;Username=appsurface_durable_runtime");
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", "not-a-uuid");

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync([command], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BootstrapCommand_MapsCallerCancellationBeforeOpeningPostgreSqlConnections()
    {
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var migrationConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_MIGRATION_CONNECTION",
            "Host=localhost;Database=durable_example;Username=appsurface_durable_owner");
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", Guid.NewGuid().ToString("D"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Theory]
    [InlineData("Host=localhost;Port=not-a-number")]
    [InlineData("Host=durable.example;Database=durable_example;Username=appsurface_durable_owner")]
    public async Task BootstrapCommand_rejects_invalid_or_nonlocal_migration_connections_before_database_work(string connectionString)
    {
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var migrationConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_MIGRATION_CONNECTION",
            connectionString);
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", Guid.NewGuid().ToString("D"));

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BootstrapCommand_requires_the_migration_connection_after_all_local_proof_guards_pass()
    {
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var migrationConnection = new EnvironmentVariableScope("APPSURFACE_DURABLE_MIGRATION_CONNECTION", null);
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", Guid.NewGuid().ToString("D"));

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("APPSURFACE_DURABLE_RUNTIME_CONNECTION", null)]
    [InlineData("APPSURFACE_DURABLE_RUNTIME_CONNECTION", "Host=localhost;Port=not-a-number")]
    [InlineData("APPSURFACE_DURABLE_RUNTIME_CONNECTION", "Host=durable.example;Database=durable_example;Username=appsurface_durable_runtime")]
    [InlineData("APPSURFACE_DURABLE_DISPATCHER_CONNECTION", null)]
    [InlineData("APPSURFACE_DURABLE_DISPATCHER_CONNECTION", "Host=localhost;Port=not-a-number")]
    [InlineData("APPSURFACE_DURABLE_DISPATCHER_CONNECTION", "Host=durable.example;Database=durable_example;Username=appsurface_durable_dispatcher")]
    public async Task VerifyLocalCommand_rejects_missing_invalid_or_nonlocal_runtime_settings_before_opening_connections(
        string settingName,
        string? settingValue)
    {
        const string localRuntimeConnection =
            "Host=localhost;Database=durable_example;Username=appsurface_durable_runtime";
        const string localDispatcherConnection =
            "Host=localhost;Database=durable_example;Username=appsurface_durable_dispatcher";
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var runtimeConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_RUNTIME_CONNECTION",
            settingName == "APPSURFACE_DURABLE_RUNTIME_CONNECTION" ? settingValue : localRuntimeConnection);
        using var dispatcherConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_DISPATCHER_CONNECTION",
            settingName == "APPSURFACE_DURABLE_DISPATCHER_CONNECTION" ? settingValue : localDispatcherConnection);
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", Guid.NewGuid().ToString("D"));

        var exitCode = await DurablePostgreSqlLocalExample.RunAsync(["verify-local"], CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Commands_reject_non_single_argument_shapes_with_usage_exit_codes()
    {
        Assert.Equal(2, await DurablePostgreSqlLocalExample.RunAsync([], CancellationToken.None));
        Assert.Equal(2, await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev", "unexpected"], CancellationToken.None));
        Assert.Equal(2, await DurablePostgreSqlLocalExample.RunAsync(null!, CancellationToken.None));
    }
}
