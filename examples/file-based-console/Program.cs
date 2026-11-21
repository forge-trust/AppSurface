// .NET SDK 10.0.402 file-based app. Run with: dotnet run --file Program.cs -- --help
// Package versions are intentionally pinned for this package-consumer sample.
#:property PublishAot=false
#:property PublishTrimmed=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:package ForgeTrust.AppSurface.Console@0.2.0-preview.14
#:package ForgeTrust.AppSurface.Config@0.2.0-preview.14
#:package Microsoft.Extensions.Hosting@10.0.14

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

await ConsoleApp<DemoModule>.RunAsync(args, options => options.OutputMode = ConsoleOutputMode.CommandFirst);

[Command("inspect", Description = "Resolve and validate package-sample settings.")]
public sealed partial class InspectCommand(IConfigManager configManager) : ICommand
{
    [CommandOption("port", Description = "Port number (Config key Demo:Port, default 5055).")]
    [ConfigKey("Demo:Port")]
    public int? Port { get; set; }

    [CommandOption("region", Description = "Deployment region (Config key Demo:Region).")]
    [ConfigKey("Demo:Region")]
    public string? Region { get; set; }

    public ValueTask ExecuteAsync(IConsole console)
    {
        var settings = ReadSettings(configManager, ReadConfigKey(nameof(Port)), Port, ReadConfigKey(nameof(Region)), Region);
        if (settings.ApiKey is { HasValue: true } apiKey && settings.Port is >= 1 and <= 65535)
        {
            console.Output.WriteLine($"Settings validated. Effective port: {settings.Port}. Effective region: {settings.Region}. Synthetic typed secret matched in memory.");
        }

        return ValueTask.CompletedTask;
    }

    private static DemoSettings ReadSettings(IConfigManager manager, string portKey, int? explicitPort, string regionKey, string? explicitRegion)
    {
        var port = explicitPort ?? manager.GetValue<int?>("Production", portKey) ?? 5055;
        var region = explicitRegion ?? manager.GetValue<string>("Production", regionKey);
        var evidencePath = manager.GetValue<string>("Production", "Demo:EvidencePath");
        var endpoint = manager.GetValue<string>("Production", "Demo:Endpoint");
        var secretSettings = manager.GetValue<DemoSecretSettings>("Production", "Demo:Credentials")
            ?? throw new ValidationException("Config key Demo:Credentials must resolve a typed secret settings object.");
        var settings = new DemoSettings(port, region, endpoint, evidencePath, secretSettings.ApiKey);
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        if (settings.ApiKey is not { HasValue: true } apiKey
            || !string.Equals(apiKey.Value, SyntheticCredential.Value, StringComparison.Ordinal))
        {
            throw new ValidationException("Config key Demo:Credentials:ApiKey must resolve to the configured synthetic test secret.");
        }

        return settings;
    }

    private static string ReadConfigKey(string propertyName) =>
        typeof(InspectCommand).GetProperty(propertyName)?.GetCustomAttributes(typeof(ConfigKeyAttribute), inherit: true)
            .Cast<ConfigKeyAttribute>().SingleOrDefault()?.Key
        ?? throw new InvalidOperationException($"The {propertyName} switch is missing its Config key metadata.");
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConfigKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

internal sealed record DemoSettings(
    [property: Range(1, 65535)] int Port,
    [property: Required] string? Region,
    [property: Required, Url] string? Endpoint,
    [property: Required] string? EvidencePath,
    [property: Required] Secret<string>? ApiKey);

internal sealed class DemoSecretSettings
{
    [Required]
    public required Secret<string> ApiKey { get; init; }
}

internal static class SyntheticCredential
{
    public const string Value = "synthetic-file-based-proof-only";
}

internal sealed class DemoModule : IAppSurfaceHostModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
    }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<IConfigSecretProvider, SyntheticSecretProvider>();
        services.AddSingleton<IConfigFileLocationProvider, SampleConfigFileLocationProvider>();
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
}

internal sealed class SampleConfigFileLocationProvider : IConfigFileLocationProvider
{
    public string Directory { get; } = AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("The .NET file-based app source directory is unavailable.");
}

internal sealed class SyntheticSecretProvider : IConfigSecretProvider
{
    public string Id => "file-based-sample";

    public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) =>
        reference.Key == "synthetic" && reference.Version == "1"
            ? ConfigSecretReferenceValidation.Supported()
            : ConfigSecretReferenceValidation.Unclaimed();

    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>
        ConfigSecretProviderResolution.Resolved(SyntheticCredential.Value, ConfigSecretSourceMetadata.Create(Id));
}
