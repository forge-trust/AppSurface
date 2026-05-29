namespace ForgeTrust.AppSurface.Docs.Controllers;

/// <summary>
/// Represents the internal authorization outcome for an AppSurface Docs search-index refresh request.
/// </summary>
/// <param name="IsAllowed">
/// Indicates whether the refresh action may invalidate the search-index cache.
/// </param>
/// <param name="Reason">
/// The denial reason when <paramref name="IsAllowed"/> is <see langword="false"/>; otherwise <see langword="null"/>.
/// </param>
/// <remarks>
/// Use <see cref="Allowed"/> and <see cref="Denied(SearchIndexRefreshAuthorizationFailure)"/> to preserve the two valid
/// states: allowed results have <c>IsAllowed == true</c> and no reason, while denied results have
/// <c>IsAllowed == false</c> and a concrete reason. Consumers should check <see cref="IsAllowed"/> before reading
/// <see cref="Reason"/> because the reason is intentionally nullable for successful authorization. This immutable record
/// struct is internal to the controller layer and should not become part of a public wire contract.
/// </remarks>
internal readonly record struct SearchIndexRefreshAuthorizationResult(
    bool IsAllowed,
    SearchIndexRefreshAuthorizationFailure? Reason)
{
    /// <summary>
    /// Creates an authorization result that permits cache invalidation.
    /// </summary>
    /// <returns>An allowed result with no failure reason.</returns>
    public static SearchIndexRefreshAuthorizationResult Allowed()
    {
        return new SearchIndexRefreshAuthorizationResult(true, null);
    }

    /// <summary>
    /// Creates an authorization result that denies cache invalidation for a specific reason.
    /// </summary>
    /// <param name="reason">The concrete denial reason to expose to internal logging and branch-specific tests.</param>
    /// <returns>A denied result whose <see cref="Reason"/> is set to <paramref name="reason"/>.</returns>
    public static SearchIndexRefreshAuthorizationResult Denied(SearchIndexRefreshAuthorizationFailure reason)
    {
        return new SearchIndexRefreshAuthorizationResult(false, reason);
    }
}

/// <summary>
/// Enumerates the internal reasons a search-index refresh request can be denied before cache invalidation.
/// </summary>
/// <remarks>
/// These reasons intentionally distinguish host setup failures from caller authorization failures so logs and tests can
/// identify the remediation path. They are not serialized to clients; the packaged endpoint returns HTTP 403 for every
/// denial after anti-forgery validation succeeds.
/// </remarks>
internal enum SearchIndexRefreshAuthorizationFailure
{
    /// <summary>
    /// No non-blank <c>AppSurfaceDocs:Diagnostics:SearchIndexRefreshPolicy</c> value was configured.
    /// </summary>
    MissingPolicyOption,

    /// <summary>
    /// The current request cannot resolve an <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider"/>.
    /// </summary>
    MissingPolicyProvider,

    /// <summary>
    /// The current request cannot resolve an <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>.
    /// </summary>
    MissingAuthorizationService,

    /// <summary>
    /// The configured policy name did not resolve to an authorization policy.
    /// </summary>
    PolicyNotFound,

    /// <summary>
    /// The current principal is missing, has no identity, or is not authenticated.
    /// </summary>
    Unauthenticated,

    /// <summary>
    /// The current authenticated principal did not satisfy the configured policy.
    /// </summary>
    AuthorizationFailed
}
