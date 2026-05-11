using Autofac;
using Autofac.Builder;
using Autofac.Features.Scanning;

namespace ForgeTrust.AppSurface.Dependency.Autofac;

/// <summary>
/// Provides extension methods for Autofac's <see cref="ContainerBuilder"/> to simplify common registrations.
/// </summary>
public static class AppSurfaceAutofacExtensions
{
    /// <summary>
    /// Registers all non-abstract class implementations of the specified interface type found in the interface's assembly.
    /// </summary>
    /// <typeparam name="TInterface">The interface type to scan for implementations of.</typeparam>
    /// <param name="builder">The container builder.</param>
    /// <returns>A registration builder for the scanned types.</returns>
    public static IRegistrationBuilder<object, ScanningActivatorData, DynamicRegistrationStyle>
        RegisterImplementations<TInterface>(this ContainerBuilder builder)
    {
        var assembly = typeof(TInterface).Assembly;
        var types = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(TInterface).IsAssignableFrom(t));

        return builder.RegisterTypes(types.ToArray());
    }
}
