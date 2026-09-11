using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.ReconcileBeforeRetry, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<CorrectExecutor>().ReconciledBy<WrongReconciler>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record OtherWork;
sealed record Result;
sealed class CorrectExecutor : IDurableWorkerExecutor<Work, Result>
{
    public ValueTask<Result> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Result());
}
sealed class WrongReconciler : IDurableEffectReconciler<OtherWork, Result>
{
    public ValueTask<DurableEffectReconciliation<Result>> ReconcileAsync(DurableWorkerEnvelope<OtherWork> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableEffectReconciliation<Result>.Unknown());
}
