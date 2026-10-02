using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0])
    || args[1] is not ("connect" or "hold" or "collect" or "cancel-connect"))
{
    return 64;
}

using var callerCancellation = args[1] == "cancel-connect"
    ? new CancellationTokenSource()
    : null;
using var cancellationSchedulerStop = new CancellationTokenSource();
var cancellationScheduler = callerCancellation is null
    ? null
    : ArmCallerCancellationAfterReadyAsync(args[0], callerCancellation, cancellationSchedulerStop.Token);

try
{
    var supervisor = await EvidenceLinuxWorkerSupervisor.ConnectAsync(
        args[0], callerCancellation?.Token ?? CancellationToken.None);
    if (args[1] == "hold")
    {
        WriteResult(new { status = "connected", armed = supervisor.IsArmed });
        if (Console.ReadLine() == "stop")
        {
            await supervisor.RequestStopAsync(CancellationToken.None);
            WriteResult(new { status = "stopped" });
        }

        return 0;
    }

    if (args[1] == "collect")
    {
        var artifacts = await supervisor.CollectArtifactsAsync("reports", CancellationToken.None);
        WriteResult(new
        {
            status = "collected",
            count = artifacts.Count,
            bytes = artifacts.Sum(static artifact => artifact.Contents.Length),
        });
        return 0;
    }

    WriteResult(new { status = "connected", armed = supervisor.IsArmed });
    return 0;
}
catch (OperationCanceledException) when (args[1] == "cancel-connect")
{
    WriteResult(new { status = "cancelled" });
    return 22;
}
catch (EvidenceAdmissionException exception)
{
    WriteResult(new { status = "rejected", code = exception.Code });
    return 20;
}
catch (Exception)
{
    // Direct internal parser failures may be JsonException/KeyNotFoundException. Keep this
    // fixture output value-free; production CLI normalization owns user-facing diagnostics.
    WriteResult(new { status = "protocol-error" });
    return 21;
}
finally
{
    cancellationSchedulerStop.Cancel();
    if (cancellationScheduler is not null)
    {
        await cancellationScheduler;
    }
}

static void WriteResult<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value));

static async Task ArmCallerCancellationAfterReadyAsync(string socketPath,
    CancellationTokenSource callerCancellation, CancellationToken stopToken)
{
    var readyMarker = socketPath + ".ready-seen";
    try
    {
        while (!File.Exists(readyMarker))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), stopToken);
        }

        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
    }
    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
    {
    }
}
