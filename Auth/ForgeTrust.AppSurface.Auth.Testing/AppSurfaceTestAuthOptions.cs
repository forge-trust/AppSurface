using System.Security.Claims;
using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Configures the AppSurface auth testing harness.
/// </summary>
/// <remarks>
/// Defaults are intentionally test-host oriented: the scheme name is stable, WebApplicationFactory quickstarts use the
/// test scheme as the default, and production-like environments are blocked. Subject mapping is not changed unless
/// <see cref="SubjectClaimType" /> is set.
/// </remarks>
public sealed class AppSurfaceTestAuthOptions
{
    private readonly List<AppSurfaceTestPersona> _personas = [];
    private readonly IReadOnlyList<AppSurfaceTestPersona> _personasView;

    /// <summary>
    /// Creates AppSurface test auth options.
    /// </summary>
    public AppSurfaceTestAuthOptions()
    {
        _personasView = _personas.AsReadOnly();
    }

    /// <summary>
    /// Gets or sets the ASP.NET Core authentication scheme registered for test personas.
    /// </summary>
    public string SchemeName { get; set; } = AppSurfaceTestAuthDefaults.AuthenticationScheme;

    /// <summary>
    /// Gets or sets how the test scheme is applied to the host authentication setup.
    /// </summary>
    public AppSurfaceTestAuthSchemeMode SchemeMode { get; set; } = AppSurfaceTestAuthSchemeMode.DefaultScheme;

    /// <summary>
    /// Gets or sets the subject claim type used by generated principals and AppSurface subject mapping.
    /// </summary>
    /// <remarks>
    /// When this value is <see langword="null" />, the harness leaves host AppSurface subject mapping alone and emits
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier" />, matching the first default
    /// <c>Auth.AspNetCore</c> subject claim type.
    /// </remarks>
    public string? SubjectClaimType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the harness may run in a production-like environment.
    /// </summary>
    /// <remarks>
    /// Keep this false for normal tests. Set it only for production-like integration hosts that deliberately use test
    /// authentication and never receive real traffic.
    /// </remarks>
    public bool AllowProductionEnvironmentForTestHost { get; set; }

    /// <summary>
    /// Gets the configured immutable persona definitions.
    /// </summary>
    public IReadOnlyList<AppSurfaceTestPersona> Personas => _personasView;

    /// <summary>
    /// Adds a persona with the supplied subject and claims.
    /// </summary>
    /// <param name="name">Stable ordinal persona name.</param>
    /// <param name="subjectId">Stable host-owned subject identifier.</param>
    /// <param name="claims">Additional claims copied into the generated principal.</param>
    /// <returns>The current options instance for chaining.</returns>
    public AppSurfaceTestAuthOptions AddPersona(string name, string subjectId, IEnumerable<Claim>? claims = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Problem: AppSurface test auth persona name is blank. Cause: A persona was registered without a stable name. Fix: give every persona a non-blank ordinal name. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.BlankPersonaName}.");
        }

        return AddPersona(new AppSurfaceTestPersona(name, subjectId, claims));
    }

    /// <summary>
    /// Adds a persona definition.
    /// </summary>
    /// <param name="persona">Persona to add to the registry.</param>
    /// <returns>The current options instance for chaining.</returns>
    public AppSurfaceTestAuthOptions AddPersona(AppSurfaceTestPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        _personas.Add(persona);
        return this;
    }

    internal string ResolveSubjectClaimType()
    {
        return string.IsNullOrWhiteSpace(SubjectClaimType)
            ? ClaimTypes.NameIdentifier
            : SubjectClaimType.Trim();
    }
}
