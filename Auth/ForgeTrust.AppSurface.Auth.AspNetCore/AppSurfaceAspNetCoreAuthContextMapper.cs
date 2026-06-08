using System.Security.Claims;
using ForgeTrust.AppSurface.Auth;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Maps an ASP.NET Core <see cref="ClaimsPrincipal" /> into an AppSurface auth context snapshot.
/// </summary>
/// <remarks>
/// Only authenticated identities are considered. Anonymous principals map to an anonymous success snapshot, while an
/// authenticated principal without a configured stable subject claim maps to a missing-subject setup failure. This
/// keeps AppSurface from silently treating a misconfigured authenticated request as either anonymous or allowed.
/// </remarks>
internal sealed class AppSurfaceAspNetCoreAuthContextMapper
{
    private readonly IOptions<AppSurfaceAspNetCoreAuthOptions> _options;

    /// <summary>
    /// Creates a mapper using the configured subject-claim precedence.
    /// </summary>
    /// <param name="options">Options that define which claim types can provide the stable AppSurface subject id.</param>
    public AppSurfaceAspNetCoreAuthContextMapper(IOptions<AppSurfaceAspNetCoreAuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Converts an ASP.NET Core principal into a neutral AppSurface auth-context snapshot.
    /// </summary>
    /// <param name="principal">The principal populated by ASP.NET Core authentication middleware.</param>
    /// <returns>
    /// An anonymous snapshot for null or unauthenticated principals, an authenticated snapshot when a configured
    /// subject claim is present, or a missing-subject failure snapshot for authenticated principals without a subject.
    /// </returns>
    public AppSurfaceAspNetCoreAuthContextSnapshot Map(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return new AppSurfaceAspNetCoreAuthContextSnapshot(AppSurfaceAuthContext.Anonymous);
        }

        var authenticatedIdentities = principal.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();

        if (authenticatedIdentities.Length == 0)
        {
            return new AppSurfaceAspNetCoreAuthContextSnapshot(AppSurfaceAuthContext.Anonymous);
        }

        var subject = ResolveSubject(authenticatedIdentities);
        if (subject is null)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = "missing_subject_claim",
                [AppSurfaceAspNetCoreAuthMetadataKeys.SubjectClaimTypes] =
                    string.Join(",", _options.Value.SubjectClaimTypes),
            };

            return new AppSurfaceAspNetCoreAuthContextSnapshot(
                AppSurfaceAuthContext.Anonymous,
                AppSurfaceAuthResult.MissingSubject(
                    AppSurfaceAuthContext.Anonymous,
                    "The authenticated ASP.NET Core principal did not contain a configured stable subject claim.",
                    metadata));
        }

        var authType = authenticatedIdentities
            .Select(identity => identity.AuthenticationType)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var contextMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceAuthMetadataKeys.SubjectId] = subject,
        };

        if (!string.IsNullOrWhiteSpace(authType))
        {
            contextMetadata[AppSurfaceAuthMetadataKeys.AuthenticationScheme] = authType;
        }

        var user = new AppSurfaceUser(subject, metadata: contextMetadata);
        var context = new AppSurfaceAuthContext(user, metadata: contextMetadata);

        return new AppSurfaceAspNetCoreAuthContextSnapshot(context);
    }

    /// <summary>
    /// Resolves the first non-blank stable subject value using configured claim-type precedence.
    /// </summary>
    /// <param name="identities">Authenticated identities whose claims are safe to inspect.</param>
    /// <returns>The first matching subject value, or <see langword="null" /> when none is present.</returns>
    private string? ResolveSubject(IReadOnlyList<ClaimsIdentity> identities)
    {
        return _options.Value.SubjectClaimTypes
            .Select(claimType => identities
                .SelectMany(identity => identity.FindAll(claimType))
                .Select(claim => claim.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)))
            .FirstOrDefault(value => value is not null);
    }
}
