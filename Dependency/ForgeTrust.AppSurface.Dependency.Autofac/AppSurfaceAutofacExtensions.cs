using System.Reflection;
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
    /// <typeparamref name="TInterface"/> and registers concrete, non-abstract assignable classes as
    /// <typeparamref name="TInterface"/> services. It returns Autofac's
    /// <see cref="IRegistrationBuilder{TLimit,TActivatorData,TRegistrationStyle}"/> so callers can add lifetime,
    /// ownership, and metadata configuration. Use this helper when one interface owns a small assembly-local plugin
    /// surface and interface resolution is the intended contract. Prefer explicit registrations when implementations
    /// cross assemblies, require different service interfaces, need distinct lifetimes, or must be linker/AOT-friendly.
    /// Reflection scanning recovers from partial type-load failures by registering successfully loaded types only, so
    /// missing optional dependencies can still hide implementations that failed to load.
    /// </remarks>
    /// <typeparam name="TInterface">The interface type to scan for implementations of.</typeparam>
    /// <param name="builder">The container builder.</param>
    /// <returns>A registration builder for the scanned types.</returns>
    public static IRegistrationBuilder<object, ScanningActivatorData, DynamicRegistrationStyle>
        RegisterImplementations<TInterface>(this ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!typeof(TInterface).IsInterface)
        {
            throw new ArgumentException(
                $"{typeof(TInterface).FullName} must be an interface type.",
                nameof(TInterface));
        }

        var assembly = typeof(TInterface).Assembly;
        var loadedTypes = GetLoadableTypes(assembly);
        var types = loadedTypes
            .Where(t => t.IsClass && !t.IsAbstract && typeof(TInterface).IsAssignableFrom(t));

        return builder.RegisterTypes(types.ToArray())
            .As(typeof(TInterface));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
