using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.ProviderKeyed, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedByExit<WrongExitExecutor>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record OtherWork;
sealed record Result;
sealed class WrongExitExecutor : IDurableWorkExitExecutor<OtherWork, Result>
{
    public ValueTask<DurableWorkExit<Result>> ExecuteAsync(DurableWorkerEnvelope<OtherWork> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableWorkExit<Result>.Succeeded(new Result()));
}
