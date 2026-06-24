namespace ForgeTrust.AppSurface.Auth.Testing;

internal sealed class AppSurfaceTestPersonaRegistry
{
    private readonly IReadOnlyDictionary<string, AppSurfaceTestPersona> _personas;

    private AppSurfaceTestPersonaRegistry(IReadOnlyDictionary<string, AppSurfaceTestPersona> personas)
    {
        _personas = personas;
    }

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

    public AppSurfaceTestPersona Require(string personaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaName);

        return TryGet(personaName, out var persona)
            ? persona
            : throw new InvalidOperationException(
                $"Problem: AppSurface test auth persona '{personaName}' was not found. Cause: the persona was not registered in WithAppSurfaceTestAuth/AddAppSurfaceTestAuth. Fix: add the persona to AppSurfaceTestAuthOptions before creating the client. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.UnknownPersona}.");
    }

    public bool TryGet(string personaName, out AppSurfaceTestPersona persona)
    {
        return _personas.TryGetValue(personaName, out persona!);
    }
}
