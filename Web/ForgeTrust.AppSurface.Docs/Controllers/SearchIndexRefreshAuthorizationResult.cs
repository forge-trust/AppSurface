namespace ForgeTrust.AppSurface.Docs.Controllers;

internal readonly record struct SearchIndexRefreshAuthorizationResult(
    bool IsAllowed,
    SearchIndexRefreshAuthorizationFailure? Reason)
{
    public static SearchIndexRefreshAuthorizationResult Allowed()
    {
        return new SearchIndexRefreshAuthorizationResult(true, null);
    }

    public static SearchIndexRefreshAuthorizationResult Denied(SearchIndexRefreshAuthorizationFailure reason)
    {
        return new SearchIndexRefreshAuthorizationResult(false, reason);
    }
}

internal enum SearchIndexRefreshAuthorizationFailure
{
    MissingPolicyOption,
    MissingPolicyProvider,
    MissingAuthorizationService,
    PolicyNotFound,
    Unauthenticated,
    AuthorizationFailed
}
