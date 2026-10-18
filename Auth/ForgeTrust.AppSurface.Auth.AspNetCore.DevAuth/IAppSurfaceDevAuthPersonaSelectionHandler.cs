using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Prepares host-owned local fixtures when a configured DevAuth persona is explicitly selected.
/// </summary>
/// <remarks>
/// Opt in with one unkeyed scoped registration using <c>AddScoped</c>. DevAuth resolves the optional handler from
/// the selection request's <see cref="HttpContext.RequestServices"/> after queuing the protected persona cookie,
/// and awaits it before redirecting or rendering the control page. No registration preserves the default behavior.
/// Ordinary requests, control/status GETs, clearing and rejected selections do not resolve or invoke this service.
/// Compose multiple preparation steps within one implementation. The host owns fixture identity, persistence,
/// idempotence, concurrency, deadlines and recovery; repeated selections can invoke activation again.
/// Cookie delivery and fixture writes are not atomic. Resolution failures, activation errors and cancellation
/// propagate to the host pipeline without normal success navigation or framework rollback.
/// See the package README's persona-selection activation guide for registration and recovery examples.
/// </remarks>
public interface IAppSurfaceDevAuthPersonaSelectionHandler
{
    /// <summary>
    /// Completes preparation of the local fixtures required by the explicitly selected persona.
    /// </summary>
    /// <param name="persona">
    /// The validated configured persona. Use this argument for the selected identity: the incoming principal and
    /// request cookies can still describe the previously selected persona.
    /// </param>
    /// <param name="httpContext">The current selection request, whose services share this handler's request scope.</param>
    /// <param name="cancellationToken">
    /// The request's <see cref="HttpContext.RequestAborted"/> token. Observe it cooperatively during preparation.
    /// DevAuth also checks it before invocation and after normal completion.
    /// </param>
    /// <returns>A task that completes only when the required fixtures are ready.</returns>
    /// <remarks>
    /// Throw when required preparation fails. Do not write, start or redirect the response: DevAuth owns successful
    /// navigation and the host's exception pipeline owns failures. Do not retain the context, modify configured
    /// persona metadata or start background work that outlives the request. Cancellation can occur after writes;
    /// readiness must be checked by host pages and retry safety must be implemented by host persistence.
    /// </remarks>
    ValueTask ActivateAsync(
        AppSurfaceDevAuthPersona persona,
        HttpContext httpContext,
        CancellationToken cancellationToken);
}
