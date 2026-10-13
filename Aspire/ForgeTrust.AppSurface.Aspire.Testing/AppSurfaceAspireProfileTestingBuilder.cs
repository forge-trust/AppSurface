using System.Reflection;
using System.Runtime.ExceptionServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Adapts a composed AppSurface profile to Aspire's configurable testing-builder contract.
/// </summary>
/// <remarks>
/// The configured factory and profile composition first receive this builder in Preparing state. Graph access and
/// mutations are available then, while build and disposal are rejected until the factory returns. Customize the
/// returned builder before its single <see cref="BuildAsync(CancellationToken)"/> call. After a successful build,
/// direct builder access and a second build are rejected. Configured resource builders retain the native Aspire
/// context for deferred SDK callbacks after the application is built; post-build graph mutation is unsupported.
/// Dispose the
/// returned <see cref="DistributedApplication"/> before disposing this builder so stop and disposal failures remain at
/// the application call site. As a fallback, builder disposal disposes the application before profile activation
/// services. After a process-fatal build failure, builder disposal also makes a best effort to release any captured
/// partial Aspire provider. Both disposal methods are idempotent; concurrent callers join cleanup, while reentrant
/// disposal from that same cleanup flow returns without waiting on itself. Disposal during a build is rejected.
/// </remarks>
public sealed class AppSurfaceAspireProfileTestingBuilder : IDistributedApplicationTestingBuilder
{
    private const int Active = 0;
    private const int Building = 1;
    private const int Built = 2;
    private const int Faulted = 3;
    private const int Disposing = 4;
    private const int Disposed = 5;
    private const int Preparing = 6;

    private IDistributedApplicationBuilder? _innerBuilder;
    private DistributedApplication? _application;
    private IAsyncDisposable? _activation;
    private AspireBuildServiceProviderLease? _failedBuildServiceProviderLease;
    private readonly Action<string> _warningSink;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncLocal<bool> _insideDisposal = new();
    private int _state;
    private readonly bool _configuredCreation;
    private readonly IDisposable? _creationConfiguration;
    private readonly IFileProvider? _creationFileProvider;
    private int _unbuiltResourcesDisposed;
    private bool _aspireResourcesTransferred;

    /// <summary>
    /// Initializes a testing builder around the composed Aspire builder and its profile activation lifetime.
    /// </summary>
    /// <param name="innerBuilder">The Aspire builder to expose and build exactly once.</param>
    /// <param name="activation">The profile activation lifetime owned by this builder.</param>
    /// <param name="isPreparing">
    /// True only for configured factory creation: mutations are permitted but build and disposal are rejected until
    /// the factory completes preparation. Legacy creation defaults to false.
    /// </param>
    /// <param name="warningSink">
    /// An optional best-effort destination for compatibility and cleanup warnings. Non-process-fatal sink failures are
    /// suppressed.
    /// </param>
    internal AppSurfaceAspireProfileTestingBuilder(
        IDistributedApplicationBuilder innerBuilder,
        IAsyncDisposable activation,
        Action<string>? warningSink = null,
        bool isPreparing = false)
    {
        _innerBuilder = innerBuilder;
        _activation = activation;
        _warningSink = warningSink ?? AspireTestingDiagnostics.TraceWarning;
        _configuredCreation = isPreparing;
        _state = isPreparing ? Preparing : Active;
        if (isPreparing)
        {
            _creationConfiguration = innerBuilder.Configuration;
            _creationFileProvider = innerBuilder.Environment.ContentRootFileProvider;
        }
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
        var result = GetInnerForAccess().AddResource(resource);
        return _configuredCreation ? new GuardedResourceBuilder<T>(result, this) : result;
    }

    /// <inheritdoc />
    public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
        where T : IResource
    {
        EnsureMutable();
        var result = GetInnerForAccess().CreateResourceBuilder(resource);
        return _configuredCreation ? new GuardedResourceBuilder<T>(result, this) : result;
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
    /// Factory preparation is unfinished, or the builder is already building, built, faulted, or disposing.
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
            _aspireResourcesTransferred = true;
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
    /// <exception cref="InvalidOperationException">Factory preparation or a build is in progress.</exception>
    public void Dispose()
    {
        var activation = BeginDispose(out var ownsDisposal);
        if (!ownsDisposal)
        {
            if (!_insideDisposal.Value)
            {
                _disposeCompletion.Task.GetAwaiter().GetResult();
            }

            return;
        }

        var previousDisposalFlow = _insideDisposal.Value;
        _insideDisposal.Value = true;
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
                DisposeUnbuiltAspireResourcesAsync().GetAwaiter().GetResult();
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
            _insideDisposal.Value = previousDisposalFlow;
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
    /// <exception cref="InvalidOperationException">Factory preparation or a build is in progress.</exception>
    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(allowPreparation: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Transfers a fully composed configured factory result from Preparing to Active.
    /// </summary>
    /// <remarks>Only the factory may call this; callback-rejected lifecycle operations leave Preparing unchanged.</remarks>
    internal void CompletePreparation()
    {
        if (Interlocked.CompareExchange(ref _state, Active, Preparing) != Preparing)
        {
            throw new InvalidOperationException("Only an unfinished configured factory can complete preparation.");
        }
    }

    /// <summary>
    /// Aborts factory preparation through the internal ownership path and invalidates captured references.
    /// </summary>
    /// <returns>A task joining cleanup of unbuilt Aspire resources before activation services.</returns>
    /// <remarks>The factory preserves its primary error if cleanup fails; public disposal cannot enter this path.</remarks>
    internal Task AbortPreparationAsync() => DisposeCoreAsync(allowPreparation: true);

    private async Task DisposeCoreAsync(bool allowPreparation)
    {
        var activation = BeginDispose(out var ownsDisposal, allowPreparation);
        if (!ownsDisposal)
        {
            if (!_insideDisposal.Value)
            {
                await _disposeCompletion.Task.ConfigureAwait(false);
            }

            return;
        }

        var previousDisposalFlow = _insideDisposal.Value;
        _insideDisposal.Value = true;
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
                await DisposeUnbuiltAspireResourcesAsync().ConfigureAwait(false);
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
            _insideDisposal.Value = previousDisposalFlow;
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

        if (state is not (Active or Preparing))
        {
            throw new InvalidOperationException("Aspire resources can only be changed before the single BuildAsync call.");
        }
    }

    private IAsyncDisposable? BeginDispose(out bool ownsDisposal, bool allowPreparation = false)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is Disposing or Disposed)
            {
                ownsDisposal = false;
                return null;
            }

            if (state == Preparing && !allowPreparation)
            {
                throw new InvalidOperationException("Cannot dispose the Aspire testing builder while factory preparation is in progress.");
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
        try
        {
            await DisposeUnbuiltAspireResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException) when (!AspireExceptionUtilities.IsProcessFatal(cleanupException))
        {
            // Non-fatal cleanup must not replace a build or cancellation failure.
        }

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

    private async Task DisposeUnbuiltAspireResourcesAsync()
    {
        if (!_configuredCreation || _aspireResourcesTransferred ||
            Interlocked.Exchange(ref _unbuiltResourcesDisposed, 1) != 0)
        {
            return;
        }

        var currentFileProvider = _innerBuilder?.Environment.ContentRootFileProvider;
        Exception? firstError = null;
        async Task AttemptAsync(object? resource)
        {
            try
            {
                if (resource is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (resource is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            catch (Exception error)
            {
                firstError ??= error;
            }
        }

        await AttemptAsync(_creationConfiguration).ConfigureAwait(false);
        if (!ReferenceEquals(currentFileProvider, _creationFileProvider))
        {
            await AttemptAsync(currentFileProvider).ConfigureAwait(false);
        }

        await AttemptAsync(_creationFileProvider).ConfigureAwait(false);
        if (firstError is not null)
        {
            ExceptionDispatchInfo.Capture(firstError).Throw();
        }
    }

    private sealed class GuardedResourceBuilder<TResource>(IResourceBuilder<TResource> inner, AppSurfaceAspireProfileTestingBuilder owner)
        : IResourceBuilder<TResource> where TResource : IResource
    {
        // Aspire evaluates deferred SDK callbacks after the native application is built. At that
        // point its native context is needed; preparing, active and terminal references stay guarded.
        public IDistributedApplicationBuilder ApplicationBuilder =>
            Volatile.Read(ref owner._state) == Built ? inner.ApplicationBuilder : owner;
        public TResource Resource => inner.Resource;
        public IResourceBuilder<TResource> WithAnnotation<TAnnotation>(
            TAnnotation annotation,
            ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
            where TAnnotation : IResourceAnnotation
        {
            owner.EnsureMutable();
            inner.WithAnnotation(annotation, behavior);
            return this;
        }
    }

    private static void ThrowForBuildState(int state)
    {
        if (state == Preparing)
        {
            throw new InvalidOperationException("Cannot build the Aspire testing builder while factory preparation is in progress.");
        }

        if (state == Disposed)
        {
            throw new ObjectDisposedException(nameof(AppSurfaceAspireProfileTestingBuilder));
        }

        throw new InvalidOperationException(
            "The Aspire testing builder supports exactly one BuildAsync call and cannot build after failure or disposal.");
    }
}
