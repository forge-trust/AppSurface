using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

internal static class Program
{
    private static readonly object MarkerGate = new();
    private static readonly TimeSpan JobAllowance = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupAllowance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StoppingAllowance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StageDeadline = TimeSpan.FromSeconds(2);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !KnownMode(args[0])) return 64;

        var mode = args[0];
        var outputDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            Mark(outputDirectory, "worker-entered");
            var supervisor = new LifecycleSupervisor(outputDirectory);
            var execution = new EvidenceWorkerExecution(
                supervisor,
                TimeProvider.System,
                JobAllowance,
                CleanupAllowance,
                StoppingAllowance);

            if (!execution.RegisterDisposer(_ => DisposeAsync(outputDirectory, mode))) return 65;

            return await RunAsync(outputDirectory, mode, execution).ConfigureAwait(false);
        }
        finally
        {
            Mark(outputDirectory, "worker-finally");
        }
    }

    private static async Task<int> RunAsync(string outputDirectory, string mode, EvidenceWorkerExecution execution)
    {
        var callbackEntered = NewSignal();
        var stage = execution.ExecuteAsync(
            EvidenceRunStage.Producer,
            StageDeadline,
            token => RunCallbackAsync(outputDirectory, mode, execution, callbackEntered, token)).AsTask();

        await callbackEntered.Task.ConfigureAwait(false);
        var result = await stage.ConfigureAwait(false);
        Mark(outputDirectory, "stage-returned");

        if (mode == "cooperative")
        {
            if (result.Outcome != EvidenceWorkerStageOutcome.TimedOut
                || execution.TerminalCode != EvidenceWorkerTerminalCode.DeadlineExceeded)
            {
                return 70;
            }

            // ExecuteAsync returns only after its callback and tracked work have joined.
            Mark(outputDirectory, "joined");
            if (await execution.StopAndDisposeAsync().ConfigureAwait(false)) return 71;
            if (!execution.CleanupCompleted || !execution.OwnWorkStopped) return 72;

            var collection = await execution.CollectAsync(
                TimeSpan.FromSeconds(1),
                _ => CollectFailureAsync(outputDirectory, execution)).ConfigureAwait(false);
            if (collection.Outcome != EvidenceWorkerStageOutcome.Passed
                || collection.Value is null
                || !execution.CollectionCompleted)
            {
                return 73;
            }

            Mark(outputDirectory, "worker-complete");
            return 2;
        }

        if (mode == "nonsettling-disposer")
        {
            if (result.Outcome != EvidenceWorkerStageOutcome.Passed) return 74;
            _ = await execution.StopAndDisposeAsync().ConfigureAwait(false);
            Mark(outputDirectory, "unexpected-cleanup-return");
            return 75;
        }

        // Fatal scenarios must terminate inside EvidenceWorkerExecution before this point.
        return 76;
    }

    private static ValueTask<bool> RunCallbackAsync(
        string outputDirectory,
        string mode,
        EvidenceWorkerExecution execution,
        TaskCompletionSource callbackEntered,
        CancellationToken cancellationToken)
    {
        Mark(outputDirectory, "callback-entered");
        callbackEntered.TrySetResult();

        return mode switch
        {
            "synchronous-stall" => StallBeforeReturningTask(outputDirectory),
            "ignore-cancellation" => IgnoreCancellationAsync(),
            "blocked-write-pump" => TrackBlockedWriteAndPumpAsync(outputDirectory, execution, cancellationToken),
            "cooperative" => CooperateAsync(outputDirectory, cancellationToken),
            "nonsettling-disposer" => ValueTask.FromResult(true),
            _ => ValueTask.FromException<bool>(new ArgumentOutOfRangeException(nameof(mode))),
        };
    }

    private static ValueTask<bool> StallBeforeReturningTask(string outputDirectory)
    {
        using var barrier = new ManualResetEventSlim();
        barrier.Wait();
        Mark(outputDirectory, "synchronous-stall-released");
        return ValueTask.FromResult(true);
    }

    private static ValueTask<bool> IgnoreCancellationAsync()
    {
        var never = NewSignal<bool>();
        return new ValueTask<bool>(never.Task);
    }

    private static async ValueTask<bool> TrackBlockedWriteAndPumpAsync(
        string outputDirectory,
        EvidenceWorkerExecution execution,
        CancellationToken cancellationToken)
    {
        var writeEntered = NewSignal();
        var pumpEntered = NewSignal();
        var never = NewSignal();

        var write = execution.TrackOwnedWork(async _ =>
        {
            Mark(outputDirectory, "write-entered");
            writeEntered.TrySetResult();
            await never.Task.ConfigureAwait(false);
            Mark(outputDirectory, "write-settled");
        }, cancellationToken);
        var pump = execution.TrackOwnedWork(async _ =>
        {
            Mark(outputDirectory, "pump-entered");
            pumpEntered.TrySetResult();
            await never.Task.ConfigureAwait(false);
            Mark(outputDirectory, "pump-settled");
        }, cancellationToken);

        if (write is null || pump is null) throw new InvalidOperationException("Owned work admission was rejected.");
        await Task.WhenAll(writeEntered.Task, pumpEntered.Task).ConfigureAwait(false);
        Mark(outputDirectory, "stage-callback-completed");
        return true;
    }

    private static async ValueTask<bool> CooperateAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "artifact.bin"), "fixture artifact"u8.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Mark(outputDirectory, "callback-settled");
            return true;
        }
    }

    private static ValueTask DisposeAsync(string outputDirectory, string mode)
    {
        Mark(outputDirectory, "disposer-entered");
        if (mode == "nonsettling-disposer")
        {
            return new ValueTask(NewSignal().Task);
        }

        Mark(outputDirectory, "disposed");
        return ValueTask.CompletedTask;
    }

    private static ValueTask<string> CollectFailureAsync(string outputDirectory, EvidenceWorkerExecution execution)
    {
        if (!execution.CleanupCompleted)
        {
            return ValueTask.FromException<string>(new InvalidOperationException("Collection began before cleanup completed."));
        }

        var artifact = File.ReadAllBytes(Path.Combine(outputDirectory, "artifact.bin"));
        var hash = Convert.ToHexString(SHA256.HashData(artifact));
        File.WriteAllText(Path.Combine(outputDirectory, "artifact.sha256"), hash + Environment.NewLine);
        Mark(outputDirectory, "artifact-hashed");

        var manifestPath = Path.Combine(outputDirectory, "failure-manifest.json");
        var manifest = new
        {
            outcome = "failed",
            terminalCode = execution.TerminalCode.ToString(),
            artifactSha256 = hash,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Mark(outputDirectory, "failure-manifest");
        return ValueTask.FromResult(manifestPath);
    }

    private static bool KnownMode(string mode) => mode is
        "synchronous-stall" or "ignore-cancellation" or "blocked-write-pump" or "nonsettling-disposer" or "cooperative";

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Mark(string outputDirectory, string name)
    {
        lock (MarkerGate)
        {
            File.WriteAllText(Path.Combine(outputDirectory, name + ".marker"), name + Environment.NewLine);
            File.AppendAllText(Path.Combine(outputDirectory, "events.log"), name + Environment.NewLine);
        }
    }

    private sealed class LifecycleSupervisor(string outputDirectory) : IEvidenceExecutionSupervisor
    {
        public bool IsArmed => true;
        public string RunId => "isolated-lifecycle-proof";

        public void CloseAdmission() => Mark(outputDirectory, "admission-closed");

        public ValueTask RequestStopAsync(CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return ValueTask.FromException(new OperationCanceledException(stoppingToken));
            }

            Mark(outputDirectory, "stop-requested");
            return ValueTask.CompletedTask;
        }

        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return ValueTask.FromException(new OperationCanceledException(stoppingToken));
            }

            Mark(outputDirectory, "supervisor-exit-acknowledged");
            return ValueTask.CompletedTask;
        }
    }
}
