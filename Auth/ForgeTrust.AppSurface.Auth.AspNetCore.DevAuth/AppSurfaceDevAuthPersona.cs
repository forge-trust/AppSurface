using System.Security.Claims;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Represents a seeded local-development persona that can be selected by AppSurface DevAuth.
/// </summary>
public sealed class AppSurfaceDevAuthPersona
{
    internal AppSurfaceDevAuthPersona(
        string id,
        string displayName,
        string subjectClaimType,
        string subject,
        IReadOnlyList<Claim> claims)
    {
        Id = id;
        DisplayName = displayName;
        SubjectClaimType = subjectClaimType;
        Subject = subject;
        Claims = claims;
    }

    /// <summary>
    /// Gets the stable local persona id used by the DevAuth selection cookie.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name shown on the local-only DevAuth control page.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the claim type used to expose the stable local subject.
    /// </summary>
    public string SubjectClaimType { get; }

    /// <summary>
    /// Gets the stable local subject value for this persona.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Gets safe local-development claims issued for this persona.
    /// </summary>
    public IReadOnlyList<Claim> Claims { get; }
}
