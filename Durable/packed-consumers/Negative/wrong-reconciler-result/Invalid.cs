using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.ReconcileBeforeRetry, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<CorrectExecutor>().ReconciledBy<WrongReconciler>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record Result;
sealed record OtherResult;
sealed class CorrectExecutor : IDurableWorkerExecutor<Work, Result>
{
    public ValueTask<Result> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Result());
}
sealed class WrongReconciler : IDurableEffectReconciler<Work, OtherResult>
{
    public ValueTask<DurableEffectReconciliation<OtherResult>> ReconcileAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableEffectReconciliation<OtherResult>.Unknown());
}
