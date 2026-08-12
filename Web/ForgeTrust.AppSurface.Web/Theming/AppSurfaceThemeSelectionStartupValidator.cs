using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Rejects a document-provider replacement that bypasses the selection adapter.</summary>
internal sealed class AppSurfaceThemeSelectionStartupValidator : IStartupFilter
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Initializes a validator over the host's final service provider.</summary>
    public AppSurfaceThemeSelectionStartupValidator(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <summary>Validates the resolved document-provider contract after host pipeline composition.</summary>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            next(app);
            Validate();
        };
    }

    /// <summary>Validates that no later registration replaced the opt-in selection provider.</summary>
    internal void Validate()
    {
        var registrationState = _serviceProvider.GetRequiredService<AppSurfaceThemeSelectionRegistrationState>();
        registrationState.ValidateNeutralServiceLifetimes();
        registrationState.ValidatePolicyLifetime();
        registrationState.ValidateDocumentProviderRegistration();

        using var scope = _serviceProvider.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAppSurfaceThemeDocumentProvider>();
        if (provider is AppSurfaceThemeSelectionDocumentProvider)
        {
            return;
        }

        throw new InvalidOperationException(
            "ASWEBTHEME006: AddAppSurfaceWebThemeSelection requires the built-in selection document provider. Remove the consumer-owned IAppSurfaceThemeDocumentProvider replacement or do not opt into selection.");
    }
}

/// <summary>Marks the browser-preference adapter so conflicting registrations fail predictably.</summary>
internal sealed class AppSurfaceThemePreferenceRegistrationMarker;

/// <summary>Marks the selection adapter so duplicate registration fails predictably.</summary>
internal sealed class AppSurfaceThemeSelectionRegistrationMarker;

/// <summary>Retains the mutable registration collection until startup can validate its final selection contract.</summary>
internal sealed class AppSurfaceThemeSelectionRegistrationState(IServiceCollection services)
{
    private readonly IServiceCollection _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Rejects a later policy registration that changes the effective service lifetime.</summary>
    internal void ValidatePolicyLifetime()
    {
        var policyDescriptor = _services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IAppSurfaceWebThemeSelectionPolicy));
        if (policyDescriptor is null || policyDescriptor.Lifetime != ServiceLifetime.Scoped)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME004: AddAppSurfaceWebThemeSelection requires the final IAppSurfaceWebThemeSelectionPolicy registration to be scoped.");
        }
    }

    /// <summary>Rejects a later neutral-service registration that violates the cache lifetime contract.</summary>
    internal void ValidateNeutralServiceLifetimes()
    {
        var registryDescriptor = _services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeRegistry));
        var resolverDescriptor = _services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeResolver));
        if (registryDescriptor is null
            || resolverDescriptor is null
            || registryDescriptor.Lifetime != ServiceLifetime.Singleton
            || resolverDescriptor.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME003: AddAppSurfaceWebThemeSelection requires singleton IAppSurfaceThemeResolver and IAppSurfaceThemeRegistry services to be registered first. AddAppSurfaceTheming is the supported registration path.");
        }
    }

    /// <summary>Rejects a later document-provider registration that violates the scoped selection contract.</summary>
    internal void ValidateDocumentProviderRegistration()
    {
        var descriptor = _services.LastOrDefault(
            item => item.ServiceType == typeof(IAppSurfaceThemeDocumentProvider));
        if (descriptor?.Lifetime != ServiceLifetime.Scoped
            || descriptor.ImplementationType != typeof(AppSurfaceThemeSelectionDocumentProvider))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME006: AddAppSurfaceWebThemeSelection requires the built-in selection document provider. Remove the consumer-owned IAppSurfaceThemeDocumentProvider replacement or do not opt into selection.");
        }
    }
}
