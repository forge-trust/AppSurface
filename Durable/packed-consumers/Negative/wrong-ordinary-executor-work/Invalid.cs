using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<WrongExecutor>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record OtherWork;
sealed record Result;
sealed class WrongExecutor : IDurableWorkerExecutor<OtherWork, Result>
{
    public ValueTask<Result> ExecuteAsync(DurableWorkerEnvelope<OtherWork> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Result());
}
