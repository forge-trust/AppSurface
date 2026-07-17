namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Evaluates application-owned proof for one registered named canary.
/// </summary>
/// <remarks>
/// Evaluators inspect existing proof exactly once per request. They should not trigger synthetic work, retry, poll,
/// or change application readiness. The caller owns those orchestration decisions.
/// </remarks>
public interface IAppSurfaceCanaryEvaluator
{
    /// <summary>
    /// Evaluates current proof for a named canary.
    /// </summary>
    /// <param name="context">The validated canary name and optional deploy-proof inputs.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current canary status plus any optional validated, bounded evidence.</returns>
    ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken);
}
