namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Selects how the AppSurface test authentication scheme is registered with ASP.NET Core.
/// </summary>
/// <remarks>
/// These modes exist so a test harness cannot silently change host authentication behavior. Use
/// <see cref="DefaultScheme" /> for the quickest WebApplicationFactory path, <see cref="NamedScheme" /> when policies
/// opt in to a specific scheme, and <see cref="NoDefault" /> when the test host must keep every existing default
/// authentication setting untouched.
/// </remarks>
public enum AppSurfaceTestAuthSchemeMode
{
    /// <summary>
    /// Registers the test scheme and makes it ASP.NET Core's default authenticate, challenge, and forbid scheme.
    /// </summary>
    DefaultScheme = 0,

    /// <summary>
    /// Registers the test scheme by name without changing the host's default authentication schemes.
    /// </summary>
    NamedScheme = 1,

    /// <summary>
    /// Registers the persona registry without registering or defaulting the test authentication scheme.
    /// </summary>
    NoDefault = 2,
}
