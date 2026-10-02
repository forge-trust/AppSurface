using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0])
    || (args[1] is not ("connect" or "hold" or "collect" or "cancel-connect") && !IsApplicationMode(args[1])))
{
    return 64;
}

using var callerCancellation = args[1] is "cancel-connect" or "application-cancel-start" or "application-cancel-wait"
    ? new CancellationTokenSource()
    : null;
using var cancellationSchedulerStop = new CancellationTokenSource();
var cancellationScheduler = callerCancellation is null
    ? null
    : ArmCallerCancellationAfterMarkerAsync(args[0], args[1] == "cancel-connect", callerCancellation,
        cancellationSchedulerStop.Token);

try
{
    var supervisor = await EvidenceLinuxWorkerSupervisor.ConnectAsync(
        args[0], callerCancellation?.Token ?? CancellationToken.None);
    if (IsApplicationMode(args[1]))
    {
        return await RunApplicationControlAsync(supervisor, args[1], callerCancellation?.Token ?? CancellationToken.None);
    }

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
    // Keep fixture output value-free; no raw acknowledgement or exception supplies authority.
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

static bool IsApplicationMode(string mode) => mode is "application-valid" or "application-duplicate-start"
    or "application-wait-before-start" or "application-wrong-lease" or "application-wrong-resource"
    or "application-closed-start" or "application-closed-wait" or "application-wrong-id"
    or "application-wrong-digest" or "application-start-malformed" or "application-start-uid"
    or "application-start-lease" or "application-start-unowned" or "application-start-cgroup"
    or "application-wait-malformed" or "application-wait-uid" or "application-wait-kernel"
    or "application-wait-unhealthy" or "application-wait-overflow" or "application-cancel-start"
    or "application-cancel-wait" or "application-hold-start" or "application-hold-wait";

static async Task<int> RunApplicationControlAsync(EvidenceLinuxWorkerSupervisor supervisor, string mode,
    CancellationToken cancellationToken)
{
    const string applicationId = "protocol-app";
    const string resourceId = "native-http";
    var digest = new string('d', 64);
    var leaseId = new string('a', 32);
    var application = supervisor.Descriptor.Application ?? throw new InvalidOperationException();
    EvidenceLinuxApplicationStartReceipt? started = null;
    EvidenceLinuxApplicationResourceReceipt? ready = null;

    if (mode is "application-hold-start" or "application-hold-wait")
    {
        if (mode == "application-hold-wait")
            started = await supervisor.StartApplicationAsync(applicationId, digest, cancellationToken);
        WriteResult(new { status = "application-held", start_ack = started is not null });
        if (Console.ReadLine() != "operate") return 64;
        if (started is null)
            _ = await supervisor.StartApplicationAsync(applicationId, digest, cancellationToken);
        else
            _ = await supervisor.WaitForApplicationResourceAsync(started.LeaseId, resourceId, cancellationToken);
        throw new InvalidOperationException();
    }

    var outcome = "acknowledged";
    string? code = null;
    try
    {
        if (mode == "application-wait-before-start")
            ready = await supervisor.WaitForApplicationResourceAsync(leaseId, resourceId, cancellationToken);
        else
        {
            if (mode == "application-closed-start") supervisor.CloseAdmission();
            started = await supervisor.StartApplicationAsync(mode == "application-wrong-id" ? "unknown-app" : applicationId,
                mode == "application-wrong-digest" ? new string('f', 64) : digest, cancellationToken);
            if (started.LeaseId != leaseId || started.AppHostPid != 12345
                || started.ApplicationUid != application.ApplicationUid || started.ApplicationGid != application.ApplicationGid
                || started.Cgroup != $"/system.slice/issue779-app-{leaseId}.service")
                throw new InvalidOperationException();
            if (mode == "application-duplicate-start")
                _ = await supervisor.StartApplicationAsync(applicationId, digest, cancellationToken);
            else
            {
                if (mode == "application-closed-wait") supervisor.CloseAdmission();
                ready = await supervisor.WaitForApplicationResourceAsync(
                    mode == "application-wrong-lease" ? new string('b', 32) : started.LeaseId,
                    mode == "application-wrong-resource" ? "unknown-resource" : resourceId, cancellationToken);
                if (ready.LeaseId != leaseId || ready.ResourceId != resourceId || ready.ApplicationUid != application.ApplicationUid
                    || ready.Cgroup != started.Cgroup || ready.HttpStatus != 200 || ready.ReceivedBytes != 4096)
                    throw new InvalidOperationException();
            }
        }
    }
    catch (EvidenceAdmissionException error)
    {
        outcome = "rejected";
        code = error.Code;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        outcome = "cancelled";
    }

    // The blocked operation's cancelled token must never be reused for stop/wait.
    await supervisor.RequestStopAsync(CancellationToken.None);
    await supervisor.WaitForOwnedExitAsync(CancellationToken.None);
    WriteResult(new { status = "application-control", outcome, code, start_ack = started is not null,
        readiness_ack = ready is not null, cleanup_ack = true });
    return 0;
}

static async Task ArmCallerCancellationAfterMarkerAsync(string socketPath, bool connecting,
    CancellationTokenSource callerCancellation, CancellationToken stopToken)
{
    var readyMarker = socketPath + (connecting ? ".ready-seen" : ".operation-seen");
    try
    {
        while (!File.Exists(readyMarker))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), stopToken);
        }

        if (connecting) callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
        else callerCancellation.Cancel();
    }
    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
    {
    }
}
