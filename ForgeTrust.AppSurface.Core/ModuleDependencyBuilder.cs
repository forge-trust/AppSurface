namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// A builder used to discover and register module dependencies recursively.
/// </summary>
public sealed class ModuleDependencyBuilder
{
    private readonly Dictionary<Type, IAppSurfaceModule> _modules = new();

    /// <summary>
    /// Gets the collection of registered modules.
    /// </summary>
    public IEnumerable<IAppSurfaceModule> Modules => _modules.Values;

    /// <summary>
    /// Adds a module of type <typeparamref name="T"/> and its dependencies to the builder.
    /// </summary>
    /// <typeparam name="T">The type of the module to add.</typeparam>
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
