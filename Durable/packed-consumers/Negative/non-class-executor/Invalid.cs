using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedBy<StructExecutor>(); // expected-compiler-error: CS0452
sealed record Work;
sealed record Result;
struct StructExecutor : IDurableWorkerExecutor<Work, Result>
{
    public ValueTask<Result> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Result());
}
