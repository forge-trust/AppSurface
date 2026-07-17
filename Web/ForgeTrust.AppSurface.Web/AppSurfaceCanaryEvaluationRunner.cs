using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Runs one transport-neutral named-canary lookup and evaluation in the current service scope.
/// </summary>
internal sealed class AppSurfaceCanaryEvaluationRunner
{
    private readonly AppSurfaceCanaryRegistry _registry;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a scoped runner over the immutable registry and current request service provider.
    /// </summary>
    /// <param name="registry">The registry used for exact ordinal name lookup.</param>
    /// <param name="services">The current scope used to resolve the registered concrete evaluator type.</param>
    internal AppSurfaceCanaryEvaluationRunner(
        AppSurfaceCanaryRegistry registry,
        IServiceProvider services)
    {
        _registry = registry;
        _services = services;
    }

    /// <summary>
    /// Looks up and evaluates one registered name without applying HTTP status mapping.
    /// </summary>
    /// <param name="name">The exact canary name to find.</param>
    /// <param name="marker">The optional opaque marker.</param>
    /// <param name="freshSince">The optional freshness boundary.</param>
    /// <param name="cancellationToken">The evaluation cancellation token.</param>
    /// <returns>A not-found outcome or the evaluator result wrapped as a completed outcome.</returns>
    internal async ValueTask<AppSurfaceCanaryEvaluationOutcome> EvaluateAsync(
        string name,
        string? marker,
        DateTimeOffset? freshSince,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(name, out var descriptor))
        {
            return AppSurfaceCanaryEvaluationOutcome.NotFound;
        }

        return AppSurfaceCanaryEvaluationOutcome.Completed(
            await EvaluateAsync(descriptor, marker, freshSince, cancellationToken));
    }

    /// <summary>Attempts exact ordinal lookup without resolving an evaluator.</summary>
    /// <param name="name">The exact registered name.</param>
    /// <param name="descriptor">The descriptor when found.</param>
    /// <returns><see langword="true"/> when the name is registered; otherwise <see langword="false"/>.</returns>
    internal bool TryGetDescriptor(string name, out AppSurfaceCanaryDescriptor descriptor) =>
        _registry.TryGet(name, out descriptor!);

    /// <summary>
    /// Resolves and invokes the evaluator represented by a known descriptor in the current service scope.
    /// </summary>
    /// <param name="descriptor">The non-null registered descriptor.</param>
    /// <param name="marker">The optional opaque marker.</param>
    /// <param name="freshSince">The optional freshness boundary.</param>
    /// <param name="cancellationToken">The evaluation cancellation token.</param>
    /// <returns>The non-null status-only evaluator result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The evaluator returns <see langword="null"/>.</exception>
    internal async ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryDescriptor descriptor,
        string? marker,
        DateTimeOffset? freshSince,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var evaluator = (IAppSurfaceCanaryEvaluator)_services.GetRequiredService(descriptor.EvaluatorType);
        var context = new AppSurfaceCanaryEvaluationContext(descriptor.Name, marker, freshSince);
        var result = await evaluator.EvaluateAsync(context, cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("A named canary evaluator returned a null result.");
        }

        return result;
    }
}

/// <summary>
/// Represents either an exact-name miss or a completed transport-neutral evaluation.
/// </summary>
internal sealed class AppSurfaceCanaryEvaluationOutcome
{
    private AppSurfaceCanaryEvaluationOutcome(bool found, AppSurfaceCanaryResult? result)
    {
        Found = found;
        Result = result;
    }

    /// <summary>Gets the shared exact-name miss outcome.</summary>
    internal static AppSurfaceCanaryEvaluationOutcome NotFound { get; } = new(false, null);

    /// <summary>Gets a value indicating whether a registered descriptor was found.</summary>
    internal bool Found { get; }

    /// <summary>Gets the completed result, or <see langword="null"/> for <see cref="NotFound"/>.</summary>
    internal AppSurfaceCanaryResult? Result { get; }

    /// <summary>Creates an outcome for a non-null completed evaluator result.</summary>
    /// <param name="result">The completed evaluator result.</param>
    /// <returns>A found outcome containing <paramref name="result"/>.</returns>
    internal static AppSurfaceCanaryEvaluationOutcome Completed(AppSurfaceCanaryResult result) => new(true, result);
}
