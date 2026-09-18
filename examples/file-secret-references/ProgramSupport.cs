using System.Text;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Core;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileSecretReferencesExample;

[ConfigKey("FileSecretReferences")]
public sealed class FileSecretReferencesConfig : Config<FileSecretReferencesOptions>
{
    /// <summary>Provides a harmless fallback for model construction; the example file supplies the real root.</summary>
    public override FileSecretReferencesOptions? DefaultValue => new() { Endpoint = "https://default.invalid", ApiKey = new() };
}

/// <summary>Configuration model used by the file declared secret reference walkthrough.</summary>
public sealed class FileSecretReferencesOptions
{
    /// <summary>Non-secret service endpoint carried by the same file root.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Typed scalar destination populated by the declared reference, environment, or lower source.</summary>
    public required Secret<string> ApiKey { get; init; }
}

/// <summary>Registers the real AppSurface Config and Google modules for the network-free walkthrough.</summary>
public sealed class FileSecretReferencesModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.ConfigureAppSurfaceGoogleSecretManager(options =>
        {
            options.ProjectId = "demo-project";
            options.LookupTimeout = TimeSpan.FromSeconds(1);
        });
        var demoClient = new DemoGoogleSecretClient();
        services.AddSingleton(demoClient);
        services.UseAppSurfaceGoogleSecretManagerClient(demoClient);
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
        builder.AddModule<AppSurfaceGoogleSecretManagerModule>();
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }

    public static bool IsExpectedFailure(string[] args, ConfigurationCompositionException exception) =>
        args.Any(argument => string.Equals(argument, "failure", StringComparison.OrdinalIgnoreCase))
        && exception.Failures.Any(failure => failure.Code == "secret-not-found");
}

/// <summary>Uses the standard AppSurface host builder without command or hosted-service behavior.</summary>
public sealed class FileSecretReferencesStartup : AppSurfaceStartup<FileSecretReferencesModule>
{
    protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
    {
    }
}

internal static class DemoOutput
{
    public static void Verify(FileSecretReferencesConfig config, DemoGoogleSecretClient client)
    {
        var mode = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var environmentKey = Environment.GetEnvironmentVariable("FILESECRETREFERENCES__APIKEY");
        var hasValue = config.Value?.ApiKey.HasValue == true;
        var provider = config.Value?.ApiKey.ResolvedProvider ?? "none";

        if (mode.Equals("Production", StringComparison.OrdinalIgnoreCase) && environmentKey is null)
        {
            Assert(!hasValue, "disabled declaration remains empty");
            Assert(client.CallCount == 0, "disabled declaration performs no Google read");
        }
        else if (environmentKey is not null)
        {
            Assert(config.Value!.ApiKey.HasValue, "environment rescue has a value");
            Assert(config.Value!.ApiKey.TryGetValue(out var value) && value == environmentKey, "exact environment rescue wins");
            Assert(provider == "EnvironmentConfigProvider", "environment is the effective source");
            Assert(client.CallCount == 0, "environment rescue avoids Google");
        }
        else
        {
            Assert(config.Value!.ApiKey.TryGetValue(out var value) && value == "fake-google-value", "Google payload binds to Secret<string>");
            Assert(provider == GoogleSecretManagerConfigProvider.ProviderId, "canonical Google provider id is reported");
            Assert(client.CallCount == 1, "enabled reference reads Google exactly once");
        }

        Console.WriteLine($"PASS mode={mode} hasValue={hasValue} provider={provider} googleCalls={client.CallCount}");
    }

    private static void Assert(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {assertion}");
        Console.WriteLine($"PASS assertion={assertion}");
    }
}

/// <summary>Network-free Google client used by the executable proof.</summary>
public sealed class DemoGoogleSecretClient : IAppSurfaceGoogleSecretManagerClient
{
    /// <summary>Number of fake Google reads attempted by this process.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
    {
        CallCount++;
        if (resourceName.Contains("missing-google-key", StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.NotFound, "demo missing"));

        if (resourceName != "projects/demo-project/secrets/demo-google-key/versions/4")
            throw new InvalidOperationException($"Unexpected demo resource path: {resourceName}");

        return new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("fake-google-value"), resourceName);
    }
}
