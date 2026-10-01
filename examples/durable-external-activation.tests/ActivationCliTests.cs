using System.Globalization;
using System.Text.Json;
using ForgeTrust.AppSurface.Examples.DurableExternalActivation;

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

        var invalidAcceptance = await CaptureAsync(["accept-demo-work", "--value"]);
        Assert.Equal(2, invalidAcceptance.ExitCode);
        Assert.Equal("Usage: accept-demo-work --value <value>", invalidAcceptance.StandardError.Trim());

        var invalidInspection = await CaptureAsync(["inspect-demo-work", "--scope", "external-activation-demo"]);
        Assert.Equal(2, invalidInspection.ExitCode);
        Assert.Contains("inspect-demo-work --scope <scope> --work-id <id>", invalidInspection.StandardError);

        var unknown = await CaptureAsync(["not-a-command"]);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Equal("Unknown example command 'not-a-command'. Use --help.", unknown.StandardError.Trim());
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

        foreach (var result in new[] { acceptance, inspection })
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

    private sealed record CliCapture(int ExitCode, string StandardOutput, string StandardError)
    {
        internal string Combined => StandardOutput + StandardError;
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private static readonly string[] Keys =
        [
            "DOTNET_ENVIRONMENT",
            "ASPNETCORE_ENVIRONMENT",
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
