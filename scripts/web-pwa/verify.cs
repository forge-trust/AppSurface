#!/usr/bin/env dotnet
// Run from any working directory with the repository's pinned SDK:
// dotnet run --file scripts/web-pwa/verify.cs -- --help
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:package CliFx
#:project ../../Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
#:project ../../Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj
#:include ../../Cli/ForgeTrust.AppSurface.Cli/PwaVerificationV3Report.cs
#:include PwaVerifier.cs

using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

await ConsoleApp<PwaVerifyModule>.RunAsync(args, options => options.OutputMode = ConsoleOutputMode.CommandFirst);

internal sealed class PwaVerifyModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("PwaVerify:Port", "APP_SURFACE_WEB_PWA_PORT"));
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("PwaVerify:EvidencePath", "APP_SURFACE_WEB_PWA_EVIDENCE"));
        context.CustomRegistrations.Add(services => services.Replace(ServiceDescriptor.Singleton<IConfigFileLocationProvider>(
            new ScriptConfigFileLocationProvider(PwaVerifyPaths.ScriptDirectory))));
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder) =>
        builder.AddModule<AppSurfaceConfigModule>();

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
}

internal sealed class ScriptConfigFileLocationProvider(string directory) : IConfigFileLocationProvider
{
    public string Directory { get; } = Path.GetFullPath(directory);
}

[Command(Description = "Verify the example web PWA and write schema-v3 evidence.")]
internal sealed partial class VerifyCommand(IConfigManager configManager, IEnvironmentProvider environmentProvider) : ICommand
{
    [CommandOption("port", Description = "Port for the child host. Config key PwaVerify:Port, environment APP_SURFACE_WEB_PWA_PORT, default 5055.")]
    [OptionConfigKey("PwaVerify:Port")]
    public int? Port { get; set; }

    [CommandOption("evidence-path", Description = "Evidence file. Config key PwaVerify:EvidencePath, environment APP_SURFACE_WEB_PWA_EVIDENCE, default examples/web-pwa-install/pwa-verify-v3.json.")]
    [OptionConfigKey("PwaVerify:EvidencePath")]
    public string? EvidencePath { get; set; }

    [CommandOption("startup-timeout-seconds", Description = "Startup deadline in seconds. Config key PwaVerify:StartupTimeoutSeconds, environment PWAVERIFY__STARTUPTIMEOUTSECONDS, default 60.")]
    [OptionConfigKey("PwaVerify:StartupTimeoutSeconds")]
    public int? StartupTimeoutSeconds { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        PwaVerifySettings settings;
        ResolvedSetting<int> port;
        ResolvedSetting<string> evidence;
        ResolvedSetting<int> startupTimeout;
        try
        {
            port = ResolveIntegerOption(nameof(Port), Port, 5055, "--port");
            evidence = ResolveStringOption(
                nameof(EvidencePath),
                EvidencePath,
                Path.Join(PwaVerifyPaths.RepositoryRoot, "examples", "web-pwa-install", "pwa-verify-v3.json"),
                "--evidence-path");
            startupTimeout = ResolveIntegerOption(nameof(StartupTimeoutSeconds), StartupTimeoutSeconds, 60, "--startup-timeout-seconds");
            settings = new PwaVerifySettings(port.Value, evidence.Value, startupTimeout.Value);
        }
        catch (ConfigurationResolutionException exception)
        {
            await console.Error.WriteLineAsync(
                $"PWA_VERIFY_CONFIG: Configuration key '{exception.Key}' could not be resolved. Source: AppSurface.Config provider '{exception.ProviderName}'. {exception.Diagnostic.ToDisplayString()}");
            await WriteStaleEvidenceWarningAsync(console);
            Environment.ExitCode = 2;
            return;
        }

        var validation = new List<ValidationResult>();
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), validation, validateAllProperties: true))
        {
            foreach (var error in validation)
            {
                var source = error.MemberNames.FirstOrDefault() switch
                {
                    nameof(PwaVerifySettings.Port) => port.Source,
                    nameof(PwaVerifySettings.EvidencePath) => evidence.Source,
                    nameof(PwaVerifySettings.StartupTimeoutSeconds) => startupTimeout.Source,
                    _ => "resolved settings"
                };
                await console.Error.WriteLineAsync($"PWA_VERIFY_INVALID_SETTINGS: {error.ErrorMessage} Source: {source}");
            }

            await WriteStaleEvidenceWarningAsync(console);
            Environment.ExitCode = 2;
            return;
        }

        string evidencePath;
        try
        {
            evidencePath = PrepareEvidenceDestination(settings.EvidencePath);
        }
        catch (EvidenceDestinationException exception)
        {
            await console.Error.WriteLineAsync(
                $"PWA_VERIFY_EVIDENCE_PATH: {exception.Message} Source: {evidence.Source}. Fix: choose a valid file path in a writable directory.");
            await WriteStaleEvidenceWarningAsync(console);
            Environment.ExitCode = 2;
            return;
        }

        var exitCode = await PwaVerifier.RunAsync(
            PwaVerifyPaths.RepositoryRoot,
            settings.Port,
            evidencePath,
            TimeSpan.FromSeconds(settings.StartupTimeoutSeconds),
            cancellationToken);
        Environment.ExitCode = exitCode;
    }

    private ResolvedSetting<int> ResolveIntegerOption(string propertyName, int? explicitValue, int fallback, string optionName)
    {
        if (explicitValue is int value)
        {
            return new ResolvedSetting<int>(value, $"explicit {optionName} option");
        }

        var key = GetConfigKey(propertyName);
        int? configured = configManager.GetValue<int?>(environmentProvider.Environment, key);
        return configured is int configuredValue
            ? new ResolvedSetting<int>(configuredValue, $"AppSurface.Config key '{key}'")
            : new ResolvedSetting<int>(fallback, $"built-in default for '{key}'");
    }

    private ResolvedSetting<string> ResolveStringOption(string propertyName, string? explicitValue, string fallback, string optionName)
    {
        if (explicitValue is not null)
        {
            return new ResolvedSetting<string>(explicitValue, $"explicit {optionName} option");
        }

        var key = GetConfigKey(propertyName);
        var configured = configManager.GetValue<string?>(environmentProvider.Environment, key);
        return configured is not null
            ? new ResolvedSetting<string>(configured, $"AppSurface.Config key '{key}'")
            : new ResolvedSetting<string>(fallback, $"built-in default for '{key}'");
    }

    private static async ValueTask WriteStaleEvidenceWarningAsync(IConsole console) =>
        await console.Error.WriteLineAsync(
            "PWA_VERIFY_STALE_EVIDENCE: This run did not publish evidence. Any existing report may be from a previous run and should be treated as stale.");

    private static string PrepareEvidenceDestination(string configuredPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileName(configuredPath)))
            {
                throw new EvidenceDestinationException("The evidence destination must include a file name.");
            }

            var fullPath = Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
            if (Directory.Exists(fullPath))
            {
                throw new EvidenceDestinationException("The evidence destination is an existing directory.");
            }

            return fullPath;
        }
        catch (EvidenceDestinationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EvidenceDestinationException("The evidence destination is not a valid file path.");
        }
    }

    private sealed record ResolvedSetting<T>(T Value, string Source);

    private sealed class EvidenceDestinationException(string message) : Exception(message);

    private static AppSurfaceConfigKey GetConfigKey(string propertyName)
    {
        var property = typeof(VerifyCommand).GetProperty(propertyName)!;
        var key = property.GetCustomAttributes(typeof(OptionConfigKeyAttribute), inherit: false)
            .Cast<OptionConfigKeyAttribute>().Single().Key;
        return AppSurfaceConfigKey.Parse(key);
    }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class OptionConfigKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

internal static class PwaVerifyPaths
{
    public static string ScriptPath { get; } = ResolveScriptPath();
    public static string ScriptDirectory { get; } = Path.GetDirectoryName(ScriptPath)!;
    public static string RepositoryRoot { get; } = Path.GetFullPath("../..", ScriptDirectory);

    private static string ResolveScriptPath()
    {
        var sourcePath = GetSourcePath();
        var entryPointDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string;
        if (!string.IsNullOrWhiteSpace(entryPointDirectory))
        {
            var entryPointSourcePath = Path.Join(entryPointDirectory, Path.GetFileName(sourcePath));
            if (File.Exists(entryPointSourcePath))
            {
                sourcePath = entryPointSourcePath;
            }
        }

        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException("PWA_VERIFY_SOURCE_PATH: Could not locate the file-based script source.");
        }

        return Path.GetFullPath(sourcePath);
    }

    private static string GetSourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}

internal sealed class PwaVerifySettings : IValidatableObject
{
    public PwaVerifySettings(int port, string evidencePath, int startupTimeoutSeconds)
    {
        Port = port;
        EvidencePath = evidencePath;
        StartupTimeoutSeconds = startupTimeoutSeconds;
    }

    [Range(1, 65535, ErrorMessage = "--port / PwaVerify:Port must be between 1 and 65535. Choose a free TCP port.")]
    public int Port { get; }

    [Required(ErrorMessage = "--evidence-path / PwaVerify:EvidencePath must name a file.")]
    public string EvidencePath { get; }

    [Range(1, int.MaxValue, ErrorMessage = "--startup-timeout-seconds / PwaVerify:StartupTimeoutSeconds must be a positive number of seconds.")]
    public int StartupTimeoutSeconds { get; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(EvidencePath))
        {
            yield return new ValidationResult(
                "--evidence-path / PwaVerify:EvidencePath must name a file. Choose a destination with a file name.",
                [nameof(EvidencePath)]);
        }
    }
}
