using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Theming;

/// <summary>Registers immutable AppSurface theme-pair services for a host.</summary>
public static class AppSurfaceThemeServiceCollectionExtensions
{
    /// <summary>Validates and registers configured semantic pairs and their default resolver.</summary>
    /// <param name="services">Service collection receiving the sealed registry.</param>
    /// <param name="configure">Host configuration for pairs and the default mode.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null"/>.</exception>
    /// <exception cref="AppSurfaceThemeValidationException">Thrown when configuration cannot safely produce a complete pair.</exception>
    public static IServiceCollection AddAppSurfaceTheming(
        this IServiceCollection services,
        Action<AppSurfaceThemeRegistryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AppSurfaceThemeRegistryOptions();
        configure(options);
        return AddAppSurfaceTheming(services, options);
    }

    /// <summary>Validates and registers an explicit semantic pair configuration snapshot.</summary>
    /// <param name="services">Service collection receiving the sealed registry.</param>
    /// <param name="options">Host configuration for pairs and the default mode.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null"/>.</exception>
    /// <exception cref="AppSurfaceThemeValidationException">Thrown when configuration cannot safely produce a complete pair.</exception>
    public static IServiceCollection AddAppSurfaceTheming(
        this IServiceCollection services,
        AppSurfaceThemeRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var registry = new AppSurfaceThemeRegistry(options);
        services.AddSingleton<AppSurfaceThemeRegistry>(
            serviceProvider =>
            {
                foreach (var validator in serviceProvider.GetServices<IAppSurfaceThemeExtensionValidator>())
                {
                    validator.Validate(registry);
                }

                return registry;
            });
        services.AddSingleton<IAppSurfaceThemeRegistry>(
            serviceProvider => serviceProvider.GetRequiredService<AppSurfaceThemeRegistry>());
        services.AddSingleton<IAppSurfaceThemeResolver>(
            serviceProvider => serviceProvider.GetRequiredService<AppSurfaceThemeRegistry>());
        return services;
    }

    /// <summary>
    /// Requires an application-owned extension provider to cover every configured semantic pair.
    /// </summary>
    /// <typeparam name="TSettings">Application-owned non-null settings type.</typeparam>
    /// <param name="services">Service collection receiving the required-provider validator.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="AppSurfaceThemeValidationException">
    /// Thrown when <see cref="IAppSurfaceThemeRegistry"/> is first resolved and the provider is missing (ASTHEME201)
    /// or supplies no non-null setting for a registered pair (ASTHEME202). This method itself does not throw it.
    /// </exception>
    /// <remarks>
    /// Register <see cref="IAppSurfaceThemeExtensionProvider{TSettings}"/> before the neutral registry is first
    /// resolved. The provider remains application-owned: this method verifies only provider presence and one
    /// non-null setting for each registered pair. It neither validates, serializes, nor logs <typeparamref name="TSettings"/>.
    /// </remarks>
    public static IServiceCollection AddRequiredThemeExtension<TSettings>(this IServiceCollection services)
        where TSettings : notnull
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAppSurfaceThemeExtensionValidator, RequiredThemeExtensionValidator<TSettings>>();
        return services;
    }

    private interface IAppSurfaceThemeExtensionValidator
    {
        void Validate(IAppSurfaceThemeRegistry registry);
    }

    private sealed class RequiredThemeExtensionValidator<TSettings>(IServiceProvider serviceProvider) : IAppSurfaceThemeExtensionValidator
        where TSettings : notnull
    {
        public void Validate(IAppSurfaceThemeRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);

            var provider = serviceProvider.GetService<IAppSurfaceThemeExtensionProvider<TSettings>>();
            if (provider is null)
            {
                throw new AppSurfaceThemeValidationException(
                [
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME201",
                        "A required theme extension provider is not registered.",
                        $"No IAppSurfaceThemeExtensionProvider<{typeof(TSettings).Name}> is registered.",
                        "Register an application-owned provider before the theme registry is resolved.")
                ]);
            }

            foreach (var themeId in registry.ThemeIds)
            {
                if (provider.TryGet(themeId, out var settings) && settings is not null)
                {
                    continue;
                }

                throw new AppSurfaceThemeValidationException(
                [
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME202",
                        "A required theme extension setting is missing.",
                        $"IAppSurfaceThemeExtensionProvider<{typeof(TSettings).Name}> did not provide non-null settings for pair '{themeId}'.",
                        "Provide one non-null application-owned setting for every registered pair, or do not opt into AddRequiredThemeExtension.")
                ]);
            }
        }
    }
}
