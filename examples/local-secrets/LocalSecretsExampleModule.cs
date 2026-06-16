using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalSecretsExample;

public sealed class LocalSecretsExampleModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.ConfigureAppSurfaceLocalSecrets(options =>
        {
            options.ApplicationName = "LocalSecretsExample";
            options.Posture = LocalSecretsPostureMode.DevelopmentOnly;
        });

        var fileStore = Environment.GetEnvironmentVariable("APPSURFACE_LOCAL_SECRETS_FILE");
        if (!string.IsNullOrWhiteSpace(fileStore))
        {
            services.UseAppSurfaceLocalSecretStore(new FileAppSurfaceLocalSecretStore(fileStore));
        }
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
        builder.AddModule<AppSurfaceLocalSecretsModule>();
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
