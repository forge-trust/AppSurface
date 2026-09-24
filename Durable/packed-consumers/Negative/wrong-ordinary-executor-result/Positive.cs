using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<CorrectExecutor>();
sealed record Work;
sealed record Result;
sealed class CorrectExecutor : IDurableWorkerExecutor<Work, Result>
{
    public ValueTask<Result> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Result());
}
