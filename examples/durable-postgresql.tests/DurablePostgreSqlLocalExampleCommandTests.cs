/// <summary>Verifies the local PostgreSQL proof command guards without a database connection.</summary>
[Collection(DurablePostgreSqlLocalExampleCollection.Name)]
public sealed class DurablePostgreSqlLocalExampleCommandTests
{
    [Fact]
    public async Task RetentionProof_waits_for_one_bounded_batch_and_preserves_protected_rows()
    {
        var reads = 0;

        await DurablePostgreSqlLocalExample.WaitForRetentionProofAsync(
            _ => Task.FromResult(++reads == 1
                ? (Stale: 501L, Recent: 1L, Current: 1L)
                : (Stale: 1L, Recent: 1L, Current: 1L)),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(500, 1, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(501, 0, 1)]
    [InlineData(501, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public async Task RetentionProof_rejects_unexpected_deletion_or_a_missing_protected_row(
        long stale,
        long recent,
        long current)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.WaitForRetentionProofAsync(
                _ => Task.FromResult((stale, recent, current)),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));

        Assert.Contains("protected row or deleted outside one 500-row batch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionProof_reports_a_deadline_when_cleanup_never_completes()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            DurablePostgreSqlLocalExample.WaitForRetentionProofAsync(
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return (Stale: 501L, Recent: 1L, Current: 1L);
                },
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task RetentionProof_propagates_caller_cancellation_without_misreporting_a_deadline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DurablePostgreSqlLocalExample.WaitForRetentionProofAsync(
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return (Stale: 501L, Recent: 1L, Current: 1L);
                },
                TimeSpan.FromSeconds(2),
                cancellation.Token));
    }

    [Fact]
    public async Task RetentionProof_rejects_a_missing_count_reader()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DurablePostgreSqlLocalExample.WaitForRetentionProofAsync(
                null!,
                TimeSpan.FromSeconds(2),
                CancellationToken.None));
    }

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
