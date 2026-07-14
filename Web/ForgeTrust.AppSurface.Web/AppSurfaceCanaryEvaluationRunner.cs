using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Runs one transport-neutral named-canary lookup and evaluation in the current service scope.
/// </summary>
internal sealed class AppSurfaceCanaryEvaluationRunner
{
    private readonly AppSurfaceCanaryRegistry _registry;
    private readonly IServiceProvider _services;

    internal AppSurfaceCanaryEvaluationRunner(
        AppSurfaceCanaryRegistry registry,
        IServiceProvider services)
    {
        _registry = registry;
        _services = services;
    }

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

    internal bool TryGetDescriptor(string name, out AppSurfaceCanaryDescriptor descriptor) =>
        _registry.TryGet(name, out descriptor!);

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

    internal static AppSurfaceCanaryEvaluationOutcome NotFound { get; } = new(false, null);

    internal bool Found { get; }

    internal AppSurfaceCanaryResult? Result { get; }

    internal static AppSurfaceCanaryEvaluationOutcome Completed(AppSurfaceCanaryResult result) => new(true, result);
}
