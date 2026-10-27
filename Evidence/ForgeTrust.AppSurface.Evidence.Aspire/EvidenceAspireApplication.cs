using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly EvidenceAspireApplicationLifetime _lifetime;

    private EvidenceAspireApplication(DistributedApplication application)
    {
        _application = application;
        _lifetime = new EvidenceAspireApplicationLifetime(DisposeApplicationAsync);
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
    /// <remarks>Dispose every returned adapter. It retains one application registration; only the final adapter release stops this application.</remarks>
    public IEvidenceResourceReadiness CreateHealthReadiness(string evidenceResourceId, string aspireResourceName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireResourceName);
        var notifications = _application.ResourceNotifications;
        return new AspireHealthReadiness(evidenceResourceId, aspireResourceName, notifications, _lifetime.Acquire());
    }

    /// <summary>
    /// Creates an EvidenceHost readiness adapter that waits for native Aspire health and then probes one named HTTP endpoint.
    /// </summary>
    /// <param name="evidenceResourceId">Declared EvidenceHost resource identifier.</param>
    /// <param name="resourceName">Exact consumer-owned Aspire resource name.</param>
    /// <param name="endpointName">Exact named endpoint on the resource.</param>
    /// <param name="probePath">Relative or root-relative HTTP path checked after health and endpoint allocation.</param>
    /// <param name="timeout">One finite positive budget shared by health, endpoint allocation, HTTP requests, and retry delays.</param>
    /// <param name="options">Optional immutable-at-call-time HTTP and owned-client settings.</param>
    /// <returns>A disposable readiness adapter that owns one fresh probe client per active wait and joins active waits before releasing its application registration.</returns>
    /// <remarks>
    /// The adapter resolves the exact resource and endpoint from the running application's
    /// <see cref="DistributedApplicationModel"/> using Aspire.Hosting APIs. It does not require
    /// Aspire.Hosting.Testing. A custom client factory must return a fresh client for each wait;
    /// the adapter disposes each returned client after its probe completes.
    /// Each adapter retains this application until disposed. Only the last adapter release stops
    /// the application, so disposing one adapter cannot interrupt another adapter's admitted waits.
    /// </remarks>
    public IEvidenceResourceReadiness CreateHttpReadiness(
        string evidenceResourceId,
        string resourceName,
        string endpointName,
        string probePath,
        TimeSpan timeout,
        EvidenceAspireHttpReadinessOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        HttpReadinessProbe.ValidateProbePath(probePath);
        ReadinessDeadline.ValidateDuration(timeout, nameof(timeout));

        var snapshot = (options ?? new EvidenceAspireHttpReadinessOptions()).Snapshot();
        var notifications = _application.ResourceNotifications;
        var model = _application.Services.GetRequiredService<DistributedApplicationModel>();
        var endpointReference = ResolveEndpointReference(model, resourceName, endpointName);
        Func<Uri, HttpClient> createClient = snapshot.CreateProbeClient is { } factory
            ? endpoint => factory(endpoint)
            : static _ => new HttpClient();

        var registration = _lifetime.Acquire();
        return CreateHttpReadinessCore(
            evidenceResourceId,
            probePath,
            timeout,
            snapshot.HttpSettings,
            token => notifications.WaitForResourceHealthyAsync(resourceName, token),
            token => ResolveEndpointValueAsync(endpointReference, token),
            createClient,
            registration.DisposeAsync,
            TimeProvider.System);
    }

    /// <summary>Creates the adapter with controlled health, endpoint, transport, clock, and lease phases.</summary>
    /// <param name="evidenceResourceId">The EvidenceHost resource identity assigned to the adapter.</param>
    /// <param name="probePath">The relative or root-relative path validated by the neutral HTTP probe.</param>
    /// <param name="timeout">The one finite positive adapter budget, including health and HTTP work.</param>
    /// <param name="settings">The immutable validated HTTP policy snapshot shared by admitted waits.</param>
    /// <param name="waitForHealth">The native-health phase, invoked before endpoint allocation.</param>
    /// <param name="resolveEndpoint">The endpoint-value phase, invoked only after native health succeeds.</param>
    /// <param name="createClient">Creates one fresh adapter-owned client for each admitted wait.</param>
    /// <param name="disposeLease">Disposes the existing application owner after all waits and clients drain.</param>
    /// <param name="timeProvider">The monotonic clock and timer provider used for deterministic budget tests.</param>
    /// <returns>A disposable readiness adapter with atomic wait admission and joined owner cleanup.</returns>
    internal static IEvidenceResourceReadiness CreateHttpReadinessCore(
        string evidenceResourceId,
        string probePath,
        TimeSpan timeout,
        HttpReadinessSettings settings,
        Func<CancellationToken, Task> waitForHealth,
        Func<CancellationToken, Task<string?>> resolveEndpoint,
        Func<Uri, HttpClient> createClient,
        Func<ValueTask> disposeLease,
        TimeProvider timeProvider) =>
        new EvidenceAspireHttpReadiness(
            evidenceResourceId,
            probePath,
            timeout,
            settings,
            waitForHealth,
            resolveEndpoint,
            createClient,
            disposeLease,
            timeProvider);

    /// <summary>Resolves a unique named endpoint shape without allocating its value.</summary>
    /// <param name="model">The host-local Aspire model obtained from the running application's services.</param>
    /// <param name="resourceName">The exact resource name to select.</param>
    /// <param name="endpointName">The exact endpoint name to select.</param>
    /// <returns>The exact HTTP(S) Aspire endpoint reference; value allocation remains deferred until after health.</returns>
    /// <exception cref="ReadinessException">The model has no unique matching resource, endpoint, or HTTP(S) endpoint shape.</exception>
    internal static EndpointReference ResolveEndpointReference(
        DistributedApplicationModel model,
        string resourceName,
        string endpointName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        var matches = model.Resources
            .Where(resource => string.Equals(resource.Name, resourceName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1 || matches[0] is not IResourceWithEndpoints resourceWithEndpoints)
        {
            throw EndpointUnavailable();
        }

        var matchingEndpoints = ResourceExtensions.GetEndpoints(resourceWithEndpoints)
            .Where(endpoint => string.Equals(endpoint.EndpointName, endpointName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingEndpoints.Length != 1)
        {
            throw EndpointUnavailable();
        }

        var endpointReference = ResourceExtensions.GetEndpoint(resourceWithEndpoints, endpointName);
        if (!endpointReference.Exists || (!endpointReference.IsHttp && !endpointReference.IsHttps))
        {
            throw EndpointUnavailable();
        }

        return endpointReference;
    }

    /// <summary>Allocates the already validated exact endpoint after native resource health succeeds.</summary>
    /// <param name="endpoint">The unique host-local endpoint reference selected from the model.</param>
    /// <param name="cancellationToken">The original cooperative caller/deadline/adapter-stop token.</param>
    /// <returns>The allocated absolute endpoint value, or <see langword="null"/> when Aspire returns none.</returns>
    /// <exception cref="ReadinessException">Aspire reports that the selected endpoint cannot be allocated.</exception>
    private static async Task<string?> ResolveEndpointValueAsync(EndpointReference endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!endpoint.Exists || (!endpoint.IsHttp && !endpoint.IsHttps))
        {
            throw EndpointUnavailable();
        }

        try
        {
            return await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw EndpointUnavailable();
        }
    }

    /// <summary>Creates the fixed, request-data-free endpoint diagnostic.</summary>
    private static ReadinessException EndpointUnavailable() =>
        new(ReadinessPhase.EndpointResolution, ReadinessFailureCategory.EndpointUnavailable);

    /// <summary>
    /// Stops and disposes the owned Aspire application. Repeated calls join the same cleanup result.
    /// </summary>
    /// <returns>A task that completes after bounded application cleanup.</returns>
    /// <remarks>Stop all readiness work before directly disposing this owner. Adapter disposal releases only its own registration.</remarks>
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    private async ValueTask DisposeApplicationAsync()
    {
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
        _lifetime.ThrowIfDisposed();
    }

    private sealed class AspireHealthReadiness(
        string id,
        string resourceName,
        global::Aspire.Hosting.ApplicationModel.ResourceNotificationService notifications,
        IAsyncDisposable registration) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
            notifications.WaitForResourceHealthyAsync(resourceName, cancellationToken);

        public ValueTask DisposeAsync() => registration.DisposeAsync();
    }
}
