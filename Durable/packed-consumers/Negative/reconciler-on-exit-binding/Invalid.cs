using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.ProviderKeyed, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedByExit<CorrectExitExecutor>().ReconciledBy<CorrectReconciler>(); // expected-compiler-error: CS1061
sealed record Work;
sealed record Result;
sealed class CorrectExitExecutor : IDurableWorkExitExecutor<Work, Result>
{
    public ValueTask<DurableWorkExit<Result>> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableWorkExit<Result>.Succeeded(new Result()));
}
sealed class CorrectReconciler : IDurableEffectReconciler<Work, Result>
{
    public ValueTask<DurableEffectReconciliation<Result>> ReconcileAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableEffectReconciliation<Result>.Unknown());
}
