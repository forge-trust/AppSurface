using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>
/// Owns one consumer-composed Aspire application used only by an EvidenceHost run.
/// </summary>
/// <remarks>
/// Build the supplied <see cref="IDistributedApplicationBuilder"/> in test or CI composition code,
/// then register readiness adapters from this lease with <see cref="EvidenceHostBootstrap"/>. This
/// type does not inspect the entry assembly and must not be added to normal application startup.
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
    /// Builds and starts an explicitly supplied consumer Aspire application.
    /// </summary>
    /// <param name="builder">Consumer-composed Aspire builder.</param>
    /// <param name="cancellationToken">Cancellation requested while building or starting the application.</param>
    /// <returns>An evidence-owned application lease that stops and disposes the application.</returns>
    public static async Task<EvidenceAspireApplication> StartAsync(
        IDistributedApplicationBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        cancellationToken.ThrowIfCancellationRequested();
        var application = builder.Build();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new EvidenceAspireApplication(application);
        }
        catch
        {
            try
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the primary consumer build/start failure. Cleanup is best effort on a lease that was never returned.
            }

            throw;
        }
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
