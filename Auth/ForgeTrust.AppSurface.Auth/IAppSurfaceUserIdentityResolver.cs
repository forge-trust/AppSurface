namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Resolves authenticated external subjects to durable app-owned user ids.
/// </summary>
/// <remarks>
/// Applications implement this interface with their own persistence, provisioning, disabled-user, and duplicate-mapping
/// policies. Implementations should make successful resolution idempotent for the same
/// <see cref="ExternalSubject"/> tuple, honor cancellation before starting expensive work and while awaiting I/O, and
/// handle concurrent first-time resolution without creating duplicate app users.
/// <para>
/// Prefer enforcing uniqueness at the mapping store with a unique constraint over the external subject tuple
/// (issuer, subject, and partition key), then make competing inserts converge on the same app user id through
/// optimistic concurrency or a transaction retry. Stores that cannot enforce uniqueness should use an equivalent
/// first-provisioning guard, such as a short distributed lock scoped to the external subject tuple.
/// </para>
/// <para>
/// Avoid a check-then-insert flow that reads a missing mapping and blindly creates a new app user. Concurrent sign-ins
/// can otherwise provision duplicate app users before either caller observes the other mapping.
/// </para>
/// </remarks>
public interface IAppSurfaceUserIdentityResolver
{
    /// <summary>
    /// Resolves an external subject to a durable app-owned user id.
    /// </summary>
    /// <param name="subject">The authenticated external subject to resolve.</param>
    /// <param name="context">Display-safe resolution context supplied by the host integration.</param>
    /// <param name="cancellationToken">Cancellation token for resolver work and awaited I/O.</param>
    /// <returns>An identity resolution result with either an app-owned user id or a typed failure state.</returns>
    ValueTask<AppSurfaceUserIdentityResult> ResolveAsync(
        ExternalSubject subject,
        AppSurfaceUserIdentityResolutionContext context,
        CancellationToken cancellationToken = default);
}
