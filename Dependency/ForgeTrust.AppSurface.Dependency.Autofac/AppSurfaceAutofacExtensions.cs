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
    /// This method scans only the assembly that declares <typeparamref name="TInterface"/> and registers
    /// concrete, non-abstract assignable classes as
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
        where TInterface : notnull
    {
        return builder.RegisterImplementations<TInterface>(static assembly => assembly.GetTypes());
    }

    /// <summary>
    /// Registers implementations of <typeparamref name="TInterface"/> using a caller-provided assembly type loader.
    /// </summary>
    /// <remarks>
    /// This internal seam exists so tests can verify partial-load recovery without creating a broken fixture assembly.
    /// The <paramref name="getTypes"/> delegate must return a non-null array for successful loads. If it throws
    /// <see cref="ReflectionTypeLoadException"/>, successfully loaded non-null entries from
    /// <see cref="ReflectionTypeLoadException.Types"/> are still registered and failed entries are ignored.
    /// </remarks>
    /// <typeparam name="TInterface">The interface type to scan for implementations of.</typeparam>
    /// <param name="builder">The container builder.</param>
    /// <param name="getTypes">Type loader for the assembly that declares <typeparamref name="TInterface"/>.</param>
    /// <returns>A registration builder for the scanned types.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="getTypes"/> completes successfully but returns <see langword="null"/>.
    /// </exception>
    internal static IRegistrationBuilder<object, ScanningActivatorData, DynamicRegistrationStyle>
        RegisterImplementations<TInterface>(this ContainerBuilder builder, Func<Assembly, Type[]> getTypes)
        where TInterface : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(getTypes);
        if (!typeof(TInterface).IsInterface)
        {
            throw new ArgumentException(
                $"{typeof(TInterface).FullName} must be an interface type.",
                nameof(TInterface));
        }

        var assembly = typeof(TInterface).Assembly;
        var loadedTypes = GetLoadableTypes(assembly, getTypes);
        var types = loadedTypes
            .Where(t => t.IsClass && !t.IsAbstract && typeof(TInterface).IsAssignableFrom(t));

        return builder.RegisterTypes(types.ToArray())
            .As<TInterface>();
    }

    /// <summary>
    /// Gets all loadable types from an assembly while tolerating partial reflection-load failures.
    /// </summary>
    /// <param name="assembly">The assembly being scanned.</param>
    /// <param name="getTypes">The delegate used to load the assembly's type array.</param>
    /// <returns>All loaded types, or the non-null subset from <see cref="ReflectionTypeLoadException.Types"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="getTypes"/> completes successfully but returns <see langword="null"/>.
    /// </exception>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, Func<Assembly, Type[]> getTypes)
    {
        try
        {
            return getTypes(assembly)
                ?? throw new InvalidOperationException(
                    $"The type loader returned null for assembly '{assembly.FullName}'.");
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
