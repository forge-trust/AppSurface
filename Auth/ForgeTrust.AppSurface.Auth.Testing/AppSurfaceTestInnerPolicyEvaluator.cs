using ForgeTrust.AppSurface.Auth.AspNetCore;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Holds the host policy evaluator registration that Auth.Testing decorates.
/// </summary>
/// <param name="policyEvaluator">
/// The evaluator that was registered before <c>AddAppSurfaceTestAuth</c> installed test-only diagnostics.
/// </param>
internal sealed class AppSurfaceTestInnerPolicyEvaluator(IAppSurfaceAspNetCorePolicyEvaluator policyEvaluator)
{
    /// <summary>
    /// Gets the host evaluator that continues to own real policy evaluation and result mapping.
    /// </summary>
    public IAppSurfaceAspNetCorePolicyEvaluator PolicyEvaluator { get; } =
        policyEvaluator ?? throw new ArgumentNullException(nameof(policyEvaluator));
}
