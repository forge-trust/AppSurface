using System.Security.Claims;
using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Configures how ASP.NET Core request principals are mapped into AppSurface auth contracts.
/// </summary>
/// <remarks>
/// These options do not configure authentication schemes, authorization policies, cookies, bearer tokens, identity
/// providers, middleware, challenges, or forbids. ASP.NET Core remains the source of truth for those behaviors. The
/// adapter only decides which already-authenticated claim represents the stable AppSurface subject identifier.
/// </remarks>
public sealed class AppSurfaceAspNetCoreAuthOptions
{
    private readonly List<string> _subjectClaimTypes =
    [
        ClaimTypes.NameIdentifier,
        "sub",
        AppSurfaceAuthMetadataKeys.SubjectId,
    ];

    /// <summary>
    /// Gets the ordered claim types used to resolve the stable AppSurface subject identifier.
    /// </summary>
    /// <remarks>
    /// Only authenticated ASP.NET Core identities are inspected. Claims on unauthenticated identities are ignored even
    /// when they use one of these claim types.
    /// </remarks>
    public IReadOnlyList<string> SubjectClaimTypes => _subjectClaimTypes;

    /// <summary>
    /// Gives a claim type first priority when resolving the stable AppSurface subject identifier.
    /// </summary>
    /// <param name="claimType">Claim type that carries the host-owned stable subject id.</param>
    /// <returns>The current options instance so calls can be chained from registration lambdas.</returns>
    /// <remarks>
    /// This method maps the subject identifier only. It does not map display names, emails, roles, permissions, scopes,
    /// or authorization truth. Host-owned ASP.NET Core policies remain responsible for permission decisions.
    /// </remarks>
    public AppSurfaceAspNetCoreAuthOptions MapSubjectClaim(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        _subjectClaimTypes.RemoveAll(value => string.Equals(value, claimType, StringComparison.Ordinal));
        _subjectClaimTypes.Insert(0, claimType);
        return this;
    }
}
