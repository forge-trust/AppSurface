using System.Runtime.ExceptionServices;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>Owns one typed Aspire testing builder through native health and HTTP readiness.</summary>
/// <remarks>
/// Use <see cref="StartAsync(Func{CancellationToken, Task{AppSurfaceAspireProfileTestingBuilder}}, AspireHttpReadinessTarget, AppSurfaceAspireStartupOptions?, CancellationToken)"/>
/// when both native resource health and an explicit HTTP condition must pass. The builder factory runs once under one
/// startup deadline and must return an exclusive active, unbuilt builder. The fixture owns and disposes that builder;
/// <see cref="Application"/> and <see cref="Endpoint"/> are borrowed values. Do not independently build or dispose the
/// application. Builder cleanup remains its single lifecycle owner and disposes the application before activation services.
/// The deadline covers cooperative asynchronous operations; synchronous creation/build and awaited cleanup can outlast it.
/// </remarks>
public sealed class AppSurfaceAspireTestFixture : IAsyncDisposable
{
    private readonly Func<ValueTask> _cleanup;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncLocal<bool> _insideCleanup = new();
    private int _disposeStarted;

    private AppSurfaceAspireTestFixture(
        DistributedApplication application,
        Uri endpoint,
        Func<ValueTask> cleanup)
    {
        Application = application;
        Endpoint = endpoint;
        _cleanup = cleanup;
    }

    /// <summary>Gets the application borrowed from the fixture's owned builder.</summary>
    public DistributedApplication Application { get; }

    /// <summary>Gets the selected absolute HTTP(S) endpoint after both readiness conditions have passed.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Creates the typed builder once, builds and starts its application, waits for native resource health, resolves the
    /// named endpoint, and waits for the configured HTTP status condition under one startup deadline.
    /// </summary>
    /// <param name="createBuilder">A one-shot factory returning an exclusive active, unbuilt typed builder.</param>
    /// <param name="target">The exact resource, endpoint, and status-probe path.</param>
    /// <param name="options">Optional startup and HTTP settings, snapshotted before invoking the factory.</param>
    /// <param name="cancellationToken">The original caller token; caller cancellation takes precedence over deadline expiry.</param>
    /// <returns>A fixture that owns the builder and exposes its borrowed application and selected endpoint.</returns>
    /// <exception cref="ArgumentNullException">A required argument or the HTTP predicate is null.</exception>
    /// <exception cref="ArgumentException">A resource name, endpoint name, probe path, or endpoint is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration or attempt count is outside its supported range.</exception>
    /// <exception cref="InvalidOperationException">The factory returns a builder that is not active and unbuilt.</exception>
    /// <exception cref="ReadinessException">A generated deadline, endpoint, HTTP, or cleanup condition fails.</exception>
    /// <exception cref="OperationCanceledException">The original caller token is cancelled.</exception>
    /// <remarks>
    /// A factory that allocates resources but fails to return a builder remains responsible for those resources. Once a
    /// builder is returned, the fixture takes ownership before checking cancellation or deadline again and cleans it on
    /// every unsuccessful startup path unless process-fatal cleanup interrupts it. Original factory/build/start exceptions
    /// remain primary if nonfatal cleanup also fails; process-fatal cleanup exceptions propagate unchanged.
    /// For nonfatal cleanup failures, <see cref="Exception.Data"/> may contain only the fixed key
    /// <c>AppSurface.Readiness.CleanupFailure</c> with value <see cref="ReadinessFailureCategory.CleanupFailure"/>.
    /// </remarks>
    public static Task<AppSurfaceAspireTestFixture> StartAsync(
        Func<CancellationToken, Task<AppSurfaceAspireProfileTestingBuilder>> createBuilder,
        AspireHttpReadinessTarget target,
        AppSurfaceAspireStartupOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StartAsync(createBuilder, target, options, cancellationToken, TimeProvider.System, FixtureOperations.Native);

    /// <summary>Clock and operation seam for deterministic lifecycle tests.</summary>
    internal static async Task<AppSurfaceAspireTestFixture> StartAsync(
        Func<CancellationToken, Task<AppSurfaceAspireProfileTestingBuilder>> createBuilder,
        AspireHttpReadinessTarget target,
        AppSurfaceAspireStartupOptions? options,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        FixtureOperations operations)
    {
        ArgumentNullException.ThrowIfNull(createBuilder);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.EndpointName);
        HttpReadinessProbe.ValidateProbePath(target.ProbePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(operations);

        options ??= new AppSurfaceAspireStartupOptions();
        ReadinessDeadline.ValidateDuration(options.StartupTimeout, nameof(options.StartupTimeout));
        var startupTimeout = options.StartupTimeout;
        var httpSettings = HttpReadinessSettings.Snapshot(options.Http);
        var createProbeClient = options.CreateProbeClient;

        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new ReadinessDeadline(startupTimeout, timeProvider, cancellationToken);

        AppSurfaceAspireProfileTestingBuilder? builder = null;
        HttpClient? ownedProbeClient = null;
        var ownershipTransferred = false;
        var phase = ReadinessPhase.Creation;
        try
        {
            deadline.Check(phase);
            builder = await createBuilder(deadline.Token).ConfigureAwait(false);
            if (builder is null)
            {
                throw new InvalidOperationException("The typed builder factory returned null.");
            }

            ownershipTransferred = true;
            deadline.Check(phase);
            builder.EnsureActiveUnbuilt();

            phase = ReadinessPhase.Build;
            deadline.Check(phase);
            var application = await operations.BuildAsync(builder, deadline.Token).ConfigureAwait(false);
            if (application is null)
            {
                throw new InvalidOperationException("The typed builder did not return an application.");
            }
            deadline.Check(phase);

            phase = ReadinessPhase.Start;
            deadline.Check(phase);
            await operations.StartAsync(application, deadline.Token).ConfigureAwait(false);
            deadline.Check(phase);

            phase = ReadinessPhase.ResourceHealth;
            deadline.Check(phase);
            await operations.WaitForResourceHealthyAsync(application, target.ResourceName, deadline.Token).ConfigureAwait(false);
            deadline.Check(phase);

            phase = ReadinessPhase.EndpointResolution;
            deadline.Check(phase);
            var endpoint = ResolveEndpoint(application, target, operations);
            deadline.Check(phase);

            phase = ReadinessPhase.Http;
            deadline.Check(phase);
            ownedProbeClient = createProbeClient is null
                ? operations.CreateDefaultProbeClient(application, target.ResourceName, target.EndpointName)
                : createProbeClient(endpoint);
            if (ownedProbeClient is null)
            {
                throw new InvalidOperationException("The probe client factory returned null.");
            }
            deadline.Check(phase);

            var probeClient = ownedProbeClient;
            ownedProbeClient = null;
            await ProbeAndDisposeClientAsync(probeClient, endpoint, target.ProbePath, httpSettings, deadline, operations)
                .ConfigureAwait(false);
            deadline.Check(phase);

            var ownedBuilder = builder;
            var fixture = new AppSurfaceAspireTestFixture(
                application,
                endpoint,
                () => operations.CleanupAsync(ownedBuilder));
            builder = null;
            return fixture;
        }
        catch (Exception exception)
        {
            var primaryException = NormalizeCancellation(exception, deadline, phase);
            if (ownedProbeClient is not null)
            {
                try
                {
                    ownedProbeClient.Dispose();
                }
                catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
                {
                    RecordSafeSecondaryCleanup(primaryException);
                }
            }

            if (ownershipTransferred && builder is not null)
            {
                try
                {
                    await operations.CleanupAsync(builder).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
                {
                    RecordSafeSecondaryCleanup(primaryException);
                }
            }

            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable after rethrowing the readiness startup failure.");
        }
    }

    /// <summary>Joins builder-owned application and activation cleanup; concurrent callers observe the same result.</summary>
    public ValueTask DisposeAsync() => new(DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) == 0)
        {
            _ = CompleteCleanupAsync();
        }

        if (!_insideCleanup.Value)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
        }
    }

    private async Task CompleteCleanupAsync()
    {
        var previous = _insideCleanup.Value;
        _insideCleanup.Value = true;
        try
        {
            await _cleanup().ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
        finally
        {
            _insideCleanup.Value = previous;
        }
    }

    private static Uri ResolveEndpoint(
        DistributedApplication application,
        AspireHttpReadinessTarget target,
        FixtureOperations operations)
    {
        try
        {
            var endpoint = operations.GetEndpoint(application, target.ResourceName, target.EndpointName);
            if (endpoint is null)
            {
                throw new InvalidOperationException("Aspire returned no URI for the selected endpoint.");
            }

            _ = HttpReadinessProbe.ResolveProbeUri(endpoint, target.ProbePath);
            return endpoint;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!AspireExceptionUtilities.IsProcessFatal(exception))
        {
            throw new ReadinessException(ReadinessPhase.EndpointResolution, ReadinessFailureCategory.EndpointUnavailable);
        }
    }

    private static async Task ProbeAndDisposeClientAsync(
        HttpClient client,
        Uri endpoint,
        string probePath,
        HttpReadinessSettings settings,
        ReadinessDeadline deadline,
        FixtureOperations operations)
    {
        Exception? primaryException = null;
        try
        {
            await operations.WaitForHttpAsync(client, endpoint, probePath, settings, deadline).ConfigureAwait(false);
            deadline.Check(ReadinessPhase.Http);
        }
        catch (Exception exception)
        {
            primaryException = NormalizeCancellation(exception, deadline, ReadinessPhase.Http);
        }

        Exception? clientCleanupException = null;
        try
        {
            client.Dispose();
        }
        catch (Exception exception) when (!AspireExceptionUtilities.IsProcessFatal(exception))
        {
            clientCleanupException = exception;
        }

        if (primaryException is null)
        {
            try
            {
                deadline.Check(ReadinessPhase.Http);
            }
            catch (Exception exception)
            {
                primaryException = exception;
            }
        }

        if (clientCleanupException is not null)
        {
            if (primaryException is null)
            {
                primaryException = new ReadinessException(ReadinessPhase.Cleanup, ReadinessFailureCategory.CleanupFailure,
                    cleanupExceptionType: clientCleanupException.GetType());
            }
            else
            {
                RecordSafeSecondaryCleanup(primaryException);
            }
        }

        if (primaryException is not null)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }
    }

    private static Exception NormalizeCancellation(
        Exception exception,
        ReadinessDeadline deadline,
        ReadinessPhase phase)
    {
        if (exception is not OperationCanceledException)
        {
            return exception;
        }

        try
        {
            deadline.Check(phase);
            return exception;
        }
        catch (Exception cancellationOrDeadlineException)
        {
            return cancellationOrDeadlineException;
        }
    }

    private static void RecordSafeSecondaryCleanup(Exception primaryException)
    {
        try
        {
            primaryException.Data["AppSurface.Readiness.CleanupFailure"] = ReadinessFailureCategory.CleanupFailure;
        }
        catch (Exception exception) when (!AspireExceptionUtilities.IsProcessFatal(exception))
        {
            // Nonfatal diagnostic-storage failures cannot replace the primary error.
        }
    }

    /// <summary>Internal operation seam for deterministic start, health, endpoint, probe, and cleanup tests.</summary>
    internal sealed record FixtureOperations
    {
        /// <summary>Gets the production operations backed by the typed builder and Aspire 13.4.4 helpers.</summary>
        internal static FixtureOperations Native { get; } = new();

        /// <summary>Builds the application through the builder's sole lifecycle owner.</summary>
        internal Func<AppSurfaceAspireProfileTestingBuilder, CancellationToken, Task<DistributedApplication>> BuildAsync { get; init; } =
            static (builder, token) => builder.BuildAsync(token);
        /// <summary>Starts the application with the shared cooperative deadline token.</summary>
        internal Func<DistributedApplication, CancellationToken, Task> StartAsync { get; init; } =
            static (application, token) => application.StartAsync(token);
        /// <summary>Waits for the named Aspire resource's native healthy notification.</summary>
        internal Func<DistributedApplication, string, CancellationToken, Task> WaitForResourceHealthyAsync { get; init; } =
            static (application, resourceName, token) => application.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, token);
        /// <summary>Resolves the exact native resource and endpoint name to its allocated URI.</summary>
        internal Func<DistributedApplication, string, string, Uri> GetEndpoint { get; init; } =
            static (application, resourceName, endpointName) => application.GetEndpoint(resourceName, endpointName);
        /// <summary>Creates the native resource-specific Aspire testing client when the caller supplied no factory.</summary>
        internal Func<DistributedApplication, string, string, HttpClient> CreateDefaultProbeClient { get; init; } =
            static (application, resourceName, endpointName) => application.CreateHttpClient(resourceName, endpointName);
        /// <summary>Runs the shared neutral HTTP probe using the original deadline and validated settings.</summary>
        internal Func<HttpClient, Uri, string, HttpReadinessSettings, ReadinessDeadline, Task> WaitForHttpAsync { get; init; } =
            static (client, endpoint, path, settings, deadline) => HttpReadinessProbe.WaitCoreAsync(client, endpoint, path, settings, deadline);
        /// <summary>Disposes the transferred builder once; the builder remains the sole application cleanup owner.</summary>
        internal Func<AppSurfaceAspireProfileTestingBuilder, ValueTask> CleanupAsync { get; init; } =
            static builder => builder.DisposeAsync();
    }
}
