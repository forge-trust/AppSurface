using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Forms;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Provides extension methods for registering RazorWire services into the <see cref="IServiceCollection"/>.
/// </summary>
public static class RazorWireServiceCollectionExtensions
{
    /// <summary>
    /// Registers RazorWire options and default RazorWire services, including <see cref="IRazorPartialRenderer"/>, into the provided <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to register RazorWire services into.</param>
    /// <param name="configure">
    /// Optional action to configure <see cref="RazorWireOptions"/>; if null, default options are used.
    /// Stream subscriptions are denied by default because
    /// <see cref="RazorWireStreamAuthorizationMode.DenyAll"/> is the default authorization mode.
    /// </param>
    /// <remarks>
    /// This method also calls <see cref="LoggingServiceCollectionExtensions.AddLogging(IServiceCollection)"/> and
    /// <see cref="AntiforgeryServiceCollectionExtensions.AddAntiforgery(IServiceCollection)"/> because RazorWire live
    /// streams log denied subscriptions and RazorWire forms use ASP.NET Core anti-forgery services for lazy token
    /// refresh. If the host has already configured logging or anti-forgery, the normal ASP.NET Core options pipeline
    /// composes with those registrations.
    ///
    /// If no custom <see cref="IRazorWireChannelAuthorizer"/> is registered, RazorWire resolves a built-in authorizer
    /// from <see cref="RazorWireOptions.Streams"/>.<see cref="RazorWireStreamOptions.AuthorizationMode"/>.
    /// <see cref="RazorWireStreamAuthorizationMode.DenyAll"/> selects
    /// <see cref="DenyAllRazorWireChannelAuthorizer"/>, while
    /// <see cref="RazorWireStreamAuthorizationMode.AllowAll"/> selects
    /// <see cref="AllowAllRazorWireChannelAuthorizer"/>. Register a custom
    /// <see cref="IRazorWireChannelAuthorizer"/> before or after this method when stream access depends on the current
    /// request, user, tenant, or workflow. Unknown authorization-mode values throw <see cref="InvalidOperationException"/>
    /// during authorizer resolution instead of falling back to an unsafe allow path.
    /// </remarks>
    /// <returns>The same <see cref="IServiceCollection"/> instance with RazorWire registrations added.</returns>
    public static IServiceCollection AddRazorWire(
        this IServiceCollection services,
        Action<RazorWireOptions>? configure = null)
    {
        services.AddOptions<RazorWireOptions>()
            .ValidateOnStart();

        services.Configure(configure ?? (_ => { }));
        services.AddLogging();
        services.AddAntiforgery();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RazorWireOptions>, RazorWireOptionsValidator>());

        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<RazorWireOptions>>().Value);

        services.TryAddSingleton<IRazorWireStreamHub, InMemoryRazorWireStreamHub>();
        services.TryAddSingleton<RazorWireStreamAdmissionController>();
        services.TryAddSingleton<DenyAllRazorWireChannelAuthorizer>();
        services.TryAddSingleton<AllowAllRazorWireChannelAuthorizer>();
        services.TryAddSingleton<IRazorWireChannelAuthorizer>(sp =>
        {
            var mode = sp.GetRequiredService<IOptions<RazorWireOptions>>().Value.Streams.AuthorizationMode;

            return mode switch
            {
                RazorWireStreamAuthorizationMode.DenyAll => sp.GetRequiredService<DenyAllRazorWireChannelAuthorizer>(),
                RazorWireStreamAuthorizationMode.AllowAll => sp.GetRequiredService<AllowAllRazorWireChannelAuthorizer>(),
                _ => throw new InvalidOperationException(
                    $"Unknown RazorWire stream authorization mode '{mode}'. " +
                    $"Use {nameof(RazorWireStreamAuthorizationMode.DenyAll)}, " +
                    $"{nameof(RazorWireStreamAuthorizationMode.AllowAll)}, or register a custom " +
                    $"{nameof(IRazorWireChannelAuthorizer)}.")
            };
        });
        services.TryAddSingleton<IRazorPartialRenderer, RazorPartialRenderer>();
        services.TryAddSingleton<RazorWireFormRequestClassifier>();
        services.TryAddScoped<RazorWireAntiforgeryFailureFilter>();

        return services;
    }
}
