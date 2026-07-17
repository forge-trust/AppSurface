using System.Security.Claims;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Validates one bearer token for the antiforgery-free Web Push subscription rail.</summary>
/// <remarks>
/// The package extracts the token from a syntactically valid HTTP <c>Authorization: Bearer</c> header and supplies
/// only the token value. Implementations should validate issuer, audience, signature, lifetime, and any app-specific
/// revocation requirements, then return an authenticated principal or <see langword="null"/>. They must not fall
/// back to cookies, ambient request identities, or other credentials. Bearer tokens are sensitive and must not be
/// logged. Register exactly one implementation before mapping bearer subscription endpoints.
/// </remarks>
public interface IAppSurfaceWebPushBearerTokenValidator
{
    /// <summary>Validates a bearer token and returns its authenticated principal when accepted.</summary>
    /// <param name="bearerToken">The nonblank token value extracted from the Authorization header.</param>
    /// <param name="cancellationToken">Cancels validation when the request is aborted.</param>
    /// <returns>An authenticated principal when accepted; otherwise <see langword="null"/>.</returns>
    /// <exception cref="OperationCanceledException">The request is canceled.</exception>
    ValueTask<ClaimsPrincipal?> ValidateAsync(
        string bearerToken,
        CancellationToken cancellationToken = default);
}
