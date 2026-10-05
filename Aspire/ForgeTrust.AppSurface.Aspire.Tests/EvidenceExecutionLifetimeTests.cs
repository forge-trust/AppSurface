using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceExecutionLifetimeTests
{
    [Fact]
    public void ProcessLifetime_ShouldRejectMissingOrUnstartedOwnership()
    {
        Assert.Throws<ArgumentNullException>(() => new EvidenceProcessLifetime(null!));
        Assert.Throws<ArgumentException>(() => new EvidenceProcessLifetime([]));
        Assert.Throws<ArgumentException>(() => new EvidenceProcessLifetime([null!]));
        using var unstarted = new Process();
        Assert.Throws<InvalidOperationException>(() => new EvidenceProcessLifetime([unstarted]));
    }

    [Fact]
    public async Task Host_ShouldStopAndJoinProcessIgnoringGracefulTerminationBeforeDisposal()
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = Start("/bin/sh", "-c", "trap '' TERM; echo ready; while :; do sleep 1; done");
        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ready", ready);
        var producer = new ProcessProducer(process);
        var host = EvidenceHostBootstrap.Create(Plan(), registration => registration.AddProducer(producer),
            new EvidenceHostOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(100), CleanupTimeout = TimeSpan.FromSeconds(5) });
        try
        {
            var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EvidenceProducerOutcome.TimedOut, Assert.Single(manifest.ProducerResults).Outcome);
            Assert.True(manifest.Metrics.CleanupCompleted);
            Assert.True(process.HasExited);
            Assert.True(producer.DisposedAfterExit);
            await host.DisposeAsync();
        }
        finally
        {
            await KillAndJoinAsync(process);
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProcessLifetime_ShouldJoinEnrolledDescendantAfterLeaderExited()
    {
        if (OperatingSystem.IsWindows()) return;
        using var leader = Start("python3", "-u", "-c", "import os,sys,time,signal\npid=os.fork()\nif pid==0:\n signal.signal(signal.SIGTERM, signal.SIG_IGN)\n while True: time.sleep(0.1)\nelse:\n print(pid,flush=True)\n sys.stdin.readline()\n");
        Process? descendant = null;
        try
        {
            var pid = int.Parse((await leader.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            descendant = Process.GetProcessById(pid);
            var lifetime = new EvidenceProcessLifetime([leader, descendant]);
            await leader.StandardInput.WriteLineAsync("exit");
            await leader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(leader.HasExited);
            Assert.False(descendant.HasExited);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(lifetime.StopAsync(cleanup.Token).AsTask(), lifetime.StopAsync(cleanup.Token).AsTask());
            Assert.True(descendant.HasExited);
            await lifetime.StopAsync(cleanup.Token);
        }
        finally
        {
            await KillAndJoinAsync(leader);
            if (descendant is not null)
            {
                await KillAndJoinAsync(descendant);
                descendant.Dispose();
            }
        }
    }

    [Fact]
    public async Task ProcessLifetime_ShouldAttemptOtherOwnersWhenOneHandleBecameInvalid()
    {
        if (OperatingSystem.IsWindows()) return;
        using var valid = Start("/bin/sh", "-c", "echo ready; exec sleep 60");
        using var invalid = Start("/bin/sh", "-c", "exit 0");
        await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await valid.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var lifetime = new EvidenceProcessLifetime([valid, invalid]);
        invalid.Dispose();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StopAsync(cancellation.Token).AsTask());
            Assert.True(valid.HasExited);
        }
        finally
        {
            await KillAndJoinAsync(valid);
        }
    }

    [Fact]
    public async Task ProcessLifetime_ShouldAttemptAndJoinOtherOwnersAfterTreeTerminationFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        using var valid = Start("/bin/sh", "-c", "echo ready; exec sleep 60");
        using var failing = Start("/bin/sh", "-c", "echo ready; exec sleep 60");
        await valid.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await failing.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var attempted = new List<Process>();
        var lifetime = new EvidenceProcessLifetime([valid, failing], process =>
        {
            attempted.Add(process);
            process.Kill(entireProcessTree: true);
            if (ReferenceEquals(process, failing))
                throw new AggregateException(new System.ComponentModel.Win32Exception("Synthetic tree termination failure."));
        });
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StopAsync(cancellation.Token).AsTask());
            Assert.IsType<AggregateException>(error.InnerException);
            Assert.Equal([failing, valid], attempted);
            Assert.True(failing.HasExited);
            Assert.True(valid.HasExited);
        }
        finally
        {
            await KillAndJoinAsync(failing);
            await KillAndJoinAsync(valid);
        }
    }

    private static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private static async Task KillAndJoinAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static EvidencePlan Plan() => new("1.0", "policy", "policy-digest", "diff-digest",
        new EvidenceProfile("process", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("process", "process", "1.0.0", [], ["process/assertion@1"], [], 30)],
            [new EvidenceObligation("process", "process", "Process lifecycle", ["process"], "process/assertion@1")]),
        [new NormalizedDiffPath("src/process.cs")], ["process"], "plan-digest");

    private sealed class ProcessProducer(Process process) : IEvidenceProducer, IEvidenceExecutionLifetime, IDisposable
    {
        private readonly EvidenceProcessLifetime _lifetime = new([process]);
        public string Id => "process";
        public bool DisposedAfterExit { get; private set; }
        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            // Process-backed callbacks join their owner rather than returning merely on cancellation.
            await process.WaitForExitAsync();
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["process/assertion@1"]);
        }
        public ValueTask StopAsync(CancellationToken cancellationToken) => _lifetime.StopAsync(cancellationToken);
        public void Dispose() => DisposedAfterExit = process.HasExited;
    }
}
