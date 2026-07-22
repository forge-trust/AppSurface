using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Adapts a composed AppSurface profile to Aspire's configurable testing-builder contract.
/// </summary>
/// <remarks>
/// Customize this builder before its single <see cref="BuildAsync(CancellationToken)"/> call. After a successful build,
/// all builder access and a second build are rejected. Dispose the
/// returned <see cref="DistributedApplication"/> before disposing this builder so stop and disposal failures remain at
/// the application call site. As a fallback, builder disposal disposes the application before profile activation
/// services. After a process-fatal build failure, builder disposal also makes a best effort to release any captured
/// partial Aspire provider. Both disposal methods are idempotent; disposal during a build is rejected.
/// </remarks>
public sealed class AppSurfaceAspireProfileTestingBuilder : IDistributedApplicationTestingBuilder
{
    private const int Active = 0;
    private const int Building = 1;
    private const int Built = 2;
    private const int Faulted = 3;
    private const int Disposing = 4;
    private const int Disposed = 5;

    private IDistributedApplicationBuilder? _innerBuilder;
    private DistributedApplication? _application;
    private IAsyncDisposable? _activation;
    private AspireBuildServiceProviderLease? _failedBuildServiceProviderLease;
    private readonly Action<string> _warningSink;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    /// <summary>
    /// Initializes a testing builder around the composed Aspire builder and its profile activation lifetime.
    /// </summary>
    /// <param name="innerBuilder">The Aspire builder to expose and build exactly once.</param>
    /// <param name="activation">The profile activation lifetime owned by this builder.</param>
    /// <param name="warningSink">
    /// An optional best-effort destination for compatibility and cleanup warnings. Non-process-fatal sink failures are
    /// suppressed.
    /// </param>
    internal AppSurfaceAspireProfileTestingBuilder(
        IDistributedApplicationBuilder innerBuilder,
        IAsyncDisposable activation,
        Action<string>? warningSink = null)
    {
        _innerBuilder = innerBuilder;
        _activation = activation;
        _warningSink = warningSink ?? AspireTestingDiagnostics.TraceWarning;
    }

    /// <inheritdoc />
    public ConfigurationManager Configuration => GetInnerForAccess().Configuration;

    /// <inheritdoc />
    public string AppHostDirectory => GetInnerForAccess().AppHostDirectory;

    /// <inheritdoc />
    public Assembly AppHostAssembly => GetInnerForAccess().AppHostAssembly ??
        throw new InvalidOperationException("Aspire did not expose the pinned AppHost assembly.");

    /// <inheritdoc />
    public IHostEnvironment Environment => GetInnerForAccess().Environment;

    /// <inheritdoc />
    public IServiceCollection Services => GetInnerForAccess().Services;

    /// <inheritdoc />
    public DistributedApplicationExecutionContext ExecutionContext => GetInnerForAccess().ExecutionContext;

    /// <inheritdoc />
    public IDistributedApplicationEventing Eventing => GetInnerForAccess().Eventing;

    /// <inheritdoc />
    public IDistributedApplicationPipeline Pipeline => GetInnerForAccess().Pipeline;

    /// <inheritdoc />
    public IResourceCollection Resources => GetInnerForAccess().Resources;

    /// <inheritdoc />
    public IFileSystemService FileSystemService => GetInnerForAccess().FileSystemService;

    /// <inheritdoc />
    public IUserSecretsManager UserSecretsManager => GetInnerForAccess().UserSecretsManager;

    /// <inheritdoc />
    public IResourceBuilder<T> AddResource<T>(T resource)
        where T : IResource
    {
        EnsureMutable();
        return GetInnerForAccess().AddResource(resource);
    }

    /// <inheritdoc />
    public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
        where T : IResource
    {
        EnsureMutable();
        return GetInnerForAccess().CreateResourceBuilder(resource);
    }

    /// <summary>
    /// Builds the composed distributed application exactly once.
    /// </summary>
    /// <remarks>
    /// Immediately before delegating to Aspire, this method attempts to install an internal ownership lease
    /// around Aspire's root-provider host factory. A successful build transfers provider ownership to the returned
    /// application. A non-process-fatal host-construction failure disposes the captured partial provider before profile
    /// activation services, without replacing the original failure with non-fatal cleanup errors. A process-fatal
    /// failure retains the captured provider for best-effort cleanup when this builder is later disposed. A
    /// consumer-selected Aspire version with an unfamiliar host registration continues without this additional cleanup
    /// and emits a trace warning.
    /// </remarks>
    /// <param name="cancellationToken">A token checked before and immediately after Aspire's synchronous build.</param>
    /// <returns>The built application. The caller controls starting, stopping, and primary disposal.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder is already building, built, faulted, or disposing.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The builder has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled; any unreturned application is disposed.</exception>
    public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var previousState = Interlocked.CompareExchange(ref _state, Building, Active);
        if (previousState != Active)
        {
            ThrowForBuildState(previousState);
        }

        DistributedApplication? application = null;
        AspireBuildServiceProviderLease? buildServiceProviderLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inner = _innerBuilder!;
            buildServiceProviderLease = AspireBuildServiceProviderLease.TryInstall(
                inner.Services,
                _warningSink);
            application = inner.Build();
            buildServiceProviderLease?.Release();
            cancellationToken.ThrowIfCancellationRequested();
            _application = application;
            Volatile.Write(ref _state, Built);
            return application;
        }
        catch (Exception primaryException) when (!AspireExceptionUtilities.IsProcessFatal(primaryException))
        {
            try
            {
                if (application is not null)
                {
                    try
                    {
                        await application.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
                    {
                        // Non-fatal cleanup must not replace the build or cancellation failure.
                    }
                }
                else if (buildServiceProviderLease is not null)
                {
                    try
                    {
                        await buildServiceProviderLease.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
                    {
                        // Non-fatal cleanup must not replace the build failure.
                        AspireTestingDiagnostics.TryWrite(
                            _warningSink,
                            () => "Aspire partial-provider cleanup failed while preserving the primary build failure. " +
                            $"Cleanup exception type: {cleanupException.GetType().FullName}.");
                    }
                }

                await DisposeActivationAfterFailureAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _state, Faulted);
            }

            throw;
        }
        finally
        {
            if (Volatile.Read(ref _state) == Building)
            {
                _failedBuildServiceProviderLease = buildServiceProviderLease;
            }

            Interlocked.CompareExchange(ref _state, Faulted, Building);
        }
    }

    DistributedApplication IDistributedApplicationBuilder.Build() =>
        BuildAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Releases the profile activation host synchronously.
    /// </summary>
    /// <remarks>
    /// After a successful build, this method disposes the application before profile activation services. After a
    /// process-fatal build failure, it instead attempts to dispose the captured partial provider before activation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A build is in progress.</exception>
    public void Dispose()
    {
        var activation = BeginDispose(out var ownsDisposal);
        if (!ownsDisposal)
        {
            _disposeCompletion.Task.GetAwaiter().GetResult();
            return;
        }

        Exception? primaryException = null;
        try
        {
            try
            {
                var application = Interlocked.Exchange(ref _application, null);
                if (application is not null)
                {
                    application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                primaryException = ex;
            }

            try
            {
                var failedBuildServiceProviderLease = Interlocked.Exchange(
                    ref _failedBuildServiceProviderLease,
                    null);
                if (failedBuildServiceProviderLease is not null)
                {
                    failedBuildServiceProviderLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                primaryException ??= ex;
            }

            try
            {
                if (activation is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else if (activation is not null)
                {
                    activation.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                primaryException ??= ex;
            }
        }
        finally
        {
            CompleteDispose(primaryException);
        }

        _disposeCompletion.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Releases the profile activation host asynchronously.
    /// </summary>
    /// <remarks>
    /// After a successful build, this method disposes the application before profile activation services. After a
    /// process-fatal build failure, it instead attempts to dispose the captured partial provider before activation.
    /// </remarks>
    /// <returns>A value task that completes after activation services are disposed.</returns>
    /// <exception cref="InvalidOperationException">A build is in progress.</exception>
    public async ValueTask DisposeAsync()
    {
        var activation = BeginDispose(out var ownsDisposal);
        if (!ownsDisposal)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? primaryException = null;
        try
        {
            try
            {
                var application = Interlocked.Exchange(ref _application, null);
                if (application is not null)
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                primaryException = ex;
            }

            try
            {
                var failedBuildServiceProviderLease = Interlocked.Exchange(
                    ref _failedBuildServiceProviderLease,
                    null);
                if (failedBuildServiceProviderLease is not null)
                {
                    await failedBuildServiceProviderLease.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                primaryException ??= ex;
            }

            try
            {
                if (activation is not null)
                {
                    await activation.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                primaryException ??= ex;
            }
        }
        finally
        {
            CompleteDispose(primaryException);
        }

        await _disposeCompletion.Task.ConfigureAwait(false);
    }

    private IDistributedApplicationBuilder GetInnerForAccess()
    {
        var state = Volatile.Read(ref _state);
        if (state is Disposing or Disposed)
        {
            throw new ObjectDisposedException(nameof(AppSurfaceAspireProfileTestingBuilder));
        }

        if (state == Faulted)
        {
            throw new InvalidOperationException("The Aspire testing builder is faulted and can only be disposed.");
        }

        if (state == Built)
        {
            throw new InvalidOperationException(
                "The Aspire testing builder is no longer accessible after its single successful BuildAsync call.");
        }

        if (state == Building)
        {
            throw new InvalidOperationException(
                "The Aspire testing builder is inaccessible while its single BuildAsync call is in progress.");
        }

        return _innerBuilder!;
    }

    private void EnsureMutable()
    {
        var state = Volatile.Read(ref _state);
        if (state is Disposing or Disposed)
        {
            throw new ObjectDisposedException(nameof(AppSurfaceAspireProfileTestingBuilder));
        }

        if (state != Active)
        {
            throw new InvalidOperationException("Aspire resources can only be changed before the single BuildAsync call.");
        }
    }

    private IAsyncDisposable? BeginDispose(out bool ownsDisposal)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is Disposing or Disposed)
            {
                ownsDisposal = false;
                return null;
            }

            if (state == Building)
            {
                throw new InvalidOperationException("Cannot dispose the Aspire testing builder while BuildAsync is in progress.");
            }

            if (Interlocked.CompareExchange(ref _state, Disposing, state) == state)
            {
                ownsDisposal = true;
                var activation = Interlocked.Exchange(ref _activation, null);
                return activation;
            }
        }
    }

    private void CompleteDispose(Exception? exception = null)
    {
        Interlocked.Exchange(ref _innerBuilder, null);
        Volatile.Write(ref _state, Disposed);
        if (exception is null)
        {
            _disposeCompletion.TrySetResult();
        }
        else
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    private async ValueTask DisposeActivationAfterFailureAsync()
    {
        _innerBuilder = null;
        var activation = Interlocked.Exchange(ref _activation, null);
        if (activation is not null)
        {
            try
            {
                await activation.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
            {
                // A cleanup failure must not replace the build or cancellation failure.
            }
        }
    }

    private static void ThrowForBuildState(int state)
    {
        if (state == Disposed)
        {
            throw new ObjectDisposedException(nameof(AppSurfaceAspireProfileTestingBuilder));
        }

        throw new InvalidOperationException(
            "The Aspire testing builder supports exactly one BuildAsync call and cannot build after failure or disposal.");
    }
}
