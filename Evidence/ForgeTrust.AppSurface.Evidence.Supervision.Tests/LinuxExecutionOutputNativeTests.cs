using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>
/// Exercises the actual Linux pipe factory and shared output ledger used by the execution owner.
/// These observations cover output transport and drain only; they grant no worker, server, or run-settlement proof.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxExecutionOutputNativeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [LinuxRootX64Fact]
    public async Task ActualRootPipesDrainBothStreamsAfterSharedQuotaStopsExecution()
    {
        LinuxOutputPipes? pipes = null;
        FileStream? stdoutWriter = null;
        FileStream? stderrWriter = null;
        Task<SupervisionOutputReceipt>? collection = null;
        Task<SupervisionRunOutputReceipt>? completion = null;
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 3, drainOnQuotaExceeded: true);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopRegistration = budget.StopToken.Register(() => stopped.TrySetResult());
        try
        {
            pipes = LinuxOutputPipes.Create();
            stdoutWriter = DuplicateWriter(pipes.StandardOutput);
            stderrWriter = DuplicateWriter(pipes.StandardError);
            collection = pipes.BeginCollectAsync(default, receivedByteLimit: 3, prefixByteLimit: 2,
                runBudget: budget);

            await stdoutWriter.WriteAsync("stdout"u8.ToArray()).AsTask().WaitAsync(Guard);
            await stderrWriter.WriteAsync("err"u8.ToArray()).AsTask().WaitAsync(Guard);
            await stdoutWriter.FlushAsync().WaitAsync(Guard);
            await stderrWriter.FlushAsync().WaitAsync(Guard);
            await stopped.Task.WaitAsync(Guard);

            stdoutWriter.Dispose();
            stdoutWriter = null;
            stderrWriter.Dispose();
            stderrWriter = null;
            pipes.CloseWriteCopies();

            var pair = await collection.WaitAsync(Guard);
            completion = budget.CompleteAsync();
            var run = await completion.WaitAsync(Guard);
            Assert.True(pair.Stdout.EndOfStream);
            Assert.True(pair.Stderr.EndOfStream);
            Assert.True(pair.QuotaExceeded);
            Assert.Equal(SupervisionOutputFailure.QuotaExceeded, pair.Failure);
            Assert.True(run.Drained);
            Assert.True(run.QuotaExceeded);
            Assert.Equal(SupervisionRunOutputFailure.QuotaExceeded, run.Failure);
            Assert.Equal(1, run.RegisteredPairs);
            Assert.Equal(pair.ReceivedBytes, run.ReceivedBytes);
            Assert.True(run.ReceivedBytes > run.ReceivedByteLimit);
            Assert.True(budget.StopToken.IsCancellationRequested);
        }
        finally
        {
            stdoutWriter?.Dispose();
            stderrWriter?.Dispose();
            if (pipes is not null)
            {
                pipes.CloseWriteCopies();
                if (collection is not null) await collection.WaitAsync(Guard);
                if (completion is null) await budget.CompleteAsync().WaitAsync(Guard);
                await pipes.DisposeAsync().AsTask().WaitAsync(Guard);
            }
        }
    }

    private static FileStream DuplicateWriter(SafeFileHandle borrowed)
    {
        var descriptor = Fcntl(borrowed, 1030, 3); // F_DUPFD_CLOEXEC.
        Assert.True(descriptor >= 0);
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Write, bufferSize: 4096); }
        catch { handle.Dispose(); throw; }
    }

    private sealed class LinuxRootX64FactAttribute : FactAttribute
    {
        public LinuxRootX64FactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || GetEffectiveUid() != 0)
                Skip = "Requires native Linux x64 with effective UID 0; a skipped case is not native acceptance evidence.";
        }
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle handle, int command, int argument);

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEffectiveUid();
}
