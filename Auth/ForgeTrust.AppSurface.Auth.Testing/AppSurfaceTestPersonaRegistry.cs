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
        var personas = new Dictionary<string, AppSurfaceTestPersona>(StringComparer.Ordinal);
        foreach (var persona in options.Personas)
        {
            if (string.IsNullOrWhiteSpace(persona.Name))
            {
                throw new InvalidOperationException(
                    $"Problem: AppSurface test auth persona name is blank. Cause: A persona was registered without a stable name. Fix: give every persona a non-blank ordinal name. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.BlankPersonaName}.");
            }

            if (!personas.TryAdd(persona.Name, persona))
            {
                throw new InvalidOperationException(
                    $"Problem: AppSurface test auth persona '{persona.Name}' is registered more than once. Cause: persona names are matched with ordinal comparison and must be unique. Fix: remove the duplicate or rename one persona. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.DuplicatePersona}.");
            }
        }

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
