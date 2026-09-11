using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<WrongExecutor>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record Result;
sealed record OtherResult;
sealed class WrongExecutor : IDurableWorkerExecutor<Work, OtherResult>
{
    public ValueTask<OtherResult> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new OtherResult());
}
