using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;

using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class ProgramEntryPointTests
{
    private static readonly TimeSpan DefaultHealthVerifyStartupTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EntryPoint_Should_Print_Root_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{AppSurfaceCliApp.ToolCommandName} [command]", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ForgeTrust.AppSurface.Cli.dll", result.AllText, StringComparison.Ordinal);
        Assert.Contains("docs", result.AllText, StringComparison.Ordinal);
        Assert.Contains("docs export", result.AllText, StringComparison.Ordinal);
        Assert.Contains("coverage", result.AllText, StringComparison.Ordinal);
        Assert.Contains("secrets", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Secrets_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["secrets", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Manage AppSurface local development secrets and explicit remote transfers.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("set", result.AllText, StringComparison.Ordinal);
        Assert.Contains("doctor", result.AllText, StringComparison.Ordinal);
        Assert.Contains("appsurface secrets [command] --help", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plan", "--out")]
    [InlineData("apply", "--plan")]
    public async Task SecretsTransferCommands_Should_DescribeRequiredArtifacts(string command, string requiredOption)
    {
        var result = await InvokeProgramEntryPointAsync(["secrets", "transfer", command, "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(requiredOption, result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plan", "--config is required.")]
    [InlineData("apply", "--config is required.")]
    public async Task SecretsTransferCommands_Should_RequireConfigurationBeforeAnyProviderWork(string command, string expectedDiagnostic)
    {
        var result = await InvokeProgramEntryPointAsync(["secrets", "transfer", command]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(expectedDiagnostic, result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsTransferCommands_Should_Plan_DryRun_AndApply_WithValueSafeJsonOutput()
    {
        using var temp = TempDirectory.Create("appsurface-transfer-");
        var storePath = Path.Join(temp.Path, "secrets.json");
        var configPath = Path.Join(temp.Path, "promotion.json");
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        await File.WriteAllTextAsync(configPath, """
            {"version":1,"endpoints":[{"name":"staging","provider":"google","environment":"staging","credential":{"mode":"applicationDefault"}}],"jobs":[{"name":"local-to-staging","source":"local","destination":"staging","rows":[{"key":"Stripe:ApiKey","destination":"projects/staging/secrets/stripe-api-key"}]}]}
            """);
        var client = new FakeGoogleSecretTransferClient();
        client.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var shared = new[] { "--app", "MyApp", "--environment", "Development", "--store-file", storePath };

        var set = await InvokeProgramEntryPointAsync(["secrets", "set", "Stripe:ApiKey", "--stdin", .. shared], standardInput: "sentinel-local-secret\n");
        var plan = await InvokeProgramEntryPointAsync(["secrets", "transfer", "plan", "--config", configPath, "--job", "local-to-staging", "--out", planPath, "--json", .. shared], options => RegisterGoogleTransferClient(options, client));
        var dryRun = await InvokeProgramEntryPointAsync(["secrets", "transfer", "apply", "--config", configPath, "--plan", planPath, "--json", .. shared], options => RegisterGoogleTransferClient(options, client));
        var apply = await InvokeProgramEntryPointAsync(["secrets", "transfer", "apply", "--config", configPath, "--plan", planPath, "--apply", "--json", .. shared], options => RegisterGoogleTransferClient(options, client));

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(0, plan.ExitCode);
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(0, apply.ExitCode);
        Assert.Single(client.Writes);
        Assert.DoesNotContain("sentinel-local-secret", plan.AllText + dryRun.AllText + apply.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsTransferPlan_Should_RenderValueSafeTextDiagnostics_WhenSourceIsMissing()
    {
        using var temp = TempDirectory.Create("appsurface-transfer-");
        var storePath = Path.Join(temp.Path, "secrets.json");
        var configPath = Path.Join(temp.Path, "promotion.json");
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        await File.WriteAllTextAsync(configPath, """
            {"version":1,"endpoints":[{"name":"staging","provider":"google","environment":"staging","credential":{"mode":"applicationDefault"}}],"jobs":[{"name":"local-to-staging","source":"local","destination":"staging","rows":[{"key":"Missing:ApiKey","destination":"projects/staging/secrets/missing-api-key"}]}]}
            """);
        var client = new FakeGoogleSecretTransferClient();
        client.Secrets["projects/staging/secrets/missing-api-key"] = false;

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "transfer", "plan", "--config", configPath, "--job", "local-to-staging", "--out", planPath, "--app", "MyApp", "--environment", "Development", "--store-file", storePath],
            options => RegisterGoogleTransferClient(options, client));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Operation: plan; Job: local-to-staging; Mode: dry-run", result.AllText, StringComparison.Ordinal);
        Assert.Contains("SourceMissing: Missing:ApiKey", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: local-secret-promotion-source-missing", result.AllText, StringComparison.Ordinal);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task SecretsCommands_Should_Set_List_Get_And_Delete_Without_PrintingSecretValue()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");
        var shared = new[] { "--app", "MyApp", "--environment", "Development", "--store-file", storePath };

        var init = await InvokeProgramEntryPointAsync(["secrets", "init", .. shared]);
        var set = await InvokeProgramEntryPointAsync(["secrets", "set", "Stripe:ApiKey", "--stdin", .. shared], standardInput: "sk_test_secret\n");
        var list = await InvokeProgramEntryPointAsync(["secrets", "list", .. shared]);
        var get = await InvokeProgramEntryPointAsync(["secrets", "get", "Stripe:ApiKey", .. shared]);
        var delete = await InvokeProgramEntryPointAsync(["secrets", "delete", "Stripe:ApiKey", .. shared]);

        Assert.Equal(0, init.ExitCode);
        Assert.Equal(0, set.ExitCode);
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(0, get.ExitCode);
        Assert.Equal(0, delete.ExitCode);
        Assert.Contains("Stripe:ApiKey", list.AllText, StringComparison.Ordinal);
        Assert.Contains("Found: local secret namespace", get.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", init.AllText + set.AllText + list.AllText + get.AllText + delete.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsListCommand_Should_PrintOnlyNames_WhenNamesOnlyIsSet()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");
        var shared = new[] { "--app", "MyApp", "--environment", "Development", "--store-file", storePath };

        var set = await InvokeProgramEntryPointAsync(["secrets", "set", "Stripe:ApiKey", "--stdin", .. shared], standardInput: "sk_test_secret\n");
        var list = await InvokeProgramEntryPointAsync(["secrets", "list", "--names-only", .. shared]);

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("Stripe:ApiKey", list.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Source:", list.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", list.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsListCommand_Should_RenderDiagnostic_WhenStoreFileIsInvalidJson()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");
        await File.WriteAllTextAsync(storePath, "{");
        if (!OperatingSystem.IsWindows())
        {
            new FileInfo(storePath).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var shared = new[] { "--app", "MyApp", "--environment", "Development", "--store-file", storePath };

        var list = await InvokeProgramEntryPointAsync(["secrets", "list", .. shared]);

        Assert.NotEqual(0, list.ExitCode);
        Assert.Contains("local-secret-store-invalid", list.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", list.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsDoctorCommand_Should_RenderReadinessDiagnosticWithoutPrintingSecretValue()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "doctor", "--app", "MyApp", "--environment", "Development", "--store-file", storePath]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Ready: local secret namespace", result.AllText, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "local-secret-file-posture-degraded" : "local-secret-store-ready", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsDoctorCommand_Should_TreatRepairedPostureAsSuccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");
        File.WriteAllText(storePath, "{}");
        new FileInfo(storePath).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "doctor", "--app", "MyApp", "--environment", "Development", "--store-file", storePath]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Ready: local secret namespace", result.AllText, StringComparison.Ordinal);
        Assert.Contains("local-secret-file-posture-repaired", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsDoctorCommand_Should_RejectSecretToolPathWithStoreFile()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "doctor", "--store-file", storePath, "--secret-tool-path", "/usr/local/bin/secret-tool"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Use either --store-file or --secret-tool-path", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsSetCommand_Should_RejectSecretToolPathWithStoreFile()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "set", "Stripe:ApiKey", "--stdin", "--store-file", storePath, "--secret-tool-path", "/usr/local/bin/secret-tool"],
            standardInput: "sk_test_secret");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Use either --store-file or --secret-tool-path", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsCommandBase_Should_PassSecretToolPathToPlatformStoreFactory()
    {
        var command = new CapturingSecretsCommand
        {
            SecretToolPath = "/nix/store/appsurface-secret-tool/bin/secret-tool"
        };

        var context = command.BuildContextForTests();

        Assert.Equal(command.SecretToolPath, command.PlatformOptions?.LinuxSecretToolPath);
        Assert.Same(command.Store, context.Store);
    }

    [Fact]
    public void SecretsCommandBase_Should_CreateDefaultPlatformStore_WhenStoreFileIsNotConfigured()
    {
        var command = new DefaultPlatformSecretsCommand();

        var context = command.BuildContextForTests();

        Assert.IsType<PlatformAppSurfaceLocalSecretStore>(context.Store);
    }

    [Theory]
    [InlineData("local-secret-store-ready")]
    [InlineData("local-secret-file-posture-repaired")]
    [InlineData("local-secret-file-posture-degraded")]
    public async Task SecretsCommandBase_Should_TreatDoctorReadinessDiagnosticsAsSuccess(string diagnosticCode)
    {
        using var console = new FakeInMemoryConsole();
        var result = AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.Missing,
            CreateLocalSecretDiagnostic(diagnosticCode),
            "test-store");

        await CapturingSecretsCommand.WriteResultForTestsAsync(console, result, "Ready");

        var output = console.ReadOutputString();
        Assert.Contains("Ready: local secret namespace", output, StringComparison.Ordinal);
        Assert.Contains("Source: test-store", output, StringComparison.Ordinal);
        Assert.Contains(diagnosticCode, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsCommandBase_Should_RejectOrdinaryMissingDiagnostic()
    {
        using var console = new FakeInMemoryConsole();
        var result = AppSurfaceLocalSecretResult.Missing("test-store");

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await CapturingSecretsCommand.WriteResultForTestsAsync(console, result, "Found"));

        Assert.Contains("local-secret-missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsCommandBase_Should_RejectProviderFailureWithoutDiagnostic()
    {
        using var console = new FakeInMemoryConsole();
        var result = new AppSurfaceLocalSecretResult(
            LocalSecretResultStatus.ProviderFailed,
            null,
            null,
            "test-store");

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await CapturingSecretsCommand.WriteResultForTestsAsync(console, result, "Found"));

        Assert.Equal("Local secret command failed.", exception.Message);
    }

    [Fact]
    public async Task SecretsSetCommand_Should_RejectConflictingValueSources()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "set", "Stripe:ApiKey", "--value", "sk_test_secret", "--stdin", "--store-file", storePath],
            standardInput: "sk_other_secret");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Use either --value or --stdin", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_other_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsSetCommand_Should_SetValueOptionWithoutPrintingSecretValue()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var set = await InvokeProgramEntryPointAsync(
            ["secrets", "set", "Stripe:ApiKey", "--value", "sk_test_secret", "--store-file", storePath]);
        var get = await InvokeProgramEntryPointAsync(
            ["secrets", "get", "Stripe:ApiKey", "--store-file", storePath]);

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(0, get.ExitCode);
        Assert.Contains("Set: local secret namespace", set.AllText, StringComparison.Ordinal);
        Assert.Contains("Found: local secret namespace", get.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", set.AllText + get.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsSetCommand_Should_RejectMissingValue()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "set", "Stripe:ApiKey", "--store-file", storePath]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing secret value", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsGetCommand_Should_ReturnPasteSafeFailure_WhenSecretIsMissing()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "get", "Stripe:ApiKey", "--store-file", storePath]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Local secret was not found", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsCommand_Should_ReturnIdentityDiagnostic_WhenNamespaceIsInvalid()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "init", "--app", "Bad/App", "--store-file", storePath]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local-secret-applicationName-invalid-character", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsCommand_Should_ReturnIdentityDiagnostic_WhenKeyIsInvalid()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");
        var storePath = Path.Join(temp.Path, "local-secrets.json");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "get", "Stripe\nApiKey", "--store-file", storePath]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local-secret-key-invalid-character", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsDoctorCommand_Should_ReturnStoreFailure_WhenStoreFileIsDirectory()
    {
        using var temp = TempDirectory.Create("appsurface-secrets-");

        var result = await InvokeProgramEntryPointAsync(
            ["secrets", "doctor", "--store-file", temp.Path]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local-secret-file-posture-unsupported", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Coverage_Merge_Help_With_Required_Source()
    {
        var result = await InvokeEntryPointAsync(["coverage", "merge", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Merge existing Cobertura shards into local AppSurface coverage artifacts.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--source", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Required directory containing coverage.cobertura.xml shard files to merge.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--output", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.1")]
    [InlineData("0.1.0-rc-1", "0.1.0-rc-1")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("V0.1.0-rc.1", "0.1.0-rc.1")]
    [InlineData("0.1.0+abc123", "0.1.0")]
    [InlineData("v0.1.0-rc.1+abc123", "0.1.0-rc.1")]
    [InlineData(" 0.1.0-rc.1 ", "0.1.0-rc.1")]
    public void AppSurfaceCliVersion_NormalizesPackageDisplayVersion(string rawVersion, string expectedVersion)
    {
        var version = AppSurfaceCliVersion.NormalizeDisplayVersion(rawVersion);

        Assert.Equal(expectedVersion, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.1.0\nrc.1")]
    [InlineData("0.1.0\rrc.1")]
    [InlineData("v")]
    [InlineData("v+sha")]
    [InlineData("vNext")]
    [InlineData("0.1")]
    [InlineData("0..1")]
    [InlineData("0.1.x")]
    [InlineData("0.1.0-")]
    [InlineData("0.1.0-rc_1")]
    public void AppSurfaceCliVersion_UsesTruthfulFallbackWhenPackageMetadataIsUnavailable(string? rawVersion)
    {
        var version = AppSurfaceCliVersion.NormalizeDisplayVersion(rawVersion);

        Assert.Equal("unknown (package version metadata unavailable)", version);
    }

    [Fact]
    public void AppSurfaceCliVersion_UsesTruthfulFallbackWhenAssemblyLacksPackageMetadata()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("AppSurfaceCliVersionTests_NoMetadata"),
            AssemblyBuilderAccess.Run);

        var version = AppSurfaceCliVersion.ResolveDisplayVersion(assembly);

        Assert.Equal("unknown (package version metadata unavailable)", version);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Normalized_Package_Version_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["--version"]);
        var output = result.Stdout.Trim();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AppSurfaceCliVersion.ResolveDisplayVersion(typeof(AppSurfaceCliApp).Assembly), output);
        Assert.False(output.StartsWith('v'));
        Assert.DoesNotContain('+', output);
        Assert.DoesNotContain(Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Docs_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["docs", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Preview AppSurface Docs for a repository.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("docs export", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--repo", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--strict", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--public-origin", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--all-hosts", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--startup-timeout-seconds", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--redirects", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsCommand_Should_Forward_AppSurfaceDocs_Host_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            [
                "docs",
                "--repo", repository.Path,
                "--urls", "http://127.0.0.1:5189",
                "--strict",
                "--route-root", "/reference",
                "--docs-root", "/reference/next",
                "--public-origin", "https://forge-trust.com",
                "--environment", "Development"
            ],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Contains("--AppSurfaceDocs:Source:RepositoryRoot", runner.Args);
        Assert.Contains(repository.Path, runner.Args);
        Assert.Contains("--urls", runner.Args);
        Assert.Contains("http://127.0.0.1:5189", runner.Args);
        Assert.Contains("--AppSurfaceDocs:Harvest:FailOnFailure", runner.Args);
        Assert.Contains("true", runner.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:RouteRootPath", runner.Args);
        Assert.Contains("/reference", runner.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:DocsRootPath", runner.Args);
        Assert.Contains("/reference/next", runner.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:PublicOrigin", runner.Args);
        Assert.Contains("https://forge-trust.com", runner.Args);
        Assert.Contains("--environment", runner.Args);
        Assert.Contains("Development", runner.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsPreviewAlias_Should_Forward_AppSurfaceDocs_Host_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "preview", "--repo", repository.Path, "--port", "5189"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Contains("--AppSurfaceDocs:Source:RepositoryRoot", runner.Args);
        Assert.Contains(repository.Path, runner.Args);
        Assert.Contains("--port", runner.Args);
        Assert.Contains("5189", runner.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsPreviewAlias_Should_Forward_AllHosts_When_Port_Is_Configured()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "preview", "--repo", repository.Path, "--port", "5189", "--all-hosts"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Contains("--port", runner.Args);
        Assert.Contains("5189", runner.Args);
        Assert.Contains("--all-hosts", runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Default_ToDevelopmentEnvironment_ForLocalPreview()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var environmentIndex = Array.IndexOf(runner.Args, "--environment");
        Assert.InRange(environmentIndex, 0, runner.Args.Length - 2);
        Assert.Equal("Development", runner.Args[environmentIndex + 1]);
        Assert.DoesNotContain("--urls", runner.Args);
        Assert.DoesNotContain("--port", runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Preserve_Explicit_Environment()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--environment", "Production"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var environmentIndex = Array.IndexOf(runner.Args, "--environment");
        Assert.InRange(environmentIndex, 0, runner.Args.Length - 2);
        Assert.Equal("Production", runner.Args[environmentIndex + 1]);
    }

    [Fact]
    public async Task DocsCommand_Should_RunHost_FromRepositoryRoot()
    {
        using var callerDirectory = TempDirectory.Create("appsurface-docs-caller-");
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var previousDirectory = Directory.GetCurrentDirectory();
        var runner = new CapturingAppSurfaceDocsHostRunner();

        try
        {
            Directory.SetCurrentDirectory(callerDirectory.Path);

            var result = await InvokeProgramEntryPointAsync(
                ["docs", "--repo", repository.Path],
                options => RegisterRunner(options, runner));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                NormalizeDirectoryForComparison(repository.Path),
                NormalizeDirectoryForComparison(runner.WorkingDirectoryDuringRun));
            Assert.Equal(
                NormalizeDirectoryForComparison(callerDirectory.Path),
                NormalizeDirectoryForComparison(Directory.GetCurrentDirectory()));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task DocsCommand_Should_Allow_Disabling_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", "0"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Null(runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsCommand_Should_Translate_Startup_Timeout_To_CommandException()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner
        {
            Exception = new TimeoutException("Preview startup timed out.")
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Preview startup timed out.", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Blank_RepositoryRoot()
    {
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", " "],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --repo value must point to a repository directory.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task DocsCommand_Should_Reject_Invalid_Startup_Timeout(string timeout)
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", timeout],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The --startup-timeout-seconds value must be a finite number greater than or equal to 0.",
            result.AllText,
            StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Oversized_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", "1E+300"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The --startup-timeout-seconds value must be less than or equal to",
            result.AllText,
            StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public async Task DocsCommand_Should_Reject_Invalid_Port(string port)
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--port", port],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --port value must be between 1 and 65535.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_AllHosts_Without_Port()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--all-hosts"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --all-hosts option requires --port.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Missing_RepositoryRoot()
    {
        var missingRepository = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var runner = new CapturingAppSurfaceDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", missingRepository],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missingRepository, result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Docs_Export_Help_Without_Preview_Listener_Options()
    {
        var result = await InvokeEntryPointAsync(["docs", "export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Export AppSurface Docs for a repository to static files.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--output", result.AllText, StringComparison.Ordinal);
        Assert.Contains("dist/docs", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--mode", result.AllText, StringComparison.Ordinal);
        Assert.Contains("cdn", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--redirects", result.AllText, StringComparison.Ordinal);
        Assert.Contains("html", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("netlify", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--seeds", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--strict", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--publish-root-extras", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--port", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--urls", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--all-hosts", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Docs_Verify_Health_Help_Without_Preview_Listener_Options()
    {
        var result = await InvokeEntryPointAsync(["docs", "verify-health", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Verify AppSurface Docs harvest health for CI and release gates.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--require-complete-event-doclets", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--verify-event-dispatches", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--repo", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--startup-timeout-seconds", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--strict", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--port", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--urls", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--all-hosts", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Forward_StrictEventDoclets_And_HealthExposure()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = CreateHealthVerifyResult(ok: true)
        };

        var result = await InvokeProgramEntryPointAsync(
            [
                "docs", "verify-health",
                "--repo", repository.Path,
                "--require-complete-event-doclets",
                "--verify-event-dispatches",
                "--route-root", "/reference",
                "--docs-root", "/reference/next",
                "--environment", "Production",
                "--startup-timeout-seconds", "30"
            ],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var args = runner.Args.GetValueOrDefault();
        Assert.Equal("http://127.0.0.1:0", args.RequestedBaseUrl);
        Assert.Equal("/reference/next/_health.json", args.HealthJsonPath);
        Assert.Equal("Production", args.HostArgs.EnvironmentName);
        Assert.Equal(TimeSpan.FromSeconds(30), args.HostArgs.StartupTimeout);
        Assert.Contains("--AppSurfaceDocs:Harvest:StartupMode", args.HostArgs.Args);
        Assert.Contains(nameof(AppSurfaceDocsHarvestStartupMode.Disabled), args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Harvest:FailOnFailure", args.HostArgs.Args);
        Assert.Contains("false", args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Harvest:Health:ExposeRoutes", args.HostArgs.Args);
        Assert.Contains(nameof(AppSurfaceDocsHarvestHealthExposure.Always), args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Harvest:JavaScript:RequireCompleteEventDoclets", args.HostArgs.Args);
        Assert.Contains("true", args.HostArgs.Args);
        AssertForwardedValue(
            args.HostArgs.Args,
            "--AppSurfaceDocs:Harvest:JavaScript:VerifyEventDispatches",
            "true");
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_PrintWarningDiagnostics_WhenHealthIsOk()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = CreateHealthVerifyResult(ok: true, includeWarning: true)
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path, "--verify-event-dispatches"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("verified with 1 warning diagnostic", result.AllText, StringComparison.Ordinal);
        Assert.Contains("appsurfacedocs.javascript.event_doclet_dispatch_missing", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Cause: Verifier inputs include doclet evidence", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Fix: Add a matching literal CustomEvent dispatch", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableDiagnostics_WhenHealthIsNotOk()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = CreateHealthVerifyResult(ok: false)
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AppSurface Docs harvest health verification failed", result.AllText, StringComparison.Ordinal);
        Assert.Contains("appsurfacedocs.javascript.incomplete_public_event_doclet", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Cause: The item will render", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Add @target", result.AllText, StringComparison.Ordinal);
        Assert.NotNull(runner.Args);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableMessage_WhenHealthEndpointCannotBeRead()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Exception = new HttpRequestException("loopback refused")
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not read the health endpoint: loopback refused", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableMessage_WhenHealthEndpointTimesOut()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Exception = new OperationCanceledException(
                "The request was canceled.",
                new TimeoutException("The health endpoint did not respond before the timeout elapsed."))
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "could not read the health endpoint: The health endpoint did not respond before the timeout elapsed.",
            result.AllText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Reject_Unsupported_StrictOption()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = CreateHealthVerifyResult(ok: true)
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path, "--strict"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableMessage_WhenHealthJsonIsInvalid()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Exception = new JsonException("missing verification")
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("returned invalid JSON: missing verification", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableMessage_WhenStartupTimesOut()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Exception = new TimeoutException("verification host did not start")
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("verification host did not start", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WhenHttpStatusDiffersFromHealthJson()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = new AppSurfaceDocsHealthVerificationResult(
                new AppSurfaceDocsHarvestHealthResponse
                {
                    Status = nameof(DocHarvestHealthStatus.Healthy),
                    Verification = new AppSurfaceDocsHarvestHealthVerification
                    {
                        Ok = true,
                        HttpStatusCode = 200
                    }
                },
                HttpStatusCode.ServiceUnavailable)
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("inconsistent HTTP status", result.AllText, StringComparison.Ordinal);
        Assert.Contains("response HTTP 503", result.AllText, StringComparison.Ordinal);
        Assert.Contains("verification.httpStatusCode 200", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsVerifyHealthCommand_Should_Fail_WithReadableDiagnostics_WhenOptionalDiagnosticFieldsAreBlank()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new CapturingAppSurfaceDocsHealthVerifyRunner
        {
            Result = new AppSurfaceDocsHealthVerificationResult(
                new AppSurfaceDocsHarvestHealthResponse
                {
                    Status = nameof(DocHarvestHealthStatus.Failed),
                    Verification = new AppSurfaceDocsHarvestHealthVerification
                    {
                        Ok = false,
                        HttpStatusCode = 503
                    },
                    Diagnostics =
                    [
                        new AppSurfaceDocsHarvestDiagnosticResponse
                        {
                            Code = "appsurfacedocs.test",
                            Problem = "Harvest failed."
                        }
                    ]
                },
                HttpStatusCode.ServiceUnavailable)
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "verify-health", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("status Failed, HTTP 503", result.AllText, StringComparison.Ordinal);
        Assert.Contains("- appsurfacedocs.test: Harvest failed.", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Fix:", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceDocsHealthHttpClient_ShouldReturnBody_WhenHttpStatusIsUnavailable()
    {
        var client = new AppSurfaceDocsHealthHttpClient(
            new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.ServiceUnavailable, """{"status":"Degraded"}""")));

        var response = await client.GetAsync("http://127.0.0.1/docs/_health.json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"Degraded"}""", response.Body);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_Parse_HealthJson_WhenHttpStatusIsUnavailable()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61234");
        var starter = new ImmediateHealthHostStarter(host);
        var client = new CapturingHealthHttpClient(
            new AppSurfaceDocsHealthHttpResponse(
                HttpStatusCode.ServiceUnavailable,
                """
                {
                  "status": "Degraded",
                  "generatedUtc": "2026-05-25T12:00:00Z",
                  "verification": {
                    "ok": false,
                    "httpStatusCode": 503
                  },
                  "diagnostics": [
                    {
                      "code": "appsurfacedocs.javascript.incomplete_public_event_doclet",
                      "problem": "Missing @target."
                    }
                  ]
                }
                """));
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            client,
            starter);

        var result = await runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.HttpStatusCode);
        Assert.False(result.Health.Verification.Ok);
        Assert.Equal("Degraded", result.Health.Status);
        Assert.Equal("http://127.0.0.1:61234/docs/_health.json", client.Url);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
        Assert.Equal(
            NormalizeDirectoryForComparison(repository.Path),
            NormalizeDirectoryForComparison(starter.WorkingDirectoryDuringStart));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"status":"Healthy","verification":{"httpStatusCode":200}}""")]
    [InlineData("""{"status":"Healthy","verification":{"ok":true}}""")]
    [InlineData("""{"status":"Healthy","verification":{"ok":"true","httpStatusCode":200}}""")]
    [InlineData("""{"status":"Healthy","verification":{"ok":true,"httpStatusCode":"200"}}""")]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_Reject_Missing_HealthJson(string body)
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61235");
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, body)),
            new ImmediateHealthHostStarter(host));

        await Assert.ThrowsAsync<JsonException>(
            () => runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), CancellationToken.None));

        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_Fail_WhenHostStartupTimesOut()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var client = new CapturingHealthHttpClient(
            new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}"""));
        var starter = new CancelingHealthHostStarter();
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            client,
            starter);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.VerifyAsync(
                CreateHealthVerifyArgs(repository.Path, TimeSpan.FromMilliseconds(10)),
                CancellationToken.None));

        Assert.Contains("did not start", exception.Message, StringComparison.Ordinal);
        Assert.Null(client.Url);
        Assert.True(starter.StartupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_Verify_WhenStartupTimeoutIsDisabled()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61237");
        var starter = new ImmediateHealthHostStarter(host);
        var client = new CapturingHealthHttpClient(
            new AppSurfaceDocsHealthHttpResponse(
                HttpStatusCode.OK,
                """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}"""));
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            client,
            starter);

        var result = await runner.VerifyAsync(
            CreateHealthVerifyArgs(repository.Path, startupTimeout: null),
            CancellationToken.None);

        Assert.True(result.Health.Verification.Ok);
        Assert.Equal(HttpStatusCode.OK, result.HttpStatusCode);
        Assert.False(starter.StartupToken.IsCancellationRequested);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_DefaultEnvironment_WhenHostArgsOmitEnvironment()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61238");
        var starter = new ImmediateHealthHostStarter(host);
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            starter);

        var result = await runner.VerifyAsync(
            CreateHealthVerifyArgs(repository.Path, DefaultHealthVerifyStartupTimeout, environmentName: null),
            CancellationToken.None);

        Assert.True(result.Health.Verification.Ok);
        Assert.Equal(Environments.Production, starter.EnvironmentName);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_NotCancelStartupToken_WhenHostStartsBeforeTimeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61241");
        var starter = new ImmediateHealthHostStarter(host);
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            starter);

        var result = await runner.VerifyAsync(
            CreateHealthVerifyArgs(repository.Path, TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.True(result.Health.Verification.Ok);
        Assert.False(starter.StartupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_UseTrailingSlashBaseUrl()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61239/");
        var client = new CapturingHealthHttpClient(
            new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}"""));
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            client,
            new ImmediateHealthHostStarter(host));

        await runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), CancellationToken.None);

        Assert.Equal("http://127.0.0.1:61239/docs/_health.json", client.Url);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_ConvertImmediateStartupCancellationToTimeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            new ImmediatelyCanceledHealthHostStarter());

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), CancellationToken.None));

        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_DisposeHost_WhenStopFails()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        var host = new TrackingHost("http://127.0.0.1:61240", throwOnStop: true);
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            new ImmediateHealthHostStarter(host));

        var result = await runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), CancellationToken.None);

        Assert.True(result.Health.Verification.Ok);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_StopLateStartedHost_WhenCallerCancelsStartup()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        using var cancellation = new CancellationTokenSource();
        var host = new TrackingHost("http://127.0.0.1:61236");
        var starter = new ControllableHealthHostStarter();
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            NullLogger<AppSurfaceDocsInProcessHealthVerifyRunner>.Instance,
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            starter);

        var verificationTask = runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), cancellation.Token);
        await starter.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verificationTask);
        starter.Complete(host);
        await host.Disposed.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(starter.StartupToken.IsCancellationRequested);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessHealthVerifyRunner_Should_IgnoreLateStartupFailure_WhenCallerCancelsStartup()
    {
        using var repository = TempDirectory.Create("appsurface-docs-health-repo-");
        using var cancellation = new CancellationTokenSource();
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(loggerProvider);
        });
        var starter = new ControllableHealthHostStarter();
        var runner = new AppSurfaceDocsInProcessHealthVerifyRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsInProcessHealthVerifyRunner>(),
            new CapturingHealthHttpClient(
                new AppSurfaceDocsHealthHttpResponse(HttpStatusCode.OK, """{"status":"Healthy","verification":{"ok":true,"httpStatusCode":200}}""")),
            starter);

        var verificationTask = runner.VerifyAsync(CreateHealthVerifyArgs(repository.Path), cancellation.Token);
        await starter.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verificationTask);
        starter.Fail(new InvalidOperationException("Late startup failure."));
        await starter.Finished.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForLogMessageAsync(
            loggerProvider,
            "Late AppSurface Docs health verification startup task failed after verification stopped.");
    }

    [Fact]
    public async Task DocsExportCommand_Should_Default_Output_Environment_Mode_And_Derived_Seeds()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var args = runner.Args.GetValueOrDefault();
        Assert.Equal(Path.GetFullPath("dist/docs"), args.OutputPath);
        Assert.Equal(ExportMode.Cdn, args.Mode);
        Assert.NotNull(args.HybridOptions);
        Assert.Null(args.HybridOptions.LiveOrigin);
        Assert.Equal(RazorWireHybridCredentialsMode.Auto, args.HybridOptions.CredentialsMode);
        Assert.Equal(ExportRedirectStrategy.Html, args.RedirectStrategy);
        Assert.Null(args.SeedRoutesPath);
        Assert.Equal(["/", "/docs"], args.InitialSeedRoutes);
        Assert.Equal("http://127.0.0.1:0", args.RequestedBaseUrl);
        Assert.Equal("Production", args.HostArgs.EnvironmentName);
        Assert.Contains("--environment", args.HostArgs.Args);
        Assert.Contains("Production", args.HostArgs.Args);
        Assert.DoesNotContain("--urls", args.HostArgs.Args);
        Assert.DoesNotContain("--port", args.HostArgs.Args);
        Assert.DoesNotContain("--all-hosts", args.HostArgs.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), args.HostArgs.StartupTimeout);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Forward_Explicit_Export_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-output-");
        var seedFile = System.IO.Path.Join(repository.Path, "seeds.txt");
        await File.WriteAllLinesAsync(seedFile, ["/", "/reference/next"]);
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            [
                "docs", "export",
                "-r", repository.Path,
                "--output", output.Path,
                "--mode", "hybrid",
                "--redirects", "html",
                "--seeds", seedFile,
                "--strict",
                "--route-root", "/reference",
                "--docs-root", "/reference/next",
                "--public-origin", "https://forge-trust.com",
                "--live-origin", "https://api.forge-trust.com",
                "--hybrid-credentials", "omit",
                "--environment", "Development",
                "--startup-timeout-seconds", "2"
            ],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var args = runner.Args.GetValueOrDefault();
        Assert.Equal(output.Path, args.OutputPath);
        Assert.Equal(ExportMode.Hybrid, args.Mode);
        Assert.NotNull(args.HybridOptions);
        Assert.Equal("https://api.forge-trust.com", args.HybridOptions.LiveOrigin);
        Assert.Equal(RazorWireHybridCredentialsMode.Omit, args.HybridOptions.CredentialsMode);
        Assert.Equal(ExportRedirectStrategy.Html, args.RedirectStrategy);
        Assert.Equal(seedFile, args.SeedRoutesPath);
        Assert.Null(args.InitialSeedRoutes);
        Assert.Equal("http://127.0.0.1:0", args.RequestedBaseUrl);
        Assert.Equal("Development", args.HostArgs.EnvironmentName);
        Assert.Contains("--AppSurfaceDocs:Source:RepositoryRoot", args.HostArgs.Args);
        Assert.Contains(repository.Path, args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Harvest:FailOnFailure", args.HostArgs.Args);
        Assert.Contains("true", args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:RouteRootPath", args.HostArgs.Args);
        Assert.Contains("/reference", args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:DocsRootPath", args.HostArgs.Args);
        Assert.Contains("/reference/next", args.HostArgs.Args);
        Assert.Contains("--AppSurfaceDocs:Routing:PublicOrigin", args.HostArgs.Args);
        Assert.Contains("https://forge-trust.com", args.HostArgs.Args);
        Assert.Equal(TimeSpan.FromSeconds(2), args.HostArgs.StartupTimeout);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Invalid_LiveOrigin()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--live-origin", "https://api.example.com/path"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--live-origin", result.AllText, StringComparison.Ordinal);
        Assert.Contains("no path", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_AppSurface_Export_Help_With_PublishRootExtras()
    {
        var result = await InvokeEntryPointAsync(["export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Export an AppSurface/RazorWire application to static or hybrid files.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--publish-root-extras", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--seeds", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--public-origin", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceExportCommand_Should_Run_Generic_Hybrid_Export()
    {
        using var output = TempDirectory.Create("appsurface-export-output-");
        using var handler = new AppSurfaceExportHttpMessageHandler();

        var result = await InvokeProgramEntryPointAsync(
            [
                "export",
                "--url", "http://localhost:5000",
                "--output", output.Path,
                "--mode", "hybrid",
                "--public-origin", "https://www.example.com",
                "--live-origin", "https://api.example.com"
            ],
            options => options.CustomRegistrations.Add(
                services => services.AddSingleton<IHttpClientFactory>(new FixedHttpClientFactory(handler))));

        Assert.Equal(0, result.ExitCode);
        var html = await File.ReadAllTextAsync(Path.Join(output.Path, "index.html"));
        Assert.Contains("href=\"https://www.example.com/profile\"", html);
        Assert.Contains("data-rw-live-origin=\"https://api.example.com\"", html);
        Assert.Contains("action=\"https://api.example.com/profile/save\"", html);
        Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
    }

    [Fact]
    public async Task AppSurfaceExportCommand_Should_Copy_PublishRootExtrasManifest_Files()
    {
        using var output = TempDirectory.Create("appsurface-export-output-");
        using var deploy = TempDirectory.Create("appsurface-export-deploy-");
        using var handler = new AppSurfaceExportHttpMessageHandler();
        await File.WriteAllTextAsync(Path.Join(deploy.Path, "CNAME"), "docs.example.com");
        var manifestPath = Path.Join(deploy.Path, "export-extras.yml");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            version: 1
            extras:
              - source: CNAME
                publishPath: /CNAME
            """);

        var result = await InvokeProgramEntryPointAsync(
            [
                "export",
                "--url", "http://localhost:5000",
                "--output", output.Path,
                "--publish-root-extras", manifestPath,
                "--mode", "hybrid",
                "--public-origin", "https://www.example.com",
                "--live-origin", "https://api.example.com"
            ],
            options => options.CustomRegistrations.Add(
                services => services.AddSingleton<IHttpClientFactory>(new FixedHttpClientFactory(handler))));

        Assert.True(result.ExitCode == 0, result.AllText);
        Assert.Equal("docs.example.com", await File.ReadAllTextAsync(Path.Join(output.Path, "CNAME")));
    }

    [Fact]
    public void AppSurfaceExportCommand_Should_Reject_Null_Dependencies()
    {
        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, "<html></html>");
        var httpClientFactory = new FixedHttpClientFactory(handler);
        var logger = NullLogger<AppSurfaceExportCommand>.Instance;
        var engine = new ExportEngine(NullLogger<ExportEngine>.Instance, httpClientFactory);
        var requestFactory = new ExportSourceRequestFactory();
        var sourceResolver = new ExportSourceResolver(
            NullLoggerFactory.Instance,
            new TargetAppProcessFactory(),
            httpClientFactory);

        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceExportCommand(null!, engine, requestFactory, sourceResolver));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceExportCommand(logger, null!, requestFactory, sourceResolver));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceExportCommand(logger, engine, null!, sourceResolver));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceExportCommand(logger, engine, requestFactory, null!));
    }

    [Fact]
    public async Task AppSurfaceExportCommand_Should_Reject_Invalid_LiveOrigin()
    {
        var result = await InvokeProgramEntryPointAsync(
            ["export", "--url", "http://localhost:5000", "--live-origin", "https://api.example.com/path"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--live-origin", result.AllText, StringComparison.Ordinal);
        Assert.Contains("no path", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceExportCommand_Should_Reject_Invalid_PublicOrigin()
    {
        var result = await InvokeProgramEntryPointAsync(
            ["export", "--url", "http://localhost:5000", "--public-origin", "ftp://example.com"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--public-origin", result.AllText, StringComparison.Ordinal);
        Assert.Contains("absolute http or https origin", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceExportCommand_Should_Surface_ExportValidationFailures()
    {
        using var output = TempDirectory.Create("appsurface-export-output-");
        using var handler = new AppSurfaceExportHttpMessageHandler();

        var result = await InvokeProgramEntryPointAsync(
            [
                "export",
                "--url", "http://localhost:5000",
                "--output", output.Path,
                "--mode", "cdn"
            ],
            options => options.CustomRegistrations.Add(
                services => services.AddSingleton<IHttpClientFactory>(new FixedHttpClientFactory(handler))));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RWEXPORT", result.AllText, StringComparison.Ordinal);
        Assert.Contains("CDN mode", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Forward_Netlify_Redirect_Strategy()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--redirects", "netlify"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Equal(ExportRedirectStrategy.Netlify, runner.Args.GetValueOrDefault().RedirectStrategy);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Hybrid_Netlify_Redirect_Strategy()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--mode", "hybrid", "--redirects", "netlify"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--redirects netlify", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--mode cdn", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Unknown_Redirect_Strategy()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--redirects", "cloud"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cloud", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_NonEmpty_Output_Directory()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-output-");
        await File.WriteAllTextAsync(Path.Join(output.Path, "README.md.html"), "<html>stale</html>");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--output", output.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be empty before export starts", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Linked_OutputRoot_Before_EmptyDirectory_Preflight()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var outside = TempDirectory.Create("appsurface-docs-output-outside-");
        await File.WriteAllTextAsync(Path.Join(outside.Path, "README.md.html"), "<html>outside</html>");
        var linkedOutput = Path.Join(Path.GetTempPath(), $"appsurface-docs-output-link-{Guid.NewGuid():N}");
        try
        {
            if (!TryCreateDirectorySymlink(linkedOutput, outside.Path))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var runner = new CapturingAppSurfaceDocsExportRunner();

            var result = await InvokeProgramEntryPointAsync(
                ["docs", "export", "--repo", repository.Path, "--output", linkedOutput],
                options => RegisterRunner(options, runner));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("RWEXPORT009", result.AllText, StringComparison.Ordinal);
            Assert.Contains("[output-root-reparse]", result.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("must be empty before export starts", result.AllText, StringComparison.Ordinal);
            Assert.Null(runner.Args);
        }
        finally
        {
            DeleteDirectoryLinkIfExists(linkedOutput);
        }
    }

    [Fact]
    public async Task DocsExportCommand_Should_Derive_Default_Seed_From_Custom_DocsRoot()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--route-root", "/foo/bar", "--docs-root", "/foo/bar/next"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var args = runner.Args.GetValueOrDefault();
        Assert.Equal(["/", "/foo/bar/next"], args.InitialSeedRoutes);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Blank_Output()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--output", " "],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --output value must point to an export directory.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Output_File()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var outputFile = System.IO.Path.Join(repository.Path, "docs.html");
        await File.WriteAllTextAsync(outputFile, "not a directory");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--output", outputFile],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            $"The --output value must point to an export directory, but an existing file was found: {outputFile}",
            result.AllText,
            StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Missing_Seed_File()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var missingSeedFile = System.IO.Path.Join(repository.Path, "missing-seeds.txt");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--seeds", missingSeedFile],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"The --seeds file does not exist: {missingSeedFile}", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Seeds_Short_Alias()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "-s", "seeds.txt"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-s", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Translate_Export_Validation_Failures()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner
        {
            Exception = new ExportValidationException(
                [new ExportDiagnostic("RWEXPORT004", "Managed URL could not be rewritten.", "/docs")])
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RWEXPORT004", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Managed URL could not be rewritten.", result.AllText, StringComparison.Ordinal);
        Assert.NotNull(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Translate_Startup_Timeout_Failures()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingAppSurfaceDocsExportRunner
        {
            Exception = new TimeoutException("AppSurface Docs export host did not start within 10 seconds.")
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AppSurface Docs export host did not start within 10 seconds.", result.AllText, StringComparison.Ordinal);
        Assert.NotNull(runner.Args);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Resolve_Single_Bound_BaseUrl()
    {
        var result = AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://127.0.0.1:51234"]);

        Assert.Equal("http://127.0.0.1:51234", result);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Resolve_Localhost_Bound_BaseUrl()
    {
        var result = AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://localhost:51234"]);

        Assert.Equal("http://localhost:51234", result);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Reject_Missing_Bound_BaseUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(Array.Empty<string>()));

        Assert.Contains("did not publish a listening URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Reject_Multiple_Bound_BaseUrls()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://127.0.0.1:1", "http://127.0.0.1:2"]));

        Assert.Contains("published 2 listening URLs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Reject_Invalid_Bound_BaseUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(["not-a-url"]));

        Assert.Contains("did not publish a valid listening URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsInProcessExportRunner_Should_Reject_NonLoopback_Bound_BaseUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://0.0.0.0:51234"]));

        Assert.Contains("published non-loopback URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Export_When_Startup_Timeout_Is_Disabled()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var exporter = new CapturingStaticExporter();
        var host = new TrackingHost("http://127.0.0.1:61234");
        var hostStarter = new ImmediateExportHostStarter(host);
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter,
            hostStarter);

        await runner.ExportAsync(
            CreateExportArgs(
                repository.Path,
                output.Path,
                startupTimeout: null,
                redirectStrategy: ExportRedirectStrategy.Netlify),
            CancellationToken.None);

        Assert.NotNull(exporter.Context);
        Assert.Equal("http://127.0.0.1:61234", exporter.Context.BaseUrl);
        Assert.Equal(output.Path, exporter.Context.OutputPath);
        Assert.Equal(["/"], exporter.Context.InitialSeedRoutes);
        Assert.Equal(ExportRedirectStrategy.Netlify, exporter.Context.RedirectStrategy);
        Assert.Equal("Production", hostStarter.EnvironmentName);
        Assert.False(hostStarter.StartupToken.CanBeCanceled);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_RunHost_And_Export_FromRepositoryRoot()
    {
        using var callerDirectory = TempDirectory.Create("appsurface-docs-export-caller-");
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var previousDirectory = Directory.GetCurrentDirectory();
        var exporter = new CapturingStaticExporter();
        var hostStarter = new ImmediateExportHostStarter(new TrackingHost());
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter,
            hostStarter);

        try
        {
            Directory.SetCurrentDirectory(callerDirectory.Path);

            await runner.ExportAsync(CreateExportArgs(repository.Path, output.Path, startupTimeout: null), CancellationToken.None);

            Assert.Equal(
                NormalizeDirectoryForComparison(repository.Path),
                NormalizeDirectoryForComparison(hostStarter.WorkingDirectoryDuringStart));
            Assert.Equal(
                NormalizeDirectoryForComparison(repository.Path),
                NormalizeDirectoryForComparison(exporter.WorkingDirectoryDuringExport));
            Assert.Equal(
                NormalizeDirectoryForComparison(callerDirectory.Path),
                NormalizeDirectoryForComparison(Directory.GetCurrentDirectory()));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Render_PublicOrigin_Canonical_From_ExportHost()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var guidesDirectory = Path.Join(repository.Path, "guides");
        Directory.CreateDirectory(guidesDirectory);
        await File.WriteAllTextAsync(Path.Join(repository.Path, "README.md"), "# AppSurface Docs\n");
        await File.WriteAllTextAsync(Path.Join(guidesDirectory, "intro.md"), "# Intro\n\nStart here.\n");
        var exporter = new CanonicalInspectingStaticExporter(
            "/docs/guides/intro",
            """<link rel="canonical" href="https://forge-trust.com/docs/guides/intro" />""",
            "127.0.0.1");
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter);

        await runner.ExportAsync(
            CreateExportArgs(
                repository.Path,
                output.Path,
                TimeSpan.FromSeconds(10),
                additionalHostArgs:
                [
                    "--AppSurfaceDocs:Routing:PublicOrigin",
                    "https://forge-trust.com"
                ]),
            CancellationToken.None);

        Assert.True(exporter.Inspected);
    }

    [Fact]
    public async Task AppSurfaceDocsExportContextConfigurator_Should_Register_RouteManifest_Seeds_And_Redirects()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var docs = new[]
        {
            new DocNode("Package", "packages/README.md", "<p>Package</p>"),
            new DocNode(
                "Intro",
                "docs/intro.md",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "start-here/intro",
                    RedirectAliases = ["legacy/intro"]
                })
        };
        using var host = new TrackingHost(
            configureServices: services => AddDocsAggregatorServices(services, repository.Path, docs));
        var context = new ExportContext(output.Path, seedRoutesPath: null, baseUrl: "http://127.0.0.1:51234");

        await new AppSurfaceDocsExportContextConfigurator().ConfigureAsync(host, context, CancellationToken.None);

        Assert.True(context.ReleaseArchiveManifestEnabled);
        Assert.Contains("/docs/packages", context.AdditionalSeedRoutes);
        Assert.Contains("/docs/start-here/intro", context.AdditionalSeedRoutes);
        Assert.Contains(
            context.RedirectArtifacts,
            artifact => artifact.AliasRoute == "/docs/packages/README.md"
                        && artifact.CanonicalRoute == "/docs/packages");
        Assert.Contains(
            context.RedirectArtifacts,
            artifact => artifact.AliasRoute == "/docs/packages/README.md.html"
                        && artifact.CanonicalRoute == "/docs/packages");
        Assert.Contains(
            context.RedirectArtifacts,
            artifact => artifact.AliasRoute == "/docs/docs/intro.md.html"
                        && artifact.CanonicalRoute == "/docs/start-here/intro");
        Assert.Contains(
            context.RedirectArtifacts,
            artifact => artifact.AliasRoute == "/docs/legacy/intro"
                        && artifact.CanonicalRoute == "/docs/start-here/intro");
        var frozenManifest = await File.ReadAllTextAsync(Path.Join(output.Path, ".appsurface-docs-route-manifest.json"));
        Assert.Contains("\"schema\": \"appsurface-docs-route-manifest-v1\"", frozenManifest);
        Assert.Contains("\"canonicalRoutePath\": \"packages\"", frozenManifest);
        Assert.Contains("\"packages/README.md\"", frozenManifest);
        Assert.Contains("\"packages/README.md.html\"", frozenManifest);
        Assert.Contains("\"declaredAliases\": [", frozenManifest);
        Assert.Contains("\"legacy/intro\"", frozenManifest);
    }

    [Fact]
    public async Task AppSurfaceDocsExportContextConfigurator_Should_Reject_FrozenRouteManifest_TargetReparse()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        using var outside = TempDirectory.Create("appsurface-docs-export-outside-");
        var outsideManifest = Path.Join(outside.Path, ".appsurface-docs-route-manifest.json");
        await File.WriteAllTextAsync(outsideManifest, "outside");
        var manifestPath = Path.Join(output.Path, ".appsurface-docs-route-manifest.json");
        if (!TryCreateFileSymlink(manifestPath, outsideManifest))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
        }

        using var host = new TrackingHost(
            configureServices: services => AddDocsAggregatorServices(
                services,
                repository.Path,
                [new DocNode("Package", "packages/README.md", "<p>Package</p>")]));
        var context = new ExportContext(output.Path, seedRoutesPath: null, baseUrl: "http://127.0.0.1:51234");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => new AppSurfaceDocsExportContextConfigurator().ConfigureAsync(host, context, CancellationToken.None));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("RWEXPORT009", diagnostic.Code);
        Assert.Contains("[artifact-target-reparse]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("AppSurface Docs frozen route manifest", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideManifest));
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Translate_Startup_Cancellation_To_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var exporter = new CapturingStaticExporter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter,
            new CancelingExportHostStarter());

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.ExportAsync(
                CreateExportArgs(repository.Path, output.Path, TimeSpan.FromSeconds(30)),
                CancellationToken.None));

        Assert.Contains("AppSurface Docs export host did not start within 30 seconds.", ex.Message, StringComparison.Ordinal);
        Assert.IsType<OperationCanceledException>(ex.InnerException);
        Assert.Null(exporter.Context);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Preserve_External_Cancellation_During_Startup()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        using var cts = new CancellationTokenSource();
        var exporter = new CapturingStaticExporter();
        var hostStarter = new ControllableExportHostStarter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter,
            hostStarter);

        var exportTask = runner.ExportAsync(
            CreateExportArgs(repository.Path, output.Path, TimeSpan.FromSeconds(30)),
            cts.Token);
        await hostStarter.Started.WaitAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();

        try
        {
            var completedTask = await Task.WhenAny(exportTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(exportTask, completedTask);
            await Assert.ThrowsAsync<OperationCanceledException>(() => exportTask);

            Assert.True(hostStarter.StartupToken.IsCancellationRequested);
            Assert.Null(exporter.Context);

            var lateHost = new TrackingHost();
            hostStarter.Complete(lateHost);

            await lateHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(lateHost.StopCalled);
        }
        finally
        {
            hostStarter.Complete(new TrackingHost());
        }
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Log_When_Late_Startup_Faults_After_External_Cancellation()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        using var cts = new CancellationTokenSource();
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(loggerProvider);
        });
        var hostStarter = new ControllableExportHostStarter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsInProcessExportRunner>(),
            new CapturingStaticExporter(),
            hostStarter);

        var exportTask = runner.ExportAsync(
            CreateExportArgs(repository.Path, output.Path, TimeSpan.FromSeconds(30)),
            cts.Token);
        await hostStarter.Started.WaitAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => exportTask);

        hostStarter.Fail(new InvalidOperationException("Late startup fault."));

        await WaitForLogMessageAsync(loggerProvider, "completed after external cancellation.");
        Assert.DoesNotContain(
            loggerProvider.GetMessages(),
            message => message.Contains("completed after the startup timeout.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Log_When_Late_Startup_Faults_After_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(loggerProvider);
        });
        var hostStarter = new ControllableExportHostStarter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsInProcessExportRunner>(),
            new CapturingStaticExporter(),
            hostStarter);

        var exportTask = runner.ExportAsync(
            CreateExportArgs(repository.Path, output.Path, TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);
        await hostStarter.Started.WaitAsync(TimeSpan.FromSeconds(1));

        var completedTask = await Task.WhenAny(exportTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(exportTask, completedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => exportTask);

        hostStarter.Fail(new InvalidOperationException("Late startup fault."));
        await WaitForLogMessageAsync(loggerProvider, "completed after the startup timeout.");
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Log_And_Dispose_When_Host_Stop_Fails()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61235", throwOnStop: true);
        var runner = new AppSurfaceDocsInProcessExportRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsInProcessExportRunner>(),
            new CapturingStaticExporter(),
            new ImmediateExportHostStarter(host));

        await runner.ExportAsync(CreateExportArgs(repository.Path, output.Path, startupTimeout: null), CancellationToken.None);

        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("AppSurface Docs export host failed during shutdown.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_WriteStandalone404HtmlWithDocsSearchRecovery()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        await File.WriteAllTextAsync(Path.Join(repository.Path, "README.md"), "# AppSurface Docs\n");
        var exportEngine = new ExportEngine(
            NullLogger<ExportEngine>.Instance,
            new DefaultHttpClientFactory());
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            new RazorWireExportEngineAdapter(exportEngine));

        await runner.ExportAsync(
            CreateExportArgs(repository.Path, output.Path, TimeSpan.FromSeconds(15)),
            CancellationToken.None);

        var notFoundFile = Path.Join(output.Path, "404.html");
        Assert.True(File.Exists(notFoundFile));
        var html = await File.ReadAllTextAsync(notFoundFile);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("Search documentation", html);
        Assert.Contains("href=\"/docs/search.html\"", html);
        Assert.DoesNotContain("AppSurface default 404", html);
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Open_Docs_Url_When_Host_Is_Ready()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61236");
        var starter = new ImmediatePreviewHostStarter(host);
        var browserLauncher = new CapturingBrowserLauncher();
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            starter);

        var runTask = runner.RunAsync(
            [
                "--AppSurfaceDocs:Routing:DocsRootPath",
                "/reference/next",
                "--environment",
                "Development"
            ],
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        await browserLauncher.Opened.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("http://127.0.0.1:61236/reference/next", browserLauncher.Url?.AbsoluteUri);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("AppSurface Docs is ready at http://127.0.0.1:61236/reference/next", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_CommandOwned_Harvest_Summary()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61245");
        var browserLauncher = new CapturingBrowserLauncher();
        var harvestSummaryReader = new CapturingHarvestSummaryReader(
            new AppSurfaceDocsHarvestSummary(
                DocHarvestHealthStatus.Healthy,
                TotalDocs: 534,
                TotalHarvesters: 3,
                SuccessfulHarvesters: 3,
                DiagnosticCount: 0));
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            new ImmediatePreviewHostStarter(host),
            harvestSummaryReader);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), CancellationToken.None);
        await harvestSummaryReader.Read.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("Harvested 534 docs from 3/3 active harvesters. Status: Healthy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_Harvest_Diagnostics_Count()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61246");
        var harvestSummaryReader = new CapturingHarvestSummaryReader(
            new AppSurfaceDocsHarvestSummary(
                DocHarvestHealthStatus.Degraded,
                TotalDocs: 12,
                TotalHarvesters: 3,
                SuccessfulHarvesters: 2,
                DiagnosticCount: 1));
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            new ImmediatePreviewHostStarter(host),
            harvestSummaryReader);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), CancellationToken.None);
        await harvestSummaryReader.Read.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("Harvested 12 docs from 2/3 active harvesters. Status: Degraded; diagnostics: 1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Skip_Harvest_Summary_When_Unavailable()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61247");
        var harvestSummaryReader = new CapturingHarvestSummaryReader(summary: null);
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            new ImmediatePreviewHostStarter(host),
            harvestSummaryReader);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), CancellationToken.None);
        await harvestSummaryReader.Read.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(
            loggerProvider.GetMessages(),
            message => message.Contains("Harvested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_When_Harvest_Summary_Fails()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61248");
        var harvestSummaryReader = new ThrowingHarvestSummaryReader(new InvalidOperationException("health unavailable"));
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            new ImmediatePreviewHostStarter(host),
            harvestSummaryReader);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), CancellationToken.None);
        await harvestSummaryReader.Read.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("AppSurface Docs harvest summary could not be read.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Preserve_Cancellation_When_Harvest_Summary_Is_Canceled()
    {
        using var cts = new CancellationTokenSource();
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61249");
        var harvestSummaryReader = new CancelingHarvestSummaryReader(cts);
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            new ImmediatePreviewHostStarter(host),
            harvestSummaryReader);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsHarvestSummaryReader_Should_Return_Null_When_Aggregator_Is_Not_Registered()
    {
        using var host = new TrackingHost();
        var reader = new AppSurfaceDocsHarvestSummaryReader();

        var summary = await reader.ReadAsync(host, CancellationToken.None);

        Assert.Null(summary);
    }

    [Fact]
    public async Task AppSurfaceDocsHarvestSummaryReader_Should_Read_Aggregator_Health()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [new StaticDocHarvester([new DocNode("Intro", "README.md", "<p>Intro</p>")])],
            new AppSurfaceDocsOptions
            {
                Source = new AppSurfaceDocsSourceOptions
                {
                    RepositoryRoot = repository.Path
                }
            },
            new TestWebHostEnvironment(repository.Path),
            new Memo(memoryCache),
            new PassthroughDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);
        using var host = new TrackingHost(configureServices: services => services.AddSingleton(aggregator));
        var reader = new AppSurfaceDocsHarvestSummaryReader();

        var summary = await reader.ReadAsync(host, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(DocHarvestHealthStatus.Healthy, summary.Status);
        Assert.Equal(1, summary.TotalDocs);
        Assert.Equal(1, summary.TotalHarvesters);
        Assert.Equal(1, summary.SuccessfulHarvesters);
        Assert.Equal(0, summary.DiagnosticCount);
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_When_Browser_Launch_Fails()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var host = new TrackingHost("http://127.0.0.1:61237");
        var browserLauncher = new CapturingBrowserLauncher(AppSurfaceDocsBrowserLaunchResult.Failure("no browser"));
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            new ImmediatePreviewHostStarter(host));

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(10), CancellationToken.None);
        await browserLauncher.Opened.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("browser could not be opened automatically: no browser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Run_When_Startup_Timeout_Is_Disabled()
    {
        var host = new TrackingHost("http://127.0.0.1:61250");
        var starter = new ImmediatePreviewHostStarter(host);
        var browserLauncher = new CapturingBrowserLauncher();
        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            starter);

        var runTask = runner.RunAsync(["--environment", "Development"], startupTimeout: null, CancellationToken.None);
        await browserLauncher.Opened.WaitAsync(TimeSpan.FromSeconds(1));
        host.Lifetime.StopApplication();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(starter.StartupToken.CanBeCanceled);
        Assert.Equal("http://127.0.0.1:61250/docs", browserLauncher.Url?.AbsoluteUri);
        Assert.True(host.StopCalled);
        Assert.True(host.DisposeCalled);
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Timeout_When_Host_Build_Does_Not_Complete()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var starter = new ControllablePreviewHostStarter();
        var browserLauncher = new CapturingBrowserLauncher();
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            starter);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await starter.Started.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var completedTask = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(runTask, completedTask);
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => runTask);

            Assert.Contains("AppSurface Docs preview host did not start within 0.05 seconds.", ex.Message, StringComparison.Ordinal);
            Assert.True(starter.StartupToken.IsCancellationRequested);
            Assert.Null(browserLauncher.Url);

            var lateHost = new TrackingHost();
            starter.Complete(lateHost);

            await lateHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(lateHost.StopCalled);
        }
        finally
        {
            starter.Complete(new TrackingHost());
        }
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Preserve_External_Cancellation_During_Startup()
    {
        using var cts = new CancellationTokenSource();
        var starter = new ControllablePreviewHostStarter();
        var browserLauncher = new CapturingBrowserLauncher();
        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            browserLauncher,
            starter);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(30), cts.Token);
        await starter.Started.WaitAsync(TimeSpan.FromSeconds(1));
        await cts.CancelAsync();

        try
        {
            var completedTask = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(runTask, completedTask);
            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

            Assert.True(starter.StartupToken.IsCancellationRequested);
            Assert.Null(browserLauncher.Url);

            var lateHost = new TrackingHost();
            starter.Complete(lateHost);

            await lateHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(lateHost.StopCalled);
            Assert.True(lateHost.DisposeCalled);
        }
        finally
        {
            starter.Complete(new TrackingHost());
        }
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Translate_Startup_Cancellation_To_Startup_Timeout()
    {
        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            new CancelingPreviewHostStarter());

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(["--environment", "Development"], TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Contains("AppSurface Docs preview host did not start within 30 seconds.", ex.Message, StringComparison.Ordinal);
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task SystemAppSurfaceDocsBrowserLauncher_Should_Open_Through_Command_Runner()
    {
        var commandRunner = new CapturingBrowserOpenCommandRunner();
        var launcher = new SystemAppSurfaceDocsBrowserLauncher(commandRunner);
        var url = new Uri("http://127.0.0.1:61251/docs");

        var result = await launcher.TryOpenAsync(url, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Equal(url, commandRunner.Url);
    }

    [Fact]
    public async Task SystemAppSurfaceDocsBrowserLauncher_Should_Return_Failure_When_Command_Runner_Fails()
    {
        var launcher = new SystemAppSurfaceDocsBrowserLauncher(
            new CapturingBrowserOpenCommandRunner(new InvalidOperationException("opener failed")));

        var result = await launcher.TryOpenAsync(new Uri("http://127.0.0.1:61251/docs"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("opener failed", result.FailureReason);
    }

    [Fact]
    public async Task SystemAppSurfaceDocsBrowserLauncher_Should_Preserve_Cancellation_When_Command_Runner_Is_Canceled()
    {
        using var cts = new CancellationTokenSource();
        var launcher = new SystemAppSurfaceDocsBrowserLauncher(new CancelingBrowserOpenCommandRunner(cts));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => launcher.TryOpenAsync(new Uri("http://127.0.0.1:61251/docs"), cts.Token));
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_When_Late_Startup_Faults_After_Timeout()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(loggerProvider);
        });
        var starter = new ControllablePreviewHostStarter();
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            starter);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await starter.Started.WaitAsync(TimeSpan.FromSeconds(1));

        var completedTask = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(runTask, completedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => runTask);

        starter.Fail(new InvalidOperationException("Late preview startup fault."));
        await WaitForLogMessageAsync(loggerProvider, "preview host startup task completed after the startup timeout.");
    }

    [Fact]
    public async Task AppSurfaceDocsStandaloneHostRunner_Should_Log_And_Dispose_When_Host_Stop_Fails()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var starter = new ControllablePreviewHostStarter();
        var runner = new AppSurfaceDocsStandaloneHostRunner(
            loggerFactory.CreateLogger<AppSurfaceDocsStandaloneHostRunner>(),
            new CapturingBrowserLauncher(),
            starter);

        var runTask = runner.RunAsync(["--environment", "Development"], TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await starter.Started.WaitAsync(TimeSpan.FromSeconds(1));

        var completedTask = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(runTask, completedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => runTask);

        var lateHost = new TrackingHost("http://127.0.0.1:61251", throwOnStop: true);
        starter.Complete(lateHost);

        await lateHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains("AppSurface Docs preview host failed during shutdown.", StringComparison.Ordinal));
    }

    [Fact]
    public void AppSurfaceDocsCliHost_Should_Suppress_Routine_Preview_Host_Logs()
    {
        var loggerProvider = new InMemoryLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            AppSurfaceDocsCliHost.ConfigureQuietPreviewLogging(builder);
            builder.AddProvider(loggerProvider);
        });

        loggerFactory.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Now listening on: http://localhost:6189");
        loggerFactory.CreateLogger("ForgeTrust.AppSurface.Web.Startup").LogInformation("Endpoint fallback enabled.");
        loggerFactory.CreateLogger("ForgeTrust.AppSurface.Docs.Services.DocAggregator").LogInformation("Generated docs snapshot.");
        loggerFactory.CreateLogger("ForgeTrust.AppSurface.Docs.Services.DocAggregator").LogWarning("Docs harvest warning.");
        loggerFactory.CreateLogger("ForgeTrust.AppSurface.Cli.DocsCommand").LogInformation("Starting AppSurface Docs preview.");

        var messages = loggerProvider.GetMessages();
        Assert.DoesNotContain(messages, message => message.Contains("Now listening on", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("Endpoint fallback enabled.", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("Generated docs snapshot.", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Docs harvest warning.", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Starting AppSurface Docs preview.", StringComparison.Ordinal));
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Select_Loopback_Address_For_Browser()
    {
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://*:61238");
        addresses.Addresses.Add("http://127.0.0.1:61238");

        var baseUrl = AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(addresses.Addresses);

        Assert.Equal("http://127.0.0.1:61238", baseUrl);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Replace_Wildcard_Address_For_Browser()
    {
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://*:61239");

        var baseUrl = AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(addresses.Addresses);

        Assert.Equal("http://localhost:61239", baseUrl);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Replace_AnyHost_Address_For_Browser()
    {
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://0.0.0.0:61253");

        var baseUrl = AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(addresses.Addresses);

        Assert.Equal("http://localhost:61253", baseUrl);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Use_RouteRoot_When_DocsRoot_Is_Not_Configured()
    {
        var docsUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDocsUrl(
            "http://127.0.0.1:61241",
            [
                "--AppSurfaceDocs:Routing:RouteRootPath",
                "/reference"
            ]);

        Assert.Equal("http://127.0.0.1:61241/reference", docsUrl.AbsoluteUri);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Use_DocsRoot_With_Equals_Syntax()
    {
        var docsUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDocsUrl(
            "http://127.0.0.1:61252",
            [
                "--AppSurfaceDocs:Routing:RouteRootPath",
                "/reference",
                "--AppSurfaceDocs:Routing:DocsRootPath=/reference/current"
            ]);

        Assert.Equal("http://127.0.0.1:61252/reference/current", docsUrl.AbsoluteUri);
    }

    [Fact]
    public async Task AppSurfaceDocsPreviewUrlResolver_Should_Not_Default_When_Appsettings_Configures_Endpoint()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        await File.WriteAllTextAsync(
            Path.Join(repository.Path, "appsettings.Development.json"),
            """
            {
              "urls": "http://127.0.0.1:61240"
            }
            """);

        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(
            ["--environment", "Development"],
            repository.Path);

        Assert.Null(defaultUrl);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Use_Forwarded_Repository_Root()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        using var fallback = TempDirectory.Create("appsurface-docs-preview-fallback-");

        var repositoryRoot = AppSurfaceDocsPreviewUrlResolver.ResolveRepositoryRoot(
            ["--AppSurfaceDocs:Source:RepositoryRoot", repository.Path],
            fallback.Path);

        Assert.Equal(repository.Path, repositoryRoot);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Fallback_When_Repository_Root_Is_Not_Forwarded()
    {
        using var fallback = TempDirectory.Create("appsurface-docs-preview-fallback-");

        var repositoryRoot = AppSurfaceDocsPreviewUrlResolver.ResolveRepositoryRoot(
            ["--environment", "Development"],
            fallback.Path);

        Assert.Equal(fallback.Path, repositoryRoot);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Default_To_Deterministic_Development_Url()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");

        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(
            ["--environment", "Development"],
            repository.Path);

        Assert.StartsWith("http://localhost:", defaultUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Not_Default_For_Production()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");

        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(
            ["--environment", "Production"],
            repository.Path);

        Assert.Null(defaultUrl);
    }

    [Theory]
    [InlineData("--port", "61242")]
    [InlineData("--port=61242", null)]
    [InlineData("--urls", "http://127.0.0.1:61242")]
    [InlineData("--urls=http://127.0.0.1:61242", null)]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Not_Default_When_Endpoint_Argument_Is_Configured(
        string firstArg,
        string? secondArg)
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        var args = secondArg is null
            ? new[] { "--environment", "Development", firstArg }
            : ["--environment", "Development", firstArg, secondArg];

        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(args, repository.Path);

        Assert.Null(defaultUrl);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Not_Default_When_Endpoint_Environment_Is_Configured()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        var previousValue = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:61243");

            var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(
                ["--environment", "Development"],
                repository.Path);

            Assert.Null(defaultUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previousValue);
        }
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Not_Default_When_CommandLine_Configures_Kestrel_Endpoint()
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");

        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(
            [
                "--environment",
                "Development",
                "--Kestrel:Endpoints:Http:Url",
                "http://127.0.0.1:61244"
            ],
            repository.Path);

        Assert.Null(defaultUrl);
    }

    [Theory]
    [InlineData("--environment=Development")]
    [InlineData("--environment", "--Some:Setting", "value")]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Ignore_Incomplete_Environment_Probe_Arguments(params string[] args)
    {
        using var repository = TempDirectory.Create("appsurface-docs-preview-repo-");
        var previousValue = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(args, repository.Path);

            Assert.StartsWith("http://localhost:", defaultUrl, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousValue);
        }
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Throw_When_No_Addresses_Are_Published()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(Array.Empty<string>()));

        Assert.Contains("No addresses were published", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsPreviewUrlResolver_Should_Throw_When_No_Valid_Addresses_Are_Published()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(["not-a-url"]));

        Assert.Contains("did not publish a valid listening URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Not_Translate_Exporter_Cancellation_To_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var exporter = new CancelingStaticExporter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter);
        var hostArgs = new AppSurfaceDocsHostArgs(
            repository.Path,
            [
                "--AppSurfaceDocs:Source:RepositoryRoot",
                repository.Path,
                "--environment",
                "Production"
            ],
            TimeSpan.FromSeconds(30),
            "Production");
        var exportArgs = new AppSurfaceDocsExportArgs(
            hostArgs,
            output.Path,
            SeedRoutesPath: null,
            InitialSeedRoutes: ["/"],
            ExportMode.Cdn,
            ExportRedirectStrategy.Html,
            "http://127.0.0.1:0");

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.ExportAsync(exportArgs, CancellationToken.None));

        Assert.Equal("Export canceled after startup.", ex.Message);
        Assert.NotNull(exporter.Context);
    }

    [Fact]
    public async Task AppSurfaceDocsInProcessExportRunner_Should_Timeout_When_Host_Build_Does_Not_Complete()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var exporter = new CapturingStaticExporter();
        var hostStarter = new ControllableExportHostStarter();
        var runner = new AppSurfaceDocsInProcessExportRunner(
            NullLogger<AppSurfaceDocsInProcessExportRunner>.Instance,
            exporter,
            hostStarter);
        var hostArgs = new AppSurfaceDocsHostArgs(
            repository.Path,
            [
                "--AppSurfaceDocs:Source:RepositoryRoot",
                repository.Path,
                "--environment",
                "Production"
            ],
            TimeSpan.FromMilliseconds(50),
            "Production");
        var exportArgs = new AppSurfaceDocsExportArgs(
            hostArgs,
            output.Path,
            SeedRoutesPath: null,
            InitialSeedRoutes: ["/"],
            ExportMode.Cdn,
            ExportRedirectStrategy.Html,
            "http://127.0.0.1:0");

        var exportTask = runner.ExportAsync(exportArgs, CancellationToken.None);
        await hostStarter.Started.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var completedTask = await Task.WhenAny(exportTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(exportTask, completedTask);
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => exportTask);

            Assert.Contains("AppSurface Docs export host did not start within 0.05 seconds.", ex.Message, StringComparison.Ordinal);
            Assert.True(hostStarter.StartupToken.IsCancellationRequested);
            Assert.Null(exporter.Context);

            var lateHost = new TrackingHost();
            hostStarter.Complete(lateHost);

            await lateHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(lateHost.StopCalled);
        }
        finally
        {
            hostStarter.Complete(new TrackingHost());
        }
    }

    [Fact]
    public void PushConfigureOptionsOverrideForTests_Should_Throw_When_ConfigureOptions_IsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ProgramEntryPoint.PushConfigureOptionsOverrideForTests(null!));
    }

    [Fact]
    public async Task ProgramEntryPoint_Should_Apply_Direct_And_Test_ConfigureOptions_InOrder()
    {
        var calls = new List<string>();
        using var overrideScope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(
            _ => calls.Add("override"));

        await ProgramEntryPoint.RunAsync(
            ["--help"],
            _ => calls.Add("direct"));

        Assert.Equal(["direct", "override"], calls);
    }

    [Fact]
    public void AppSurfaceCliModule_NoOpHooks_DoNotThrow()
    {
        var module = new AppSurfaceCliModule();
        var context = new StartupContext([], module);
        var services = new ServiceCollection();
        var builder = Host.CreateDefaultBuilder();
        var dependencies = new ModuleDependencyBuilder();

        module.ConfigureServices(context, services);
        module.ConfigureHostBeforeServices(context, builder);
        module.ConfigureHostAfterServices(context, builder);
        module.RegisterDependentModules(dependencies);
    }

    private static async Task<CapturedCliRun> InvokeEntryPointAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null)
    {
        var console = new FakeInMemoryConsole();
        var loggerProvider = new InMemoryLoggerProvider();
        var originalExitCode = Environment.ExitCode;
        var originalStdout = System.Console.Out;
        var originalStderr = System.Console.Error;
        using var rawStdoutWriter = new StringWriter();
        using var rawStderrWriter = new StringWriter();
        using var overrideScope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(
            options =>
            {
                AddCaptureServices(options, console, loggerProvider);
                configureOptions?.Invoke(options);
            });

        try
        {
            Environment.ExitCode = 0;
            System.Console.SetOut(rawStdoutWriter);
            System.Console.SetError(rawStderrWriter);
            await ProgramEntryPoint.RunAsync(
                args,
                options =>
                {
                    AddCaptureServices(options, console, loggerProvider);
                    configureOptions?.Invoke(options);
                });

            return new CapturedCliRun(
                rawStdoutWriter.ToString(),
                rawStderrWriter.ToString(),
                console.ReadOutputString(),
                console.ReadErrorString(),
                loggerProvider.GetMessages(),
                Environment.ExitCode);
        }
        finally
        {
            System.Console.SetOut(originalStdout);
            System.Console.SetError(originalStderr);
            Environment.ExitCode = originalExitCode;
        }
    }

    private static async Task<CapturedCliRun> InvokeProgramEntryPointAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null,
        string? standardInput = null)
    {
        var console = new FakeInMemoryConsole();
        if (standardInput != null)
        {
            console.WriteInput(standardInput);
        }

        var loggerProvider = new InMemoryLoggerProvider();
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await ProgramEntryPoint.RunAsync(
                args,
                options =>
                {
                    AddCaptureServices(options, console, loggerProvider);
                    configureOptions?.Invoke(options);
                });

            return new CapturedCliRun(
                string.Empty,
                string.Empty,
                console.ReadOutputString(),
                console.ReadErrorString(),
                loggerProvider.GetMessages(),
                Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    private static void AddCaptureServices(
        ConsoleOptions options,
        FakeInMemoryConsole console,
        InMemoryLoggerProvider loggerProvider)
    {
        options.CustomRegistrations.Add(services =>
        {
            services.AddSingleton<IConsole>(console);
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });
    }

    private static void RegisterRunner(ConsoleOptions options, CapturingAppSurfaceDocsHostRunner runner)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IAppSurfaceDocsHostRunner>(runner));
    }

    private static void RegisterGoogleTransferClient(ConsoleOptions options, FakeGoogleSecretTransferClient client)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IAppSurfaceGoogleSecretTransferClient>(client));
    }

    private static void RegisterRunner(ConsoleOptions options, CapturingAppSurfaceDocsExportRunner runner)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IAppSurfaceDocsExportRunner>(runner));
    }

    private static void RegisterRunner(ConsoleOptions options, CapturingAppSurfaceDocsHealthVerifyRunner runner)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IAppSurfaceDocsHealthVerifyRunner>(runner));
    }

    private static void AssertForwardedValue(IReadOnlyList<string> args, string key, string expectedValue)
    {
        var index = Array.IndexOf(args.ToArray(), key);
        Assert.True(index >= 0, $"Expected forwarded argument '{key}' to be present.");
        Assert.True(index + 1 < args.Count, $"Expected forwarded argument '{key}' to have a value.");
        Assert.Equal(expectedValue, args[index + 1]);
    }

    private static AppSurfaceDocsHealthVerificationResult CreateHealthVerifyResult(bool ok, bool includeWarning = false)
    {
        return new AppSurfaceDocsHealthVerificationResult(
            new AppSurfaceDocsHarvestHealthResponse
            {
                Status = ok ? nameof(DocHarvestHealthStatus.Healthy) : nameof(DocHarvestHealthStatus.Degraded),
                Verification = new AppSurfaceDocsHarvestHealthVerification
                {
                    Ok = ok,
                    HttpStatusCode = ok ? 200 : 503
                },
                Diagnostics = ok
                    ? includeWarning
                        ?
                        [
                            new AppSurfaceDocsHarvestDiagnosticResponse
                            {
                                Code = DocHarvestDiagnosticCodes.JavaScriptEventDocletDispatchMissing,
                                Severity = nameof(DocHarvestDiagnosticSeverity.Warning),
                                HarvesterType = nameof(JavaScriptDocHarvester),
                                Problem = "Public JavaScript event doclet 'razorwire:missing-dispatch' has no matching literal CustomEvent dispatch.",
                                Cause = "Verifier inputs include doclet evidence at src/public-api.js:1 but no direct dispatch evidence.",
                                Fix = "Add a matching literal CustomEvent dispatch to the verified JavaScript inputs."
                            }
                        ]
                        : []
                    :
                    [
                        new AppSurfaceDocsHarvestDiagnosticResponse
                        {
                            Code = DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet,
                            Severity = nameof(DocHarvestDiagnosticSeverity.Error),
                            HarvesterType = nameof(JavaScriptDocHarvester),
                            Problem = "JavaScript Event 'razorwire:missing' is missing public contract fields.",
                            Cause = "The item will render, but readers may not know enough about the public browser contract to consume it confidently.",
                            Fix = "Add @target, @firesWhen, @property detail.* or @detail none to the public JavaScript doclet."
                        }
                    ]
            },
            ok ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
    }

    private static AppSurfaceDocsHealthVerifyArgs CreateHealthVerifyArgs(string repositoryPath)
    {
        return CreateHealthVerifyArgs(repositoryPath, DefaultHealthVerifyStartupTimeout);
    }

    private static AppSurfaceDocsHealthVerifyArgs CreateHealthVerifyArgs(
        string repositoryPath,
        TimeSpan? startupTimeout)
    {
        return CreateHealthVerifyArgs(repositoryPath, startupTimeout, Environments.Production);
    }

    private static AppSurfaceDocsHealthVerifyArgs CreateHealthVerifyArgs(
        string repositoryPath,
        TimeSpan? startupTimeout,
        string? environmentName)
    {
        var hostArgs = new AppSurfaceDocsHostArgs(
            repositoryPath,
            ["--AppSurfaceDocs:Source:RepositoryRoot", repositoryPath],
            startupTimeout,
            environmentName);
        return new AppSurfaceDocsHealthVerifyArgs(
            hostArgs,
            "/docs/_health.json",
            "http://127.0.0.1:0");
    }

    private static AppSurfaceDocsExportArgs CreateExportArgs(
        string repositoryPath,
        string outputPath,
        TimeSpan? startupTimeout,
        ExportRedirectStrategy redirectStrategy = ExportRedirectStrategy.Html,
        params string[] additionalHostArgs)
    {
        var args = new List<string>
        {
            "--AppSurfaceDocs:Source:RepositoryRoot",
            repositoryPath,
            "--environment",
            "Production"
        };
        args.AddRange(additionalHostArgs);

        var hostArgs = new AppSurfaceDocsHostArgs(
            repositoryPath,
            args.ToArray(),
            startupTimeout,
            "Production");

        return new AppSurfaceDocsExportArgs(
            hostArgs,
            outputPath,
            SeedRoutesPath: null,
            InitialSeedRoutes: ["/"],
            ExportMode.Cdn,
            redirectStrategy,
            "http://127.0.0.1:0");
    }

    private static void AddDocsAggregatorServices(
        IServiceCollection services,
        string repositoryPath,
        IReadOnlyList<DocNode> docs)
    {
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(repositoryPath));
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<IMemo, Memo>();
        services.AddSingleton<IAppSurfaceDocsHtmlSanitizer, PassthroughDocsHtmlSanitizer>();
        services.AddSingleton<IDocHarvester>(new StaticDocHarvester(docs));
        services.AddSingleton(new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions
            {
                RepositoryRoot = repositoryPath
            }
        });
        services.AddSingleton<ILogger<DocAggregator>>(NullLogger<DocAggregator>.Instance);
        services.AddSingleton<DocAggregator>();
    }

    private static async Task WaitForLogMessageAsync(InMemoryLoggerProvider loggerProvider, string expectedMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (loggerProvider.GetMessages().Any(message => message.Contains(expectedMessage, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Contains(
            loggerProvider.GetMessages(),
            message => message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    private static string NormalizeDirectoryForComparison(string? path)
    {
        Assert.NotNull(path);

        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return OperatingSystem.IsMacOS() && normalized.StartsWith("/private/var/", StringComparison.Ordinal)
            ? normalized["/private".Length..]
            : normalized;
    }

    private sealed record CapturedCliRun(
        string RawStdout,
        string RawStderr,
        string Stdout,
        string Stderr,
        IReadOnlyList<string> LogMessages,
        int ExitCode)
    {
        public string AllText =>
            string.Join(
                Environment.NewLine,
                new[]
                {
                    RawStdout,
                    RawStderr,
                    Stdout,
                    Stderr,
                    string.Join(Environment.NewLine, LogMessages)
                });
    }

    private sealed class FakeGoogleSecretTransferClient : IAppSurfaceGoogleSecretTransferClient
    {
        public Dictionary<string, bool> Secrets { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Versions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, GoogleSecretManagerTransferStatus> VersionProbeFailures { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, GoogleSecretManagerTransferStatus> AccessFailures { get; } = new(StringComparer.Ordinal);

        public List<(string ResourceName, string Value)> Writes { get; } = [];

        public int ProbeSecretCalls { get; private set; }

        public int VersionProbeCalls { get; private set; }

        public int AccessCalls { get; private set; }

        public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout)
        {
            ProbeSecretCalls++;
            return Secrets.TryGetValue(secretResourceName, out var hasEnabledVersions)
                ? AppSurfaceGoogleSecretProbeResult.Ready(secretResourceName, hasEnabledVersions)
                : AppSurfaceGoogleSecretProbeResult.Failed(
                    GoogleSecretManagerTransferStatus.Missing,
                    secretResourceName,
                    CreateDiagnostic(GoogleSecretManagerTransferStatus.Missing));
        }

        public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout)
        {
            VersionProbeCalls++;
            if (VersionProbeFailures.TryGetValue(versionResourceName, out var status))
            {
                return AppSurfaceGoogleSecretProbeResult.Failed(status, versionResourceName, CreateDiagnostic(status));
            }

            return Versions.ContainsKey(versionResourceName)
                    ? AppSurfaceGoogleSecretProbeResult.Ready(versionResourceName)
                    : AppSurfaceGoogleSecretProbeResult.Failed(
                        GoogleSecretManagerTransferStatus.Missing,
                        versionResourceName,
                        CreateDiagnostic(GoogleSecretManagerTransferStatus.Missing));
        }

        public AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout)
        {
            AccessCalls++;
            if (AccessFailures.TryGetValue(versionResourceName, out var status))
            {
                return AppSurfaceGoogleSecretAccessResult.Failed(status, versionResourceName, CreateDiagnostic(status));
            }

            return AppSurfaceGoogleSecretAccessResult.Accessed(
                versionResourceName,
                new AppSurfaceGoogleSecretPayload(Versions[versionResourceName], versionResourceName));
        }

        public AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout)
        {
            Writes.Add((secretResourceName, value));
            return AppSurfaceGoogleSecretWriteResult.Written(
                secretResourceName,
                $"{secretResourceName}/versions/{Writes.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        private static AppSurfaceGoogleSecretTransferDiagnostic CreateDiagnostic(GoogleSecretManagerTransferStatus status) =>
            new(
                status switch
                {
                    GoogleSecretManagerTransferStatus.AccessDenied => "google-secret-transfer-access-denied",
                    GoogleSecretManagerTransferStatus.NotEnabled => "google-secret-transfer-version-not-enabled",
                    _ => "fake-google-secret-transfer-diagnostic"
                },
                "Google Secret Manager fake transfer failed.",
                $"The fake transfer client returned {status}.",
                "Adjust the fake transfer test setup.",
                "google-secret-manager-transfer",
                false);
    }

    private sealed class CapturingAppSurfaceDocsHostRunner : IAppSurfaceDocsHostRunner
    {
        public string[]? Args { get; private set; }

        public TimeSpan? StartupTimeout { get; private set; }

        public string? WorkingDirectoryDuringRun { get; private set; }

        public Exception? Exception { get; init; }

        public Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken)
        {
            Args = args;
            StartupTimeout = startupTimeout;
            WorkingDirectoryDuringRun = Directory.GetCurrentDirectory();
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAppSurfaceDocsExportRunner : IAppSurfaceDocsExportRunner
    {
        public AppSurfaceDocsExportArgs? Args { get; private set; }

        public Exception? Exception { get; init; }

        public Task ExportAsync(AppSurfaceDocsExportArgs args, CancellationToken cancellationToken)
        {
            Args = args;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAppSurfaceDocsHealthVerifyRunner : IAppSurfaceDocsHealthVerifyRunner
    {
        public AppSurfaceDocsHealthVerifyArgs? Args { get; private set; }

        public AppSurfaceDocsHealthVerificationResult Result { get; init; } = CreateHealthVerifyResult(ok: true);

        public Exception? Exception { get; init; }

        public Task<AppSurfaceDocsHealthVerificationResult> VerifyAsync(
            AppSurfaceDocsHealthVerifyArgs args,
            CancellationToken cancellationToken)
        {
            Args = args;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class ImmediateHealthHostStarter(IHost host) : IAppSurfaceDocsHealthHostStarter
    {
        public string? EnvironmentName { get; private set; }

        public CancellationToken StartupToken { get; private set; }

        public string? WorkingDirectoryDuringStart { get; private set; }

        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsHealthVerifyArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            EnvironmentName = environmentName;
            StartupToken = cancellationToken;
            WorkingDirectoryDuringStart = Directory.GetCurrentDirectory();
            return Task.FromResult(host);
        }
    }

    private sealed class CancelingHealthHostStarter : IAppSurfaceDocsHealthHostStarter
    {
        private readonly TaskCompletionSource<IHost> _hostCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken StartupToken { get; private set; }

        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsHealthVerifyArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            StartupToken = cancellationToken;
            cancellationToken.Register(() => _hostCompletion.TrySetCanceled(cancellationToken));
            return _hostCompletion.Task;
        }
    }

    private sealed class ImmediatelyCanceledHealthHostStarter : IAppSurfaceDocsHealthHostStarter
    {
        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsHealthVerifyArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            return Task.FromException<IHost>(new OperationCanceledException(cancellationToken));
        }
    }

    private sealed class ControllableHealthHostStarter : IAppSurfaceDocsHealthHostStarter
    {
        private readonly TaskCompletionSource<IHost> _hostCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Finished => _finished.Task;

        public CancellationToken StartupToken { get; private set; }

        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsHealthVerifyArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            StartupToken = cancellationToken;
            _started.TrySetResult(null);
            return _hostCompletion.Task;
        }

        public void Complete(IHost host)
        {
            _hostCompletion.TrySetResult(host);
            _finished.TrySetResult(null);
        }

        public void Fail(Exception exception)
        {
            _hostCompletion.TrySetException(exception);
            _finished.TrySetResult(null);
        }
    }

    private sealed class CapturingHealthHttpClient(AppSurfaceDocsHealthHttpResponse response) : IAppSurfaceDocsHealthHttpClient
    {
        public string? Url { get; private set; }

        public Task<AppSurfaceDocsHealthHttpResponse> GetAsync(string url, CancellationToken cancellationToken)
        {
            Url = url;
            return Task.FromResult(response);
        }
    }

    private sealed class StaticHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response = new(statusCode)
        {
            Content = new StringContent(body)
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class AppSurfaceExportHttpMessageHandler : HttpMessageHandler
    {
        private const string RazorWireRuntimePath = "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js";

        private readonly Queue<HttpResponseMessage> _okResponses;
        private readonly Queue<HttpResponseMessage> _assetResponses;
        private readonly Queue<HttpResponseMessage> _notFoundResponses;
        private readonly List<HttpResponseMessage> _responses;

        public AppSurfaceExportHttpMessageHandler()
        {
            _responses = Enumerable.Range(0, 8)
                .Select(_ => CreateOkResponse())
                .Concat(Enumerable.Range(0, 8).Select(_ => CreateAssetResponse()))
                .Concat(Enumerable.Range(0, 8).Select(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
                .ToList();
            _okResponses = new Queue<HttpResponseMessage>(
                _responses.Where(response => response.Content?.Headers.ContentType?.MediaType == "text/html"));
            _assetResponses = new Queue<HttpResponseMessage>(
                _responses.Where(response => response.Content?.Headers.ContentType?.MediaType == "text/javascript"));
            _notFoundResponses = new Queue<HttpResponseMessage>(_responses.Where(response => response.StatusCode == HttpStatusCode.NotFound));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Task.FromResult(DequeueOrThrow(_okResponses, HttpStatusCode.OK, path));
            }

            if (path == RazorWireRuntimePath)
            {
                return Task.FromResult(DequeueOrThrow(_assetResponses, HttpStatusCode.OK, path));
            }

            return Task.FromResult(DequeueOrThrow(_notFoundResponses, HttpStatusCode.NotFound, path));
        }

        private static HttpResponseMessage DequeueOrThrow(
            Queue<HttpResponseMessage> responses,
            HttpStatusCode statusCode,
            string path)
        {
            if (responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No queued {statusCode} response available for request path '{path}'. Increase test handler capacity.");
            }

            return responses.Dequeue();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _responses)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private static HttpResponseMessage CreateOkResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <html>
                      <head>
                        <link rel="canonical" href="/profile">
                      </head>
                      <body>
                        <script src="/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"></script>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """,
                    System.Text.Encoding.UTF8,
                    "text/html")
            };
        }

        private static HttpResponseMessage CreateAssetResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "window.RazorWire = window.RazorWire || {};",
                    System.Text.Encoding.UTF8,
                    "text/javascript")
            };
        }
    }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CancelingStaticExporter : IRazorWireStaticExporter
    {
        public ExportContext? Context { get; private set; }

        public Task ExportAsync(ExportContext context, CancellationToken cancellationToken)
        {
            Context = context;
            throw new OperationCanceledException("Export canceled after startup.");
        }
    }

    private sealed class CapturingStaticExporter : IRazorWireStaticExporter
    {
        public ExportContext? Context { get; private set; }

        public string? WorkingDirectoryDuringExport { get; private set; }

        public Task ExportAsync(ExportContext context, CancellationToken cancellationToken)
        {
            Context = context;
            WorkingDirectoryDuringExport = Directory.GetCurrentDirectory();
            return Task.CompletedTask;
        }
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    private sealed class CanonicalInspectingStaticExporter(
        string route,
        string expectedCanonicalLink,
        string forbiddenCanonicalOrigin) : IRazorWireStaticExporter
    {
        public bool Inspected { get; private set; }

        public async Task ExportAsync(ExportContext context, CancellationToken cancellationToken)
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(context.BaseUrl)
            };

            var html = await client.GetStringAsync(route, cancellationToken);
            Assert.Contains(expectedCanonicalLink, html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"rel=\"canonical\" href=\"http://{forbiddenCanonicalOrigin}",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"rel=\"canonical\" href=\"https://{forbiddenCanonicalOrigin}",
                html,
                StringComparison.Ordinal);
            Inspected = true;
        }
    }

    private sealed class CapturingBrowserLauncher(AppSurfaceDocsBrowserLaunchResult? result = null) : IAppSurfaceDocsBrowserLauncher
    {
        private readonly AppSurfaceDocsBrowserLaunchResult _result = result ?? AppSurfaceDocsBrowserLaunchResult.Success;
        private readonly TaskCompletionSource<object?> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _opened.Task;

        public Uri? Url { get; private set; }

        public Task<AppSurfaceDocsBrowserLaunchResult> TryOpenAsync(Uri url, CancellationToken cancellationToken)
        {
            Url = url;
            _opened.TrySetResult(null);
            return Task.FromResult(_result);
        }
    }

    private sealed class CapturingBrowserOpenCommandRunner(Exception? exception = null) : IAppSurfaceDocsBrowserOpenCommandRunner
    {
        public Uri? Url { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task OpenAsync(Uri url, CancellationToken cancellationToken)
        {
            Url = url;
            CancellationToken = cancellationToken;

            return exception is null
                ? Task.CompletedTask
                : Task.FromException(exception);
        }
    }

    private sealed class CancelingBrowserOpenCommandRunner(CancellationTokenSource cancellationTokenSource) : IAppSurfaceDocsBrowserOpenCommandRunner
    {
        public Task OpenAsync(Uri url, CancellationToken cancellationToken)
        {
            cancellationTokenSource.Cancel();
            return Task.FromException(new OperationCanceledException(cancellationTokenSource.Token));
        }
    }

    private sealed class CapturingHarvestSummaryReader(AppSurfaceDocsHarvestSummary? summary) : IAppSurfaceDocsHarvestSummaryReader
    {
        private readonly TaskCompletionSource<object?> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Read => _read.Task;

        public Task<AppSurfaceDocsHarvestSummary?> ReadAsync(IHost host, CancellationToken cancellationToken)
        {
            _read.TrySetResult(null);
            return Task.FromResult(summary);
        }
    }

    private sealed class ThrowingHarvestSummaryReader(Exception exception) : IAppSurfaceDocsHarvestSummaryReader
    {
        private readonly TaskCompletionSource<object?> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Read => _read.Task;

        public Task<AppSurfaceDocsHarvestSummary?> ReadAsync(IHost host, CancellationToken cancellationToken)
        {
            _read.TrySetResult(null);
            return Task.FromException<AppSurfaceDocsHarvestSummary?>(exception);
        }
    }

    private sealed class StaticDocHarvester(IReadOnlyList<DocNode> docs) : IDocHarvester
    {
        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(docs);
        }
    }

    private sealed class PassthroughDocsHtmlSanitizer : IAppSurfaceDocsHtmlSanitizer
    {
        public string Sanitize(string html)
        {
            return html;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ForgeTrust.AppSurface.Cli.Tests";

        public string WebRootPath { get; set; } = contentRootPath;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }

    private sealed class CancelingHarvestSummaryReader(CancellationTokenSource cancellationTokenSource) : IAppSurfaceDocsHarvestSummaryReader
    {
        public Task<AppSurfaceDocsHarvestSummary?> ReadAsync(IHost host, CancellationToken cancellationToken)
        {
            cancellationTokenSource.Cancel();
            return Task.FromException<AppSurfaceDocsHarvestSummary?>(
                new OperationCanceledException(cancellationTokenSource.Token));
        }
    }

    private sealed class ImmediatePreviewHostStarter(IHost host) : IAppSurfaceDocsPreviewHostStarter
    {
        public AppSurfaceDocsPreviewHostArgs? Args { get; private set; }

        public CancellationToken StartupToken { get; private set; }

        public Task<IHost> BuildAndStartAsync(AppSurfaceDocsPreviewHostArgs args, CancellationToken cancellationToken)
        {
            Args = args;
            StartupToken = cancellationToken;
            return Task.FromResult(host);
        }
    }

    private sealed class CancelingPreviewHostStarter : IAppSurfaceDocsPreviewHostStarter
    {
        public Task<IHost> BuildAndStartAsync(AppSurfaceDocsPreviewHostArgs args, CancellationToken cancellationToken)
        {
            return Task.FromException<IHost>(new OperationCanceledException(cancellationToken));
        }
    }

    private sealed class ControllablePreviewHostStarter : IAppSurfaceDocsPreviewHostStarter
    {
        private readonly TaskCompletionSource<IHost> _hostCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public CancellationToken StartupToken { get; private set; }

        public Task<IHost> BuildAndStartAsync(AppSurfaceDocsPreviewHostArgs args, CancellationToken cancellationToken)
        {
            StartupToken = cancellationToken;
            _started.TrySetResult(null);
            return _hostCompletion.Task;
        }

        public void Complete(IHost host)
        {
            _hostCompletion.TrySetResult(host);
        }

        public void Fail(Exception exception)
        {
            _hostCompletion.TrySetException(exception);
        }
    }

    private sealed class ImmediateExportHostStarter(IHost host) : IAppSurfaceDocsExportHostStarter
    {
        public string? EnvironmentName { get; private set; }

        public CancellationToken StartupToken { get; private set; }

        public string? WorkingDirectoryDuringStart { get; private set; }

        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsExportArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            EnvironmentName = environmentName;
            StartupToken = cancellationToken;
            WorkingDirectoryDuringStart = Directory.GetCurrentDirectory();
            return Task.FromResult(host);
        }
    }

    private sealed class CancelingExportHostStarter : IAppSurfaceDocsExportHostStarter
    {
        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsExportArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            return Task.FromException<IHost>(new OperationCanceledException(cancellationToken));
        }
    }

    private sealed class ControllableExportHostStarter : IAppSurfaceDocsExportHostStarter
    {
        private readonly TaskCompletionSource<IHost> _hostCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public CancellationToken StartupToken { get; private set; }

        public Task<IHost> BuildAndStartAsync(
            AppSurfaceDocsExportArgs args,
            string environmentName,
            CancellationToken cancellationToken)
        {
            StartupToken = cancellationToken;
            _started.TrySetResult(null);
            return _hostCompletion.Task;
        }

        public void Complete(IHost host)
        {
            _hostCompletion.TrySetResult(host);
        }

        public void Fail(Exception exception)
        {
            _hostCompletion.TrySetException(exception);
        }
    }

    private sealed class TrackingHost : IHost
    {
        private readonly bool _throwOnStop;
        private readonly ServiceProvider _services;
        private readonly TaskCompletionSource<object?> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrackingHost(
            string boundAddress = "http://127.0.0.1:51234",
            bool throwOnStop = false,
            Action<IServiceCollection>? configureServices = null)
        {
            _throwOnStop = throwOnStop;
            Lifetime = new FakeHostApplicationLifetime();
            var services = new ServiceCollection()
                .AddSingleton<IServer>(new FakeServer(boundAddress))
                .AddSingleton<IHostApplicationLifetime>(Lifetime);
            configureServices?.Invoke(services);
            _services = services.BuildServiceProvider();
        }

        public IServiceProvider Services => _services;

        public FakeHostApplicationLifetime Lifetime { get; }

        public bool StopCalled { get; private set; }

        public bool DisposeCalled { get; private set; }

        public Task Disposed => _disposed.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalled = true;
            if (_throwOnStop)
            {
                throw new InvalidOperationException("Host stop failed.");
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            _services.Dispose();
            _disposed.TrySetResult(null);
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public FakeHostApplicationLifetime()
        {
            _started.Cancel();
        }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(string boundAddress)
        {
            var addresses = new ServerAddressesFeature();
            addresses.Addresses.Add(boundAddress);
            Features.Set<IServerAddressesFeature>(addresses);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(
            Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application,
            CancellationToken cancellationToken)
            where TContext : notnull
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public ILogger CreateLogger(string categoryName) => new InMemoryLogger(_messages);

        public void Dispose()
        {
        }

        public IReadOnlyList<string> GetMessages() => _messages.ToArray();

        private sealed class InMemoryLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class CapturingSecretsCommand : SecretsCommandBase
    {
        public IAppSurfaceLocalSecretStore Store { get; } = new CapturedLocalSecretStore();

        public AppSurfaceLocalSecretsOptions? PlatformOptions { get; private set; }

        public SecretsCommandContext BuildContextForTests() => BuildContext();

        public static ValueTask WriteResultForTestsAsync(
            IConsole console,
            AppSurfaceLocalSecretResult result,
            string successVerb) =>
            WriteResultAsync(console, result, successVerb);

        public override ValueTask ExecuteAsync(IConsole console) => ValueTask.CompletedTask;

        protected override IAppSurfaceLocalSecretStore CreatePlatformStore(AppSurfaceLocalSecretsOptions options)
        {
            PlatformOptions = options;
            return Store;
        }
    }

    private static AppSurfaceLocalSecretDiagnostic CreateLocalSecretDiagnostic(string code) =>
        new(
            code,
            "Local secret posture was inspected.",
            "The local secret namespace is safe to use.",
            "No action is required.",
            "local-secrets-without-a-remote-vault");

    private sealed class CapturedLocalSecretStore : IAppSurfaceLocalSecretStore
    {
        public string Name => nameof(CapturedLocalSecretStore);

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) =>
            throw new NotSupportedException("This test store only captures platform-store construction.");

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) =>
            throw new NotSupportedException("This test store only captures platform-store construction.");

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) =>
            throw new NotSupportedException("This test store only captures platform-store construction.");

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            throw new NotSupportedException("This test store only captures platform-store construction.");

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
            throw new NotSupportedException("This test store only captures platform-store construction.");
    }

    private sealed class DefaultPlatformSecretsCommand : SecretsCommandBase
    {
        public SecretsCommandContext BuildContextForTests() => BuildContext();

        public override ValueTask ExecuteAsync(IConsole console) => ValueTask.CompletedTask;
    }

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryLinkIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                new DirectoryInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }

            return new TempDirectory(path);
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
