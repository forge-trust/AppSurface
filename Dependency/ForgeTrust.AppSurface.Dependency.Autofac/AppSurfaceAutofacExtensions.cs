using Autofac;
using Autofac.Builder;
using Autofac.Features.Scanning;

namespace ForgeTrust.AppSurface.Dependency.Autofac;

/// <summary>
/// Provides extension methods for Autofac's <see cref="ContainerBuilder"/> to simplify common registrations.
/// </summary>
/// <remarks>
/// These helpers are convenience wrappers for assembly-scoped reflection scanning. Prefer explicit Autofac
/// registration when ordering matters, multiple implementations need different lifetimes, startup performance is
/// sensitive, or AOT/linker trimming must preserve only known types.
/// </remarks>
public static class AppSurfaceAutofacExtensions
{
    /// <summary>
    /// Registers all non-abstract class implementations of the specified interface type found in the interface's assembly.
    /// </summary>
    /// <remarks>
    /// <see cref="RegisterImplementations{TInterface}"/> scans only the assembly that declares
    /// <typeparamref name="TInterface"/> and registers concrete, non-abstract assignable classes. It returns Autofac's
    /// <see cref="IRegistrationBuilder{TLimit,TActivatorData,TRegistrationStyle}"/> so callers can add lifetime,
    /// ownership, and metadata configuration. Reflection scanning can load dependent types, may miss trimmed/private
    /// implementation patterns, and should be called during container construction rather than concurrently with
    /// container use.
    /// </remarks>
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
