using ForgeTrust.AppSurface.Web;

namespace NamedCanaryLab;

/// <summary>Reads existing local proof without triggering the synthetic workflow.</summary>
internal sealed class CanaryLabEvaluator(
    CanaryLabProofStore proofStore,
    CanaryLabSettings settings) : IAppSurfaceCanaryEvaluator
{
    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.Marker) || context.FreshSince is not { } freshSince)
        {
            throw new InvalidOperationException(
                "The named-canary lab evaluator requires marker and freshness registration.");
        }

        if (!proofStore.TryRead(CanaryLabMarkerFingerprint.Create(context.Marker), out var proof))
        {
            return ValueTask.FromResult(CreateResult(
                AppSurfaceCanaryStatus.Pending,
                observedAt: null,
                matchedCount: 0,
                reasonCode: "proof-not-observed",
                summary: "No matching local proof has been observed yet."));
        }

        if (proof.Identity != settings.Identity)
        {
            return ValueTask.FromResult(CreateResult(
                AppSurfaceCanaryStatus.Fail,
                proof.ObservedAt,
                matchedCount: 1,
                reasonCode: "candidate-mismatch",
                summary: "The local proof is bound to a different candidate or environment."));
        }

        if (proof.ObservedAt < freshSince)
        {
            return ValueTask.FromResult(CreateResult(
                AppSurfaceCanaryStatus.Stale,
                proof.ObservedAt,
                matchedCount: 1,
                reasonCode: "proof-stale",
                summary: "Matching local proof predates the requested freshness boundary."));
        }

        return ValueTask.FromResult(proof.Status switch
        {
            AppSurfaceCanaryStatus.Pass => CreateResult(
                AppSurfaceCanaryStatus.Pass,
                proof.ObservedAt,
                matchedCount: 1,
                reasonCode: "proof-observed",
                summary: "Fresh matching local proof was observed."),
            AppSurfaceCanaryStatus.Fail => CreateResult(
                AppSurfaceCanaryStatus.Fail,
                proof.ObservedAt,
                matchedCount: 1,
                reasonCode: "workflow-failed",
                summary: "The local synthetic workflow recorded a safe failure."),
            _ => throw new InvalidOperationException("The named-canary lab store contains an unsupported proof status."),
        });
    }

    private static AppSurfaceCanaryResult CreateResult(
        AppSurfaceCanaryStatus status,
        DateTimeOffset? observedAt,
        int matchedCount,
        string reasonCode,
        string summary) =>
        new(
            status,
            result =>
            {
                result.ObservedAt = observedAt;
                result.MatchedCount = matchedCount;
                result.ReasonCode = reasonCode;
                result.Summary = summary;
            });
}
