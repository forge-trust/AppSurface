namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// A builder used to discover and register module dependencies recursively.
/// </summary>
/// <remarks>
/// <see cref="ModuleDependencyBuilder"/> is intended for the single-threaded startup composition phase. It is not
/// thread-safe; callers must synchronize access if multiple threads could call <see cref="AddModule{T}"/> or enumerate
/// <see cref="Modules"/> concurrently.
/// </remarks>
public sealed class ModuleDependencyBuilder
{
    private readonly Dictionary<Type, IAppSurfaceModule> _modules = new();

    /// <summary>
    /// Gets the collection of registered modules.
    /// </summary>
    /// <remarks>
    /// Modules are keyed by concrete type and added once. Enumeration follows insertion order for the current
    /// dependency graph, with a module visible in <see cref="Modules"/> before its
    /// <see cref="IAppSurfaceModule.RegisterDependentModules(ModuleDependencyBuilder)"/> callback registers children.
    /// </remarks>
    public IEnumerable<IAppSurfaceModule> Modules => _modules.Values;

    /// <summary>
    /// Adds a module of type <typeparamref name="T"/> and its dependencies to the builder.
    /// </summary>
    /// <typeparam name="T">The module type to add. It must have a public parameterless constructor.</typeparam>
    /// <remarks>
    /// The module instance is created with the <c>new()</c> constraint and pre-registered before
    /// <see cref="IAppSurfaceModule.RegisterDependentModules(ModuleDependencyBuilder)"/> runs. That pre-registration
    /// prevents infinite recursion for cyclic module graphs and means dependency callbacks observe already-created
    /// module instances. Prefer manual registration or a factory-based composition root when module construction needs
    /// dependency injection, runtime arguments, or externally owned disposable resources. If dependency registration
    /// throws, the newly created module remains in the builder, so later <see cref="AddModule{T}"/> calls for that same
    /// type are no-ops against a partial graph. Treat registration failures as fatal for the current builder instance.
    /// </remarks>
    /// <returns>The current <see cref="ModuleDependencyBuilder"/> instance.</returns>
    public ModuleDependencyBuilder AddModule<T>()
        where T : IAppSurfaceModule, new()
    {
        var type = typeof(T);
        if (!_modules.ContainsKey(type))
        {
            var newModule = new T();
            _modules.Add(type, newModule);
            newModule.RegisterDependentModules(this);
        }

        return this;
    }
}
