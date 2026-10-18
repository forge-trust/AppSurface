using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.PostgreSql.Tests;
using ForgeTrust.AppSurface.Examples.DurableExternalActivation;
using Npgsql;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

public sealed class ActivationCliTests
{
    [Fact]
    public async Task Help_and_invalid_command_shapes_return_bounded_usage()
    {
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", "Development");

        var empty = await CaptureAsync([]);
        Assert.Equal(0, empty.ExitCode);
        Assert.Contains("Durable external-activation example commands:", empty.StandardOutput);
        Assert.Contains("schema-apply-dev", empty.StandardOutput);
        Assert.Empty(empty.StandardError);

        var help = await CaptureAsync(["--help"]);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("accept-demo-work --value <value>", help.StandardOutput);
        Assert.Empty(help.StandardError);

        foreach (var helpCommand in new[] { "help", "-h" })
        {
            var synonym = await CaptureAsync([helpCommand]);
            Assert.Equal(0, synonym.ExitCode);
            Assert.Contains("Durable external-activation example commands:", synonym.StandardOutput);
            Assert.Empty(synonym.StandardError);
        }

        var invalidAcceptance = await CaptureAsync(["accept-demo-work", "--value"]);
        Assert.Equal(2, invalidAcceptance.ExitCode);
        Assert.Equal("Usage: accept-demo-work --value <value>", invalidAcceptance.StandardError.Trim());

        var invalidInspection = await CaptureAsync(["inspect-demo-work", "--scope", "external-activation-demo"]);
        Assert.Equal(2, invalidInspection.ExitCode);
        Assert.Contains("inspect-demo-work --scope <scope> --work-id <id>", invalidInspection.StandardError);

        foreach (var args in new[]
        {
            new[] { "accept-demo-work" },
            new[] { "accept-demo-work", "--other", "value" },
            new[] { "accept-demo-work", "--value", " " },
            new[] { "accept-demo-work", "--value", new string('x', 201) },
            new[] { "accept-demo-work", "--value", "value", "extra" },
        })
        {
            var invalid = await CaptureAsync(args);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Empty(invalid.StandardOutput);
            Assert.Equal("Usage: accept-demo-work --value <value>", invalid.StandardError.Trim());
        }

        foreach (var args in new[]
        {
            new[] { "inspect-demo-work" },
            new[] { "inspect-demo-work", "--other", "scope", "--work-id", "id" },
            new[] { "inspect-demo-work", "--scope", "scope", "--other", "id" },
        })
        {
            var invalid = await CaptureAsync(args);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Empty(invalid.StandardOutput);
            Assert.Contains("inspect-demo-work --scope <scope> --work-id <id>", invalid.StandardError);
        }

        foreach (var command in new[] { "schema-apply-dev", "epoch-bootstrap-dev", "serve" })
        {
            var extraArgument = await CaptureAsync([command, "unexpected"]);
            Assert.Equal(2, extraArgument.ExitCode);
            Assert.Empty(extraArgument.StandardOutput);
            Assert.Equal(
                $"Unknown example command '{command}'. Use --help.",
                extraArgument.StandardError.Trim());
        }

        var unknown = await CaptureAsync(["not-a-command"]);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Equal("Unknown example command 'not-a-command'. Use --help.", unknown.StandardError.Trim());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Schema_apply_is_idempotent_and_epoch_bootstrap_guards_then_initializes_an_empty_epoch()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", null);
        environment.Set("ASPNETCORE_ENVIRONMENT", "Development");
        environment.Set(DurableExternalActivationProgram.MigrationConnectionKey, database.ConnectionString);
        var epoch = Guid.NewGuid();
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, epoch.ToString("D", CultureInfo.InvariantCulture));
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);

        environment.Set("DOTNET_ENVIRONMENT", "Production");
        environment.Set("ASPNETCORE_ENVIRONMENT", "Development");
        foreach (var command in new[] { "schema-apply-dev", "epoch-bootstrap-dev" })
        {
            AssertSafeCommandFailure(await CaptureAsync([command]), command);
        }
        Assert.False((await manager.GetStatusAsync()).IsCompatible);

        environment.Set("DOTNET_ENVIRONMENT", null);
        environment.Set("ASPNETCORE_ENVIRONMENT", null);
        foreach (var command in new[] { "schema-apply-dev", "epoch-bootstrap-dev" })
        {
            AssertSafeCommandFailure(await CaptureAsync([command]), command);
        }
        Assert.False((await manager.GetStatusAsync()).IsCompatible);

        environment.Set("ASPNETCORE_ENVIRONMENT", "Development");
        var incompatible = await CaptureAsync(["epoch-bootstrap-dev"]);
        AssertSafeCommandFailure(incompatible, "epoch-bootstrap-dev");
        Assert.False((await manager.GetStatusAsync()).IsCompatible);

        var firstApply = await CaptureAsync(["schema-apply-dev"]);
        var secondApply = await CaptureAsync(["schema-apply-dev"]);
        Assert.Equal(0, firstApply.ExitCode);
        Assert.Equal(0, secondApply.ExitCode);
        Assert.StartsWith("[schema-apply-dev] schema compatible at version ", firstApply.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(firstApply.StandardOutput, secondApply.StandardOutput);
        Assert.Empty(firstApply.StandardError);
        Assert.Empty(secondApply.StandardError);

        var emptyStatus = await manager.GetStatusAsync();
        Assert.True(emptyStatus.IsCompatible);
        Assert.Null(emptyStatus.ActiveRuntimeEpoch);

        foreach (var invalidEpoch in new string?[]
        {
            null,
            string.Empty,
            "not-a-guid",
            Guid.Empty.ToString("D", CultureInfo.InvariantCulture),
        })
        {
            environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, invalidEpoch);
            AssertSafeCommandFailure(await CaptureAsync(["epoch-bootstrap-dev"]), "epoch-bootstrap-dev");
            Assert.Null((await manager.GetStatusAsync()).ActiveRuntimeEpoch);
        }

        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, epoch.ToString("D", CultureInfo.InvariantCulture));
        var bootstrap = await CaptureAsync(["epoch-bootstrap-dev"]);
        Assert.Equal(0, bootstrap.ExitCode);
        Assert.Equal("[epoch-bootstrap-dev] active epoch initialized", bootstrap.StandardOutput.Trim());
        Assert.Empty(bootstrap.StandardError);
        Assert.Equal(epoch, (await manager.GetStatusAsync()).ActiveRuntimeEpoch);

        var replacementEpoch = Guid.NewGuid();
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, replacementEpoch.ToString("D", CultureInfo.InvariantCulture));
        var alreadyActive = await CaptureAsync(["epoch-bootstrap-dev"]);
        AssertSafeCommandFailure(alreadyActive, "epoch-bootstrap-dev");
        Assert.Equal(epoch, (await manager.GetStatusAsync()).ActiveRuntimeEpoch);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Acceptance_and_inspection_reject_mismatched_epochs_and_missing_work_returns_only_a_bounded_code()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", "Development");
        environment.Set("ASPNETCORE_ENVIRONMENT", null);
        environment.Set(DurableExternalActivationProgram.MigrationConnectionKey, database.ConnectionString);
        environment.Set(DurableExternalActivationProgram.DispatcherConnectionKey, database.ConnectionString);
        environment.Set(DurableExternalActivationProgram.RuntimeConnectionKey, database.ConnectionString);

        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var activeEpoch = Guid.NewGuid();
        await manager.InitializeRuntimeEpochAsync(activeEpoch, "cli-coverage", "initial-development");
        var mismatchedEpoch = Guid.NewGuid();
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, mismatchedEpoch.ToString("D", CultureInfo.InvariantCulture));

        var acceptance = await CaptureAsync(["accept-demo-work", "--value", "epoch-mismatch"]);
        AssertSafeCommandFailure(acceptance, "accept-demo-work");
        var inspection = await CaptureAsync([
            "inspect-demo-work",
            "--scope",
            "external-activation-demo",
            "--work-id",
            "missing-cli-work",
        ]);
        AssertSafeCommandFailure(inspection, "inspect-demo-work");
        Assert.DoesNotContain(mismatchedEpoch.ToString("D", CultureInfo.InvariantCulture), acceptance.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain(mismatchedEpoch.ToString("D", CultureInfo.InvariantCulture), inspection.Combined, StringComparison.Ordinal);

        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, activeEpoch.ToString("D", CultureInfo.InvariantCulture));
        var missingWork = await CaptureAsync([
            "inspect-demo-work",
            "--scope",
            "external-activation-demo",
            "--work-id",
            "missing-cli-work",
        ]);

        Assert.Equal(3, missingWork.ExitCode);
        Assert.Empty(missingWork.StandardError);
        using var failureDocument = JsonDocument.Parse(missingWork.StandardOutput);
        var failure = failureDocument.RootElement;
        Assert.Single(failure.EnumerateObject());
        Assert.Equal(DurableProblemCodes.WorkNotFound, failure.GetProperty("ProblemCode").GetString());
        Assert.DoesNotContain(database.ConnectionString, missingWork.Combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_validates_startup_guards_serves_probes_and_releases_its_data_sources_on_shutdown()
    {
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", "Development");
        environment.Set("ASPNETCORE_ENVIRONMENT", null);
        environment.Set("ASPNETCORE_URLS", "http://127.0.0.1:0");
        environment.Set(DurableExternalActivationProgram.MigrationConnectionKey, null);
        environment.Set("DurableActivation__DevelopmentBearerToken", null);

        environment.Set("DOTNET_ENVIRONMENT", "Production");
        environment.Set("ASPNETCORE_ENVIRONMENT", "Development");
        AssertSafeCommandFailure(await CaptureAsync(["serve"]), "serve");
        environment.Set("DOTNET_ENVIRONMENT", null);
        environment.Set("ASPNETCORE_ENVIRONMENT", null);
        AssertSafeCommandFailure(await CaptureAsync(["serve"]), "serve");
        environment.Set("DOTNET_ENVIRONMENT", "Development");

        await using (var guardDatabase = await PostgreSqlIntegrationTestDatabase.TryCreateAsync())
        {
            environment.Set(DurableExternalActivationProgram.DispatcherConnectionKey, guardDatabase.ConnectionString);
            environment.Set(DurableExternalActivationProgram.RuntimeConnectionKey, guardDatabase.ConnectionString);
            environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

            var missingToken = await CaptureAsync(["serve"]);
            AssertSafeCommandFailure(missingToken, "serve");

            environment.Set("DurableActivation__DevelopmentBearerToken", "serve-cli-test-token");
            var guardSchema = new PostgreSqlDurableRuntimeSchemaManager(guardDatabase.DataSource);
            var incompatible = await CaptureAsync(["serve"]);
            AssertSafeCommandFailure(incompatible, "serve");
            Assert.False((await guardSchema.GetStatusAsync()).IsCompatible);

            await guardSchema.ApplyAsync();
            var guardEpoch = Guid.NewGuid();
            await guardSchema.InitializeRuntimeEpochAsync(guardEpoch, "cli-coverage", "initial-development");
            environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
            var mismatchedEpoch = await CaptureAsync(["serve"]);
            AssertSafeCommandFailure(mismatchedEpoch, "serve");
            Assert.Equal(guardEpoch, (await guardSchema.GetStatusAsync()).ActiveRuntimeEpoch);
        }

        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        var applicationName = $"activation-cli-serve-{Guid.NewGuid():N}";
        var dispatcherApplicationName = $"{applicationName}-dispatcher";
        var runtimeApplicationName = $"{applicationName}-runtime";
        var dispatcherConnection = new NpgsqlConnectionStringBuilder(fixture.DispatcherConnection)
        {
            ApplicationName = dispatcherApplicationName,
            Pooling = false,
        }.ConnectionString;
        var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnection)
        {
            ApplicationName = runtimeApplicationName,
            Pooling = false,
        }.ConnectionString;
        environment.Set(DurableExternalActivationProgram.DispatcherConnectionKey, dispatcherConnection);
        environment.Set(DurableExternalActivationProgram.RuntimeConnectionKey, runtimeConnection);
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, fixture.Epoch.ToString("D", CultureInfo.InvariantCulture));

        var schema = new PostgreSqlDurableRuntimeSchemaManager(fixture.Administrator);
        var statusBefore = await schema.GetStatusAsync();
        var catalogBefore = await fixture.ReadCatalogAsync();
        using var shutdown = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = new ServeOutputWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Task<int>? serveTask = null;
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            serveTask = Task.Run(() => DurableExternalActivationProgram.RunAsync(["serve"], shutdown.Token));
            var readyLine = await output.Ready.WaitAsync(watchdog.Token);
            Assert.StartsWith("[serve] host ready at http://127.0.0.1:", readyLine, StringComparison.Ordinal);
            var address = readyLine["[serve] host ready at ".Length..].Split(';', 2)[0];
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };

            using var live = await client.GetAsync("/live", watchdog.Token);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var liveBody = JsonDocument.Parse(await live.Content.ReadAsStringAsync(watchdog.Token));
            Assert.Equal("Live", liveBody.RootElement.GetProperty("status").GetString());

            using var compatibility = await client.GetAsync("/compatibility", watchdog.Token);
            Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
            using var compatibilityBody = JsonDocument.Parse(await compatibility.Content.ReadAsStringAsync(watchdog.Token));
            Assert.Equal("Assessment", compatibilityBody.RootElement.GetProperty("outcome").GetString());

            using var readiness = await client.GetAsync("/ready", watchdog.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            using var readinessBody = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync(watchdog.Token));
            Assert.Equal("Assessment", readinessBody.RootElement.GetProperty("outcome").GetString());

            shutdown.Cancel();
            Assert.Equal(0, await serveTask.WaitAsync(watchdog.Token));
            Assert.Contains("[serve] host ready at ", output.Content, StringComparison.Ordinal);
            var statusAfter = await schema.GetStatusAsync(watchdog.Token);
            Assert.Equal(statusBefore.Compatibility, statusAfter.Compatibility);
            Assert.Equal(statusBefore.StoreId, statusAfter.StoreId);
            Assert.Equal(statusBefore.ActiveRuntimeEpoch, statusAfter.ActiveRuntimeEpoch);
            Assert.Equal(statusBefore.AppliedVersions, statusAfter.AppliedVersions);
            Assert.Equal(catalogBefore, await fixture.ReadCatalogAsync());
            Assert.Equal(0, await CountConnectionsAsync(fixture.Administrator, dispatcherApplicationName, runtimeApplicationName));
        }
        finally
        {
            shutdown.Cancel();
            if (serveTask is not null)
            {
                using var cleanupWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await serveTask.WaitAsync(cleanupWait.Token);
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            }
            else
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public async Task Blank_required_connection_is_rejected_without_printing_environment_values()
    {
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", "Development");
        environment.Set(DurableExternalActivationProgram.MigrationConnectionKey, "  ");
        environment.Set(DurableExternalActivationProgram.DispatcherConnectionKey, "Host=do-not-print;Password=connection-secret");
        environment.Set(DurableExternalActivationProgram.RuntimeConnectionKey, "Host=do-not-print;Password=runtime-secret");
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, "epoch-secret");
        environment.Set("DurableActivation__DevelopmentBearerToken", "token-secret");

        var result = await CaptureAsync(["schema-apply-dev"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[schema-apply-dev] failed; verify the configured PostgreSQL roles, schema, epoch, and local bearer-token settings.", result.StandardError);
        Assert.DoesNotContain("connection-secret", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("epoch-secret", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", result.Combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_cli_acceptance_receipt_and_inspection_round_trip_through_restricted_postgresql_roles()
    {
        await using var fixture = await PostgreSqlActivationFixture.StartAsync();
        using var environment = new ProcessEnvironmentScope();
        environment.Set("DOTNET_ENVIRONMENT", "Development");
        environment.Set(DurableExternalActivationProgram.MigrationConnectionKey, "");
        environment.Set(DurableExternalActivationProgram.DispatcherConnectionKey, fixture.DispatcherConnection);
        environment.Set(DurableExternalActivationProgram.RuntimeConnectionKey, fixture.RuntimeConnection);
        environment.Set(DurableExternalActivationProgram.RuntimeEpochKey, fixture.Epoch.ToString("D", CultureInfo.InvariantCulture));
        environment.Set("DurableActivation__DevelopmentBearerToken", "cli-test-token");

        var acceptance = await CaptureAsync(["accept-demo-work", "--value", "accepted-through-cli"]);

        Assert.Equal(0, acceptance.ExitCode);
        Assert.Empty(acceptance.StandardError);
        using var receiptDocument = JsonDocument.Parse(acceptance.StandardOutput);
        var receipt = receiptDocument.RootElement;
        Assert.Equal("external-activation-demo", receipt.GetProperty("Scope").GetString());
        var workId = receipt.GetProperty("WorkId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workId));
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("CommandId").GetString()));
        Assert.Equal("Accepted", receipt.GetProperty("Kind").GetString());
        Assert.True(receipt.GetProperty("Revision").GetInt64() > 0);
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("AcceptedAtUtc").GetString()));

        var inspection = await CaptureAsync([
            "inspect-demo-work",
            "--scope",
            "external-activation-demo",
            "--work-id",
            workId!,
        ]);

        Assert.Equal(0, inspection.ExitCode);
        Assert.Empty(inspection.StandardError);
        using var inspectionDocument = JsonDocument.Parse(inspection.StandardOutput);
        var inspected = inspectionDocument.RootElement;
        Assert.Equal("external-activation-demo", inspected.GetProperty("Scope").GetString());
        Assert.Equal(workId, inspected.GetProperty("WorkId").GetString());
        Assert.Equal("Ready", inspected.GetProperty("State").GetString());
        Assert.Equal(0, inspected.GetProperty("AttemptNumber").GetInt32());
        Assert.True(inspected.GetProperty("Revision").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, inspected.GetProperty("TerminalAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, inspected.GetProperty("TerminalCode").ValueKind);

        var persisted = await fixture.InspectAsync(new ForgeTrust.AppSurface.Durable.DurableWorkAcceptance(
            new ForgeTrust.AppSurface.Durable.DurableWorkId(workId!),
            new ForgeTrust.AppSurface.Durable.DurableCommandId(receipt.GetProperty("CommandId").GetString()!),
            ForgeTrust.AppSurface.Durable.DurableWorkAcceptanceKind.Accepted,
            receipt.GetProperty("Revision").GetInt64(),
            DateTimeOffset.Parse(receipt.GetProperty("AcceptedAtUtc").GetString()!, CultureInfo.InvariantCulture)));
        Assert.Equal(ForgeTrust.AppSurface.Durable.DurableWorkState.Ready, persisted.State);
        Assert.Equal(workId, persisted.WorkId.Value);

        using var activationContent = new ByteArrayContent([]);
        using var activation = await fixture.Client.PostAsync("/private/durable/activate", activationContent);
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using var activationDocument = JsonDocument.Parse(await activation.Content.ReadAsStringAsync());
        Assert.Equal("Completed", activationDocument.RootElement.GetProperty("outcome").GetString());

        var terminalInspection = await CaptureAsync([
            "inspect-demo-work",
            "--scope",
            "external-activation-demo",
            "--work-id",
            workId!,
        ]);
        Assert.Equal(0, terminalInspection.ExitCode);
        Assert.Empty(terminalInspection.StandardError);
        using var terminalDocument = JsonDocument.Parse(terminalInspection.StandardOutput);
        var terminal = terminalDocument.RootElement;
        Assert.Equal("Succeeded", terminal.GetProperty("State").GetString());
        Assert.Equal(1, terminal.GetProperty("AttemptNumber").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(terminal.GetProperty("TerminalAtUtc").GetString()));
        Assert.Equal("completed", terminal.GetProperty("TerminalCode").GetString());

        foreach (var result in new[] { acceptance, inspection, terminalInspection })
        {
            Assert.DoesNotContain(fixture.DispatcherConnection, result.Combined, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.RuntimeConnection, result.Combined, StringComparison.Ordinal);
            Assert.DoesNotContain("cli-test-token", result.Combined, StringComparison.Ordinal);
        }
    }

    private static async Task<CliCapture> CaptureAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            exitCode = await DurableExternalActivationProgram.RunAsync(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return new CliCapture(exitCode, output.ToString(), error.ToString());
    }

    private static void AssertSafeCommandFailure(CliCapture result, string command)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"[{command}] failed; verify the configured PostgreSQL roles, schema, epoch, and local bearer-token settings.",
            result.StandardError.Trim());
    }

    private static async Task<long> CountConnectionsAsync(
        NpgsqlDataSource dataSource,
        string dispatcherApplicationName,
        string runtimeApplicationName)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_stat_activity
            WHERE application_name = @dispatcher OR application_name = @runtime;
            """);
        command.Parameters.AddWithValue("dispatcher", dispatcherApplicationName);
        command.Parameters.AddWithValue("runtime", runtimeApplicationName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed record CliCapture(int ExitCode, string StandardOutput, string StandardError)
    {
        internal string Combined => StandardOutput + StandardError;
    }

    private sealed class ServeOutputWriter : StringWriter
    {
        private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private readonly StringBuilder content = new();

        internal Task<string> Ready => ready.Task;

        internal string Content
        {
            get
            {
                lock (gate)
                {
                    return content.ToString();
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (gate)
            {
                content.AppendLine(value);
            }

            if (value?.StartsWith("[serve] host ready at ", StringComparison.Ordinal) == true)
            {
                ready.TrySetResult(value);
            }
        }
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private static readonly string[] Keys =
        [
            "DOTNET_ENVIRONMENT",
            "ASPNETCORE_ENVIRONMENT",
            "ASPNETCORE_URLS",
            DurableExternalActivationProgram.MigrationConnectionKey,
            DurableExternalActivationProgram.DispatcherConnectionKey,
            DurableExternalActivationProgram.RuntimeConnectionKey,
            DurableExternalActivationProgram.RuntimeEpochKey,
            "DurableActivation__DevelopmentBearerToken",
        ];

        private readonly Dictionary<string, string?> originalValues = Keys.ToDictionary(
            static key => key,
            static key => Environment.GetEnvironmentVariable(key),
            StringComparer.Ordinal);

        internal void Set(string key, string? value) => Environment.SetEnvironmentVariable(key, value);

        public void Dispose()
        {
            foreach (var (key, value) in originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
