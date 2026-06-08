using System.Security.Claims;
using ForgeTrust.AppSurface.Auth;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

internal sealed class AppSurfaceAspNetCoreAuthContextMapper
{
    private readonly IOptions<AppSurfaceAspNetCoreAuthOptions> _options;

    public AppSurfaceAspNetCoreAuthContextMapper(IOptions<AppSurfaceAspNetCoreAuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

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

    private string? ResolveSubject(IReadOnlyList<ClaimsIdentity> identities)
    {
        foreach (var claimType in _options.Value.SubjectClaimTypes)
        {
            var value = identities
                .SelectMany(identity => identity.FindAll(claimType))
                .Select(claim => claim.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
