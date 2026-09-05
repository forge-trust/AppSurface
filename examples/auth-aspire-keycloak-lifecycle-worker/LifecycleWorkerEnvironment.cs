namespace AuthAspireKeycloakLifecycleWorker;

/// <summary>
/// Names the private environment values used only by the #782 finite-worker spike.
/// </summary>
public static class AuthAspireKeycloakLifecycleWorkerEnvironment
{
    /// <summary>
    /// Selects the finite worker behavior observed by the AppHost feasibility graph.
    /// </summary>
    public const string Mode = "AUTH_ASPIRE_KEYCLOAK_LIFECYCLE_MODE";

    /// <summary>
    /// Completes successfully.
    /// </summary>
    public const string Success = "success";

    /// <summary>
    /// Completes with a nonzero worker failure.
    /// </summary>
    public const string Failure = "failure";

    /// <summary>
    /// Simulates a consumer-owned bounded timeout by completing nonzero.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>
    /// Remains non-completed until AppHost cancellation.
    /// </summary>
    public const string Hang = "hang";
}
