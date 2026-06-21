namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Collection of seeded local-development personas for AppSurface DevAuth.
/// </summary>
public sealed class AppSurfaceDevAuthPersonaCollection
{
    private readonly Dictionary<string, AppSurfaceDevAuthPersona> _personas = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a local-development persona.
    /// </summary>
    /// <param name="id">URL-safe local persona id.</param>
    /// <param name="configure">Callback that configures the persona.</param>
    /// <returns>The same collection for chaining.</returns>
    public AppSurfaceDevAuthPersonaCollection Add(string id, Action<AppSurfaceDevAuthUserBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AppSurfaceDevAuthUserBuilder(id);
        configure(builder);
        var persona = builder.Build();

        if (!_personas.TryAdd(persona.Id, persona))
        {
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.InvalidPersonaId,
                $"ASDEV006 Problem: DevAuth persona id '{persona.Id}' is duplicated. Cause: seeded personas must have unique ids. Fix: remove the duplicate or choose a unique id. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }

        return this;
    }

    /// <summary>
    /// Gets the configured local-development personas.
    /// </summary>
    public IReadOnlyDictionary<string, AppSurfaceDevAuthPersona> Personas => _personas;
}
