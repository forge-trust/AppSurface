using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Stores AppSurface Flow definitions by context type, flow id, and version.
/// </summary>
public interface IFlowDefinitionRegistry
{
    /// <summary>
    /// Registers a flow definition.
    /// </summary>
    /// <typeparam name="TContext">Context type for the definition.</typeparam>
    /// <param name="definition">Definition to register.</param>
    /// <exception cref="ArgumentException">Thrown when the same context, flow id, and version are already registered.</exception>
    void Register<TContext>(FlowDefinition<TContext> definition);

    /// <summary>
    /// Tries to resolve a registered flow definition.
    /// </summary>
    /// <typeparam name="TContext">Expected context type.</typeparam>
    /// <param name="flowId">Flow id.</param>
    /// <param name="version">Flow version.</param>
    /// <param name="definition">Resolved definition, when found.</param>
    /// <returns><see langword="true"/> when a matching definition exists.</returns>
    bool TryGet<TContext>(
        string flowId,
        string version,
        [NotNullWhen(true)] out FlowDefinition<TContext>? definition);

    /// <summary>
    /// Resolves a registered flow definition or throws a diagnostic exception.
    /// </summary>
    /// <typeparam name="TContext">Expected context type.</typeparam>
    /// <param name="flowId">Flow id.</param>
    /// <param name="version">Flow version.</param>
    /// <returns>The matching definition.</returns>
    /// <exception cref="FlowDefinitionException">Thrown when the definition is missing or registered with another context type.</exception>
    FlowDefinition<TContext> GetRequired<TContext>(string flowId, string version);
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IFlowDefinitionRegistry"/>.
/// </summary>
public sealed class FlowDefinitionRegistry : IFlowDefinitionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<FlowDefinitionKey, object> _definitions = new();

    /// <inheritdoc />
    public void Register<TContext>(FlowDefinition<TContext> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var key = new FlowDefinitionKey(typeof(TContext), definition.FlowId, definition.Version);
        lock (_gate)
        {
            if (!_definitions.TryAdd(key, definition))
            {
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{definition.FlowId}' version '{definition.Version}' is already registered for context '{typeof(TContext).FullName}'."),
                    nameof(definition));
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet<TContext>(
        string flowId,
        string version,
        [NotNullWhen(true)] out FlowDefinition<TContext>? definition)
    {
        var key = new FlowDefinitionKey(
            typeof(TContext),
            FlowDefinition<TContext>.RequireText(flowId, nameof(flowId)),
            FlowDefinition<TContext>.RequireText(version, nameof(version)));

        lock (_gate)
        {
            if (_definitions.TryGetValue(key, out var stored) && stored is FlowDefinition<TContext> typed)
            {
                definition = typed;
                return true;
            }
        }

        definition = null;
        return false;
    }

    /// <inheritdoc />
    public FlowDefinition<TContext> GetRequired<TContext>(string flowId, string version)
    {
        if (TryGet<TContext>(flowId, version, out var definition))
        {
            return definition;
        }

        throw new FlowDefinitionException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Flow '{flowId}' version '{version}' is not registered for context '{typeof(TContext).FullName}'."));
    }

    private sealed record FlowDefinitionKey(Type ContextType, string FlowId, string Version);
}
