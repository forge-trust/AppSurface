namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Stores the immutable per-factory persona lookup used by AppSurface test auth helpers.
/// </summary>
/// <remarks>
/// Names use ordinal comparison and must be unique after <see cref="AppSurfaceTestPersona" /> trimming. Create the
/// registry only after options are fully configured; later option changes are intentionally not observed by an existing
/// registry.
/// </remarks>
internal sealed class AppSurfaceTestPersonaRegistry
{
    private readonly IReadOnlyDictionary<string, AppSurfaceTestPersona> _personas;

    private AppSurfaceTestPersonaRegistry(IReadOnlyDictionary<string, AppSurfaceTestPersona> personas)
    {
        _personas = personas;
    }

    /// <summary>
    /// Creates a registry from configured personas and validates duplicate ordinal names.
    /// </summary>
    /// <param name="options">Configured test auth options.</param>
    /// <returns>An immutable registry snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown when two personas share the same ordinal name.</exception>
    public static AppSurfaceTestPersonaRegistry Create(AppSurfaceTestAuthOptions options)
    {
        var duplicatePersona = options.Personas
            .GroupBy(persona => persona.Name, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.First())
            .FirstOrDefault();

        if (duplicatePersona is not null)
        {
            throw new InvalidOperationException(
                $"Problem: AppSurface test auth persona '{duplicatePersona.Name}' is registered more than once. Cause: persona names are matched with ordinal comparison and must be unique. Fix: remove the duplicate or rename one persona. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.DuplicatePersona}.");
        }

        var personas = options.Personas.ToDictionary(
            persona => persona.Name,
            StringComparer.Ordinal);

        return new AppSurfaceTestPersonaRegistry(personas);
    }

    /// <summary>
    /// Gets a registered persona or throws a setup diagnostic for public helper failures.
    /// </summary>
    /// <param name="personaName">Ordinal persona name.</param>
    /// <returns>The matching persona.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="personaName" /> is blank.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the persona is not registered.</exception>
    public AppSurfaceTestPersona Require(string personaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaName);

        return TryGet(personaName, out var persona)
            ? persona
            : throw new InvalidOperationException(
                $"Problem: AppSurface test auth persona '{personaName}' was not found. Cause: the persona was not registered in WithAppSurfaceTestAuth/AddAppSurfaceTestAuth. Fix: add the persona to AppSurfaceTestAuthOptions before creating the client. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.UnknownPersona}.");
    }

    /// <summary>
    /// Attempts to get a persona by ordinal name without throwing.
    /// </summary>
    /// <param name="personaName">Ordinal persona name.</param>
    /// <param name="persona">The matching persona when found.</param>
    /// <returns><see langword="true" /> when the persona exists; otherwise <see langword="false" />.</returns>
    public bool TryGet(string personaName, out AppSurfaceTestPersona persona)
    {
        return _personas.TryGetValue(personaName, out persona!);
    }
}
