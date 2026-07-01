using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Registers the AppSurface Google Secret Manager configuration provider.
/// </summary>
public sealed class AppSurfaceGoogleSecretManagerModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceGoogleSecretManagerOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AppSurfaceGoogleSecretManagerOptions>, AppSurfaceGoogleSecretManagerOptionsValidator>());
        services.TryAddSingleton<IAppSurfaceGoogleSecretManagerClient, GoogleSecretManagerClientAdapter>();
        services.AddSingleton<GoogleSecretManagerConfigProvider>();
        services.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<GoogleSecretManagerConfigProvider>());
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
    }
}
