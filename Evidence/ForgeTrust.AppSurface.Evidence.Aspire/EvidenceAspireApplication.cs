using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>
/// Retains the legacy consumer-composed Aspire application lease for source compatibility.
/// </summary>
/// <remarks>
/// The public <see cref="StartAsync"/> method rejects a supplied builder with ASEVD400 before
/// building or starting it. A preconfigured builder cannot establish authenticated admission or
/// the root-owned restricted application lease required by <see cref="EvidenceHostBootstrap"/>.
/// Current production application registration remains closed until its consumer proof is accepted;
/// this compatibility API must not be added to normal application startup.
/// </remarks>
public sealed class EvidenceAspireApplication : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private readonly DistributedApplication _application;
    private int _disposed;

    private EvidenceAspireApplication(DistributedApplication application)
    {
        _application = application;
    }

    /// <summary>
    /// Rejects a preconfigured consumer Aspire builder before application construction.
    /// </summary>
    /// <param name="builder">Consumer-composed Aspire builder.</param>
    /// <param name="cancellationToken">Retained for source compatibility; rejection precedes application work.</param>
    /// <returns>A faulted task containing an <see cref="EvidenceAdmissionException"/> with code ASEVD400.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static Task<EvidenceAspireApplication> StartAsync(
        IDistributedApplicationBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // A preconfigured builder may already have run consumer configuration. Keep this compatibility
        // entry point closed before build or start; restricted application ownership belongs to the host.
        _ = cancellationToken;
        return Task.FromException<EvidenceAspireApplication>(
            new EvidenceAdmissionException("ASEVD400", "Use the host-owned admitted Aspire application factory."));
    }

    /// <summary>Starts an application only after admission and registers ownership before startup can fail.</summary>
    /// <remarks>
    /// The owning host must register its cleanup callback before calling this method and stop/join all work
    /// before invoking the registered application's disposer. This operation never cleans up inside a failed
    /// start callback. Production callers additionally require a proved restricted child capability.
    /// </remarks>
    internal static ValueTask<EvidenceAspireApplication> StartAdmittedAsync(
        EvidenceAdmissionResult admission,
        EvidencePlan plan,
        Func<IDistributedApplicationBuilder> builderFactory,
        Action<EvidenceAspireApplication> registerOwnership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builderFactory);
        return StartOwnedAsync(
            admission,
            plan,
            () =>
            {
                var builder = builderFactory();
                ArgumentNullException.ThrowIfNull(builder);
                return new EvidenceAspireApplication(builder.Build());
            },
            registerOwnership,
            static (application, token) => application._application.StartAsync(token),
            cancellationToken);
    }

    /// <summary>Shares admission and ownership ordering with deterministic application-start tests.</summary>
    /// <remarks>
    /// The ownership callback must retain the lease before it returns and cannot dispose it during startup.
    /// It runs under the host's tracked callback, whose real completion is joined before cleanup begins.
    /// This internal seam supplies no restricted-child or platform admission authority.
    /// </remarks>
    internal static async ValueTask<TApplication> StartOwnedAsync<TApplication>(
        EvidenceAdmissionResult admission,
        EvidencePlan plan,
        Func<TApplication> build,
        Action<TApplication> registerOwnership,
        Func<TApplication, CancellationToken, Task> start,
        CancellationToken cancellationToken = default)
        where TApplication : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(registerOwnership);
        ArgumentNullException.ThrowIfNull(start);
        admission.ValidateActive(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var application = build();
        ArgumentNullException.ThrowIfNull(application);
        registerOwnership(application);
        cancellationToken.ThrowIfCancellationRequested();
        admission.ValidateActive(plan);
        await start(application, cancellationToken).ConfigureAwait(false);
        admission.ValidateActive(plan);
        return application;
    }

    /// <summary>
    /// Creates a readiness adapter that waits for the named Aspire resource's healthy state.
    /// </summary>
    /// <param name="evidenceResourceId">Declared EvidenceHost resource identifier.</param>
    /// <param name="aspireResourceName">Consumer-owned Aspire resource name.</param>
    /// <returns>An explicitly registered health readiness adapter.</returns>
    public IEvidenceResourceReadiness CreateHealthReadiness(string evidenceResourceId, string aspireResourceName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireResourceName);
        return new AspireHealthReadiness(evidenceResourceId, aspireResourceName, _application.ResourceNotifications, this);
    }

    /// <summary>
    /// Stops and disposes the owned Aspire application. Repeated calls are safe.
    /// </summary>
    /// <returns>A task that completes after bounded application cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var stop = new CancellationTokenSource(StopTimeout);
        try
        {
            await _application.StopAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class AspireHealthReadiness(
        string id,
        string resourceName,
        global::Aspire.Hosting.ApplicationModel.ResourceNotificationService notifications,
        EvidenceAspireApplication application) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
            notifications.WaitForResourceHealthyAsync(resourceName, cancellationToken);

        public ValueTask DisposeAsync() => application.DisposeAsync();
    }
}
