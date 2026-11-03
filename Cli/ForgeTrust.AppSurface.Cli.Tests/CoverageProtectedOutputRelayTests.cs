using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageProtectedOutputRelayTests
{
    private const int MaximumBufferedBytesPerStream = 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRunner_RelaysExactCombinedOutputAndKeepsMirrorCallerOwned(bool streamToFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        using var mirror = new RecordingRawOutputStream();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(2 * 1024 * 1024);
        var outputFile = streamToFile ? Path.Join(directory.Path, "reporter.log") : null;
        var observedBytes = 0;
        var request = CreateProcessRequest(
            directory.Path,
            "printf 'coverage-stdout-prefix\\n'; { head -c 1048624 /dev/zero; printf '\\ncoverage-stdout-suffix\\n'; } & stdout_pid=$!; { sleep 0.002; printf 'coverage-stderr-marker\\n' >&2; } & stderr_pid=$!; wait \"$stdout_pid\"; wait \"$stderr_pid\"",
            quota,
            mirror,
            outputFile,
            count => Interlocked.Add(ref observedBytes, count));

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        var expectedStdout = Encoding.UTF8.GetBytes("coverage-stdout-prefix\n")
            .Concat(new byte[1_048_624])
            .Concat(Encoding.UTF8.GetBytes("\ncoverage-stdout-suffix\n"))
            .ToArray();
        var expectedStderr = Encoding.UTF8.GetBytes("coverage-stderr-marker\n");
        var mirroredOutput = mirror.ToArray();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedStdout.Length + expectedStderr.Length, quota.AccountedBytes);
        Assert.Equal(expectedStdout.Length + expectedStderr.Length, observedBytes);
        Assert.Equal(expectedStdout, RemoveSingleOccurrence(mirroredOutput, expectedStderr));
        Assert.True(mirror.FlushCount > 0);
        Assert.Equal(mirror.WriteCount, mirror.FlushCount);
        Assert.Equal(1, mirror.MaximumConcurrentWrites);
        Assert.False(mirror.IsDisposed);

        if (streamToFile)
        {
            Assert.False(result.OutputTruncated);
            Assert.Equal(string.Empty, result.Output);
            Assert.Equal(expectedStdout, RemoveSingleOccurrence(await File.ReadAllBytesAsync(outputFile!), expectedStderr));
        }
        else
        {
            Assert.True(result.OutputTruncated);
            Assert.Contains("coverage-stderr-marker", result.Output, StringComparison.Ordinal);
            Assert.Contains("[output truncated after 1048576 bytes]", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BufferedPump_RelaysFullReadBeforeQuotaRejectionAndBufferDiscard()
    {
        var bytes = Enumerable.Range(0, MaximumBufferedBytesPerStream + 513)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream();
        using var mirrorGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(2 * 1024 * 1024);
        var observedBytes = 0;

        await CliWrapCoverageRunProcessRunner.CopyPipeToBufferAsync(
            source,
            target,
            CancellationToken.None,
            count => observedBytes += count,
            quota,
            processCancellation,
            mirror,
            mirrorGate,
            CancellationToken.None);

        Assert.Equal(MaximumBufferedBytesPerStream + 1, target.Length);
        Assert.Equal(bytes[..(MaximumBufferedBytesPerStream + 1)], target.ToArray());
        Assert.Equal(bytes, mirror.ToArray());
        Assert.Equal(bytes.Length, quota.AccountedBytes);
        Assert.Equal(bytes.Length, observedBytes);
        Assert.False(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Fact]
    public async Task BufferedPump_RelaysOverLimitReadBeforeQuotaFailureAndDoesNotObserveIt()
    {
        var bytes = Encoding.UTF8.GetBytes("over-the-limit-read");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream();
        using var mirrorGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(1);
        var observedBytes = 0;

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() =>
            CliWrapCoverageRunProcessRunner.CopyPipeToBufferAsync(
                source,
                target,
                CancellationToken.None,
                count => observedBytes += count,
                quota,
                processCancellation,
                mirror,
                mirrorGate,
                CancellationToken.None));

        Assert.Contains("ASCOV420", exception.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, mirror.ToArray());
        Assert.Equal(0, target.Length);
        Assert.Equal(0, observedBytes);
        Assert.Equal(0, quota.AccountedBytes);
        Assert.True(quota.IsFailed);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Theory]
    [InlineData(RawOutputFailure.Write)]
    [InlineData(RawOutputFailure.Flush)]
    public async Task BufferedPump_RelayFailureCancelsAndPreventsPositiveAccounting(RawOutputFailure failure)
    {
        var bytes = Encoding.UTF8.GetBytes("relay-failure");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream(failure);
        using var mirrorGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100);
        var observedBytes = 0;

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            CliWrapCoverageRunProcessRunner.CopyPipeToBufferAsync(
                source,
                target,
                CancellationToken.None,
                count => observedBytes += count,
                quota,
                processCancellation,
                mirror,
                mirrorGate,
                CancellationToken.None));

        Assert.Same(mirror.ConfiguredFailure, exception.InnerException);
        Assert.Equal(bytes.Length, quota.AccountedBytes);
        Assert.False(quota.IsFailed);
        Assert.Equal(0, observedBytes);
        Assert.Equal(0, target.Length);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BufferedPump_MirrorWriteAndFlushHonorOriginalCallerCancellationToken(
        bool blockWrites,
        bool blockFlushes)
    {
        var bytes = Encoding.UTF8.GetBytes("cancel-during-relay");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream(blockWrites: blockWrites, blockFlushes: blockFlushes);
        using var mirrorGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100);
        var observedBytes = 0;
        var pump = CliWrapCoverageRunProcessRunner.CopyPipeToBufferAsync(
            source,
            target,
            CancellationToken.None,
            count => observedBytes += count,
            quota,
            processCancellation,
            mirror,
            mirrorGate,
            callerCancellation.Token);

        await mirror.BlockedOperationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);

        Assert.Equal(bytes.Length, quota.AccountedBytes);
        Assert.Equal(0, observedBytes);
        Assert.Equal(0, target.Length);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Fact]
    public async Task StreamingPump_RelaysActualReadBeforeQuotaFailureAndDoesNotWriteRejectedChunk()
    {
        var bytes = Encoding.UTF8.GetBytes("streaming-over-the-limit-read");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream();
        using var writeGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(1);

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() =>
            CliWrapCoverageRunProcessRunner.CopyPipeToFileAsync(
                source,
                target,
                writeGate,
                CancellationToken.None,
                null,
                quota,
                processCancellation,
                mirror,
                CancellationToken.None));

        Assert.Contains("ASCOV420", exception.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, mirror.ToArray());
        Assert.Equal(0, target.Length);
        Assert.Equal(0, quota.AccountedBytes);
        Assert.True(quota.IsFailed);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Theory]
    [InlineData(RawOutputFailure.Write)]
    [InlineData(RawOutputFailure.Flush)]
    public async Task StreamingPump_RelayFailureChargesActualReadAndDoesNotWriteLog(RawOutputFailure failure)
    {
        var bytes = Encoding.UTF8.GetBytes("streaming-relay-failure");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream(failure);
        using var writeGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            CliWrapCoverageRunProcessRunner.CopyPipeToFileAsync(
                source,
                target,
                writeGate,
                CancellationToken.None,
                null,
                quota,
                processCancellation,
                mirror,
                CancellationToken.None));

        Assert.Same(mirror.ConfiguredFailure, exception.InnerException);
        Assert.Equal(bytes.Length, quota.AccountedBytes);
        Assert.False(quota.IsFailed);
        Assert.Equal(0, target.Length);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StreamingPump_MirrorWriteAndFlushHonorOriginalCallerCancellationToken(
        bool blockWrites,
        bool blockFlushes)
    {
        var bytes = Encoding.UTF8.GetBytes("streaming-cancel-during-relay");
        using var source = new MemoryStream(bytes, writable: false);
        using var target = new MemoryStream();
        using var mirror = new RecordingRawOutputStream(blockWrites: blockWrites, blockFlushes: blockFlushes);
        using var writeGate = new SemaphoreSlim(1, 1);
        using var processCancellation = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100);
        var pump = CliWrapCoverageRunProcessRunner.CopyPipeToFileAsync(
            source,
            target,
            writeGate,
            CancellationToken.None,
            null,
            quota,
            processCancellation,
            mirror,
            callerCancellation.Token);

        await mirror.BlockedOperationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);

        Assert.Equal(bytes.Length, quota.AccountedBytes);
        Assert.Equal(0, target.Length);
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.False(mirror.IsDisposed);
    }

    [Fact]
    public async Task ProcessRunner_MirrorFailureJoinsTheActualChildProcessTree()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        using var mirror = new RecordingRawOutputStream(RawOutputFailure.Write);
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100);
        var rootPidPath = Path.Join(directory.Path, "root.pid");
        var childPidPath = Path.Join(directory.Path, "child.pid");
        var script = "echo $$ > \"$1\"; sleep 30 & child=$!; echo $child > \"$2\"; printf x; wait \"$child\"";
        var request = new CoverageRunProcessRequest(
            "/bin/sh",
            ["-c", script, "coverage-output-relay-failure", rootPidPath, childPidPath],
            directory.Path,
            null,
            null,
            CoverageRunProcessLease.Detached(),
            quota,
            mirror);
        int? rootProcessId = null;
        int? childProcessId = null;

        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.NotNull(exception);

            rootProcessId = await ReadProcessIdAsync(rootPidPath);
            childProcessId = await ReadProcessIdAsync(childPidPath);
            Assert.NotEqual(rootProcessId, childProcessId);
            Assert.Equal(1, quota.AccountedBytes);
            Assert.False(quota.IsFailed);
            Assert.False(mirror.IsDisposed);
            await WaitForProcessExitAsync(rootProcessId.Value, TimeSpan.FromSeconds(5));
            await WaitForProcessExitAsync(childProcessId.Value, TimeSpan.FromSeconds(5));
        }
        finally
        {
            KillIfRunning(rootProcessId);
            KillIfRunning(childProcessId);
        }
    }

    private static CoverageRunProcessRequest CreateProcessRequest(
        string workingDirectory,
        string script,
        EvidenceRunByteQuota quota,
        Stream mirror,
        string? outputFile = null,
        Action<int>? outputObserver = null)
        => new(
            "/bin/sh",
            ["-c", script],
            workingDirectory,
            outputFile,
            outputObserver,
            CoverageRunProcessLease.Detached(),
            quota,
            mirror);

    private static byte[] RemoveSingleOccurrence(byte[] source, byte[] value)
    {
        var index = FindSequence(source, value);
        Assert.True(index >= 0, "The relayed output did not contain the complete stderr marker.");
        Assert.Equal(-1, FindSequence(source.AsSpan(index + value.Length).ToArray(), value));
        return source[..index].Concat(source[(index + value.Length)..]).ToArray();
    }

    private static int FindSequence(byte[] source, byte[] value)
    {
        for (var start = 0; start <= source.Length - value.Length; start++)
        {
            if (source.AsSpan(start, value.Length).SequenceEqual(value))
            {
                return start;
            }
        }

        return -1;
    }

    private static async Task<int> ReadProcessIdAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var processId))
            {
                return processId;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"The process did not write its id file: {path}");
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Process {processId} remained alive after its output relay failed.");
    }

    private static void KillIfRunning(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The command runner already joined the process, or the operating system no longer exposes it.
        }
    }

    public enum RawOutputFailure
    {
        None,
        Write,
        Flush,
    }

    private sealed class RecordingRawOutputStream : Stream
    {
        private readonly MemoryStream _contents = new();
        private readonly object _contentsGate = new();
        private readonly RawOutputFailure _failure;
        private readonly bool _blockWrites;
        private readonly bool _blockFlushes;
        private int _activeWrites;
        private int _maximumConcurrentWrites;
        private int _writeCount;
        private int _flushCount;
        private bool _disposed;

        internal RecordingRawOutputStream(
            RawOutputFailure failure = RawOutputFailure.None,
            bool blockWrites = false,
            bool blockFlushes = false)
        {
            _failure = failure;
            _blockWrites = blockWrites;
            _blockFlushes = blockFlushes;
            ConfiguredFailure = new IOException($"Configured raw output {_failure.ToString().ToLowerInvariant()} failure.");
        }

        internal IOException ConfiguredFailure { get; }

        internal bool IsDisposed => _disposed;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal int FlushCount => Volatile.Read(ref _flushCount);

        internal int MaximumConcurrentWrites => Volatile.Read(ref _maximumConcurrentWrites);

        internal Task BlockedOperationStarted => _blockedOperationStarted.Task;

        private readonly TaskCompletionSource _blockedOperationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal byte[] ToArray()
        {
            lock (_contentsGate)
            {
                return _contents.ToArray();
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            Interlocked.Increment(ref _flushCount);
            if (_failure == RawOutputFailure.Flush)
            {
                throw ConfiguredFailure;
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _flushCount);
            if (_failure == RawOutputFailure.Flush)
            {
                throw ConfiguredFailure;
            }

            if (_blockFlushes)
            {
                _blockedOperationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await Task.Delay(1, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeWrites = Interlocked.Increment(ref _activeWrites);
            UpdateMaximumConcurrentWrites(activeWrites);
            Interlocked.Increment(ref _writeCount);
            try
            {
                if (_blockWrites)
                {
                    _blockedOperationStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else
                {
                    await Task.Delay(1, cancellationToken);
                }

                if (_failure == RawOutputFailure.Write)
                {
                    throw ConfiguredFailure;
                }

                lock (_contentsGate)
                {
                    _contents.Write(buffer.Span);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_failure == RawOutputFailure.Write)
            {
                throw ConfiguredFailure;
            }

            lock (_contentsGate)
            {
                _contents.Write(buffer, offset, count);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _contents.Dispose();
            }

            base.Dispose(disposing);
        }

        private void UpdateMaximumConcurrentWrites(int activeWrites)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentWrites);
                if (activeWrites <= current
                    || Interlocked.CompareExchange(ref _maximumConcurrentWrites, activeWrites, current) == current)
                {
                    return;
                }
            }
        }
    }
}
