using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public class CommandDescriptorResolverTests
{
    [Fact]
    public void GetRequiredDescriptor_WithGeneratedCommand_ReturnsDescriptor()
    {
        var descriptor = CommandDescriptorResolver.GetRequiredDescriptor(typeof(FirstCommand));

        var option = Assert.IsAssignableFrom<CommandOptionDescriptor>(
            Assert.Single(descriptor.Inputs.OfType<CommandOptionDescriptor>(), input => input.Name == "foo"));

        Assert.Equal(typeof(FirstCommand), descriptor.Type);
        Assert.Equal("first", descriptor.Name);
        Assert.Equal("foo", option.Name);
        Assert.True(option.IsRequired);
    }

    [Fact]
    public void GetRequiredDescriptor_WithoutGeneratedDescriptor_ThrowsActionableMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CommandDescriptorResolver.GetRequiredDescriptor(typeof(MissingDescriptorCommand)));

        Assert.Contains(nameof(MissingDescriptorCommand), exception.Message);
        Assert.Contains("partial", exception.Message);
        Assert.Contains("[Command]", exception.Message);
    }

    [Fact]
    public void GetRequiredDescriptor_ConsoleExampleConfigDiagnosticsCommand_HasDebugOption()
    {
        var descriptor = CommandDescriptorResolver.GetRequiredDescriptor(typeof(ConsoleAppExample.ConfigDiagnosticsCommand));

        Assert.Equal("config diagnostics", descriptor.Name);
        Assert.Single(descriptor.Inputs.OfType<CommandOptionDescriptor>(), input => input.Name == "debug");
    }

    [Fact]
    public void GetRequiredDescriptor_LocalSecretsConfigDiagnosticsCommand_HasDebugOption()
    {
        var descriptor = CommandDescriptorResolver.GetRequiredDescriptor(typeof(LocalSecretsExample.ConfigDiagnosticsCommand));

        Assert.Equal("config diagnostics", descriptor.Name);
        Assert.Single(descriptor.Inputs.OfType<CommandOptionDescriptor>(), input => input.Name == "debug");
    }

    [Fact]
    public void TryGetDescriptor_WithoutGeneratedDescriptor_ReturnsNull()
    {
        Assert.Null(CommandDescriptorResolver.TryGetDescriptor(typeof(MissingDescriptorCommand)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ConsoleExampleConfigDiagnosticsCommand_DispatchesAndSucceeds(bool debug)
    {
        var result = await RunConfigDiagnosticsAsync(debug, localSecrets: false, fail: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Environment: Test", result.Output, StringComparison.Ordinal);
        Assert.Equal(debug, result.Output.Contains("Mode: ExpandKnownEntryCollections", StringComparison.Ordinal));
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_LocalSecretsConfigDiagnosticsCommand_DispatchesAndSucceeds(bool debug)
    {
        var result = await RunConfigDiagnosticsAsync(debug, localSecrets: true, fail: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Environment: Test", result.Output, StringComparison.Ordinal);
        Assert.Equal(debug, result.Output.Contains("Mode: ExpandKnownEntryCollections", StringComparison.Ordinal));
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ConsoleExampleConfigDiagnosticsCommand_ReportsRunnerFailure(bool debug)
    {
        var result = await RunConfigDiagnosticsAsync(debug, localSecrets: false, fail: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Configuration diagnostics could not render", result.Error, StringComparison.Ordinal);
        Assert.Contains("Exception type: InvalidOperationException", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_LocalSecretsConfigDiagnosticsCommand_ReportsRunnerFailure(bool debug)
    {
        var result = await RunConfigDiagnosticsAsync(debug, localSecrets: true, fail: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Configuration diagnostics could not render", result.Error, StringComparison.Ordinal);
        Assert.Contains("Exception type: InvalidOperationException", result.Error, StringComparison.Ordinal);
    }

    private static async Task<CommandRun> RunConfigDiagnosticsAsync(bool debug, bool localSecrets, bool fail)
    {
        using var console = new FakeInMemoryConsole();
        using var provider = CreateProvider(localSecrets, fail, console);
        CommandService.PrimaryServiceProvider = provider;

        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var command = localSecrets
                ? (ICommand)provider.GetRequiredService<LocalSecretsExample.ConfigDiagnosticsCommand>()
                : provider.GetRequiredService<ConsoleAppExample.ConfigDiagnosticsCommand>();
            var context = new StartupContext(
                debug ? ["config", "diagnostics", "--debug"] : ["config", "diagnostics"],
                new TestModule());
            var service = new CommandService([command], context, new LevenshteinOptionSuggester());

            await service.RunInternalAsync(CancellationToken.None);

            return new CommandRun(
                Environment.ExitCode,
                console.ReadOutputString(),
                console.ReadErrorString());
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            CommandService.PrimaryServiceProvider = null;
        }
    }

    private static ServiceProvider CreateProvider(bool localSecrets, bool fail, IConsole console)
    {
        var services = new ServiceCollection();
        services.AddSingleton(console);
        services.AddSingleton<IConfigAuditReporter>(new TestReporter(fail));
        services.AddSingleton<IEnvironmentProvider>(new TestEnvironmentProvider());
        services.AddSingleton<ConfigAuditTextRenderer>();
        services.AddSingleton<ConfigDiagnosticsCommandRunner>();
        if (localSecrets)
        {
            services.AddTransient<LocalSecretsExample.ConfigDiagnosticsCommand>();
        }
        else
        {
            services.AddTransient<ConsoleAppExample.ConfigDiagnosticsCommand>();
        }

        return services.BuildServiceProvider();
    }

    private sealed record CommandRun(int ExitCode, string Output, string Error);

    private sealed class TestReporter(bool fail) : IConfigAuditReporter
    {
        public ConfigAuditReport GetReport(string environment)
        {
            if (fail)
            {
                throw new InvalidOperationException("test failure");
            }

            return CreateReport(environment);
        }

        public ConfigAuditReport GetReport(ConfigAuditReportRequest request)
        {
            if (fail)
            {
                throw new InvalidOperationException("test failure");
            }

            return CreateReport(request.Environment, request.Mode);
        }
    }

    private sealed class TestEnvironmentProvider : IEnvironmentProvider
    {
        public string Environment => "Test";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }

    private sealed class TestModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private static ConfigAuditReport CreateReport(
        string environment,
        ConfigAuditReportMode? mode = null) =>
        new()
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Mode = mode == ConfigAuditReportMode.Default ? null : mode,
            Redaction = new ConfigAuditRedaction
            {
                Enabled = true,
                Placeholder = "[redacted]"
            }
        };

    private sealed class MissingDescriptorCommand
    {
    }
}
