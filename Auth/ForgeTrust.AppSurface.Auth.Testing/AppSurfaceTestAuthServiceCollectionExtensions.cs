using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Registers AppSurface test authentication services for ASP.NET Core integration tests.
/// </summary>
public static class AppSurfaceTestAuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds a test-only authentication scheme and immutable persona registry for AppSurface auth tests.
    /// </summary>
    /// <param name="services">Service collection that receives the test harness.</param>
    /// <param name="configure">Optional test auth options callback.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// This method composes <c>ForgeTrust.AppSurface.Auth.AspNetCore</c> so normal ASP.NET Core policy evaluation and
    /// AppSurface result mapping still run. It does not create production users, identity providers, cookies, tokens,
    /// or session freshness checks.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceTestAuth(
        this IServiceCollection services,
        Action<AppSurfaceTestAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuredOptions = new AppSurfaceTestAuthOptions();
        configure?.Invoke(configuredOptions);
        ValidateOptions(configuredOptions);

        if (string.IsNullOrWhiteSpace(configuredOptions.SubjectClaimType))
        {
            services.AddAppSurfaceAspNetCoreAuth();
        }
        else
        {
            services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(configuredOptions.SubjectClaimType));
        }

        services.AddSingleton(configuredOptions);
        services.AddSingleton(AppSurfaceTestPersonaRegistry.Create(configuredOptions));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, AppSurfaceTestAuthStartupFilter>());
        services.DecorateAppSurfaceTestPolicyEvaluator();

        if (configuredOptions.SchemeMode == AppSurfaceTestAuthSchemeMode.NoDefault)
        {
            services.AddAuthentication();
            return services;
        }

        var authenticationBuilder = configuredOptions.SchemeMode == AppSurfaceTestAuthSchemeMode.DefaultScheme
            ? services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = configuredOptions.SchemeName;
                options.DefaultChallengeScheme = configuredOptions.SchemeName;
                options.DefaultForbidScheme = configuredOptions.SchemeName;
            })
            : services.AddAuthentication();

        authenticationBuilder.AddScheme<AuthenticationSchemeOptions, AppSurfaceTestAuthenticationHandler>(
            configuredOptions.SchemeName,
            options => { _ = options; });

        return services;
    }

    private static void ValidateOptions(AppSurfaceTestAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SchemeName))
        {
            throw new InvalidOperationException(
                $"Problem: AppSurface test auth scheme name is blank. Cause: AppSurfaceTestAuthOptions.SchemeName was empty. Fix: set a non-blank scheme name. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.BlankSchemeName}.");
        }

        if (options.SubjectClaimType is not null && string.IsNullOrWhiteSpace(options.SubjectClaimType))
        {
            throw new InvalidOperationException(
                $"Problem: AppSurface test auth subject claim type is blank. Cause: AppSurfaceTestAuthOptions.SubjectClaimType was empty. Fix: set a non-blank subject claim type or leave it null to preserve host mapping. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.BlankPersonaName}.");
        }

        _ = AppSurfaceTestPersonaRegistry.Create(options);
    }

    private static void DecorateAppSurfaceTestPolicyEvaluator(this IServiceCollection services)
    {
        var descriptorIndex = FindLastPolicyEvaluator(services);
        if (descriptorIndex < 0)
        {
            throw new InvalidOperationException(
                "Problem: AppSurface ASP.NET Core policy evaluator was not registered. Cause: AddAppSurfaceTestAuth could not find the evaluator added by AddAppSurfaceAspNetCoreAuth. Fix: register IAppSurfaceAspNetCorePolicyEvaluator before AddAppSurfaceTestAuth or call AddAppSurfaceAspNetCoreAuth successfully.");
        }

        var descriptor = services[descriptorIndex];
        services.RemoveAt(descriptorIndex);
        services.Add(ServiceDescriptor.Describe(
            typeof(AppSurfaceTestInnerPolicyEvaluator),
            serviceProvider => new AppSurfaceTestInnerPolicyEvaluator(CreateInnerPolicyEvaluator(serviceProvider, descriptor)),
            descriptor.Lifetime));
        services.Add(ServiceDescriptor.Describe(
            typeof(IAppSurfaceAspNetCorePolicyEvaluator),
            serviceProvider => ActivatorUtilities.CreateInstance<AppSurfaceTestAspNetCorePolicyEvaluator>(serviceProvider),
            descriptor.Lifetime));
    }

    private static int FindLastPolicyEvaluator(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(IAppSurfaceAspNetCorePolicyEvaluator))
            {
                return index;
            }
        }

        return -1;
    }

    private static IAppSurfaceAspNetCorePolicyEvaluator CreateInnerPolicyEvaluator(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IAppSurfaceAspNetCorePolicyEvaluator instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IAppSurfaceAspNetCorePolicyEvaluator)descriptor.ImplementationFactory(serviceProvider)!;
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IAppSurfaceAspNetCorePolicyEvaluator)ActivatorUtilities.CreateInstance(
                serviceProvider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            "Problem: AppSurface ASP.NET Core policy evaluator registration could not be decorated. Cause: the service descriptor did not expose an implementation instance, factory, or type. Fix: register IAppSurfaceAspNetCorePolicyEvaluator with a concrete implementation descriptor before AddAppSurfaceTestAuth.");
    }
}
