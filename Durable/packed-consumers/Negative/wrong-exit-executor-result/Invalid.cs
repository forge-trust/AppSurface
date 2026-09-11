using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
var definition = DurableWork.Define<Work, Result>("fixture.work", "v1", null!, null!, DurableProviderSafety.ProviderKeyed, DurableWorkRetryPolicy.Default);
var binding = definition.ExecutedByExit<WrongExitExecutor>(); // expected-compiler-error: CS0311
sealed record Work;
sealed record Result;
sealed record OtherResult;
sealed class WrongExitExecutor : IDurableWorkExitExecutor<Work, OtherResult>
{
    public ValueTask<DurableWorkExit<OtherResult>> ExecuteAsync(DurableWorkerEnvelope<Work> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(DurableWorkExit<OtherResult>.Succeeded(new OtherResult()));
}
