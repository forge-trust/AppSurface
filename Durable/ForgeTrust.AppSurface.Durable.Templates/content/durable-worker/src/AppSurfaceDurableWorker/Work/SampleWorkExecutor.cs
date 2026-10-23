using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace AppSurfaceDurableWorker.Work;

/// <summary>Executes the deterministic sample without performing an external side effect.</summary>
public sealed class SampleWorkExecutor : IDurableWorkerExecutor<SampleWork, SampleWorkResult>
{
    /// <inheritdoc />
    public ValueTask<SampleWorkResult> ExecuteAsync(
        DurableWorkerEnvelope<SampleWork> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = work.Payload ?? throw new InvalidOperationException("The persisted sample Work payload is absent.");
        return ValueTask.FromResult(new SampleWorkResult($"processed:{payload.Value}"));
    }
}
