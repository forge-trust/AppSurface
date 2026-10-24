using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable actual-pipe and counted ownership controls; no Linux factory, D-Bus or native acceptance is exercised.</summary>
public sealed class LinuxOutputPipesTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public void PipeFactsPolicyAcceptsCopiedActualFactsButCreatesNoPipeAuthority()
    {
        var read = Metadata(0x1000, 91, 0, 0);
        var write = read;
        var facts = new LinuxOutputPipeFacts(12, 13, 0, 1, 1, 1, read, write);

        LinuxOutputPipeFactsPolicy.ValidateDescriptors(facts.ReadDescriptor, facts.WriteDescriptor);
        LinuxOutputPipeFactsPolicy.ValidateFlags(facts.ReadAccessFlags, facts.WriteAccessFlags,
            facts.ReadDescriptorFlags, facts.WriteDescriptorFlags);
        LinuxOutputPipeFactsPolicy.ValidateMetadata(facts.ReadMetadata, facts.WriteMetadata);
    }

    [Theory]
    [InlineData(-1, 13)]
    [InlineData(12, -1)]
    [InlineData(12, 12)]
    public void PipeFactsPolicyRejectsEachInvalidDescriptorField(int readDescriptor, int writeDescriptor)
    {
        AssertInvalid(() => LinuxOutputPipeFactsPolicy.ValidateDescriptors(readDescriptor, writeDescriptor));
    }

    [Theory]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 1, -1, 1)]
    [InlineData(0, 1, 1, -1)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(0, 1, 0, 1)]
    [InlineData(0, 1, 1, 0)]
    public void PipeFactsPolicyRejectsEachInvalidFlagField(int readAccess, int writeAccess,
        int readDescriptor, int writeDescriptor)
    {
        AssertInvalid(() => LinuxOutputPipeFactsPolicy.ValidateFlags(readAccess, writeAccess,
            readDescriptor, writeDescriptor));
    }

    [Fact]
    public void PipeFactsPolicyRejectsEachInvalidMetadataFieldWithAdjacentValidPair()
    {
        var read = Metadata(0x1000, 91, 0, 0);
        var write = read;
        foreach (var value in new[]
        {
            read with { Mode = 0x8000 }, read with { Inode = 0 },
            read with { DeviceMajor = 2 }, read with { DeviceMinor = 3 },
            read with { Uid = 1 }, read with { Gid = 1 },
        }) AssertInvalid(() => LinuxOutputPipeFactsPolicy.ValidateMetadata(value, write));
        foreach (var value in new[]
        {
            write with { Mode = 0x8000 }, write with { Inode = 0 },
            write with { DeviceMajor = 2 }, write with { DeviceMinor = 3 },
            write with { Uid = 1 }, write with { Gid = 1 },
        }) AssertInvalid(() => LinuxOutputPipeFactsPolicy.ValidateMetadata(read, value));
        LinuxOutputPipeFactsPolicy.ValidateMetadata(read, write);
    }

    private static LinuxProtectedMetadata Metadata(ushort mode, ulong inode, uint uid, uint gid) =>
        new(4, 5, inode, uid, gid, mode, 1, 0, 0, 0, 0, 0);

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<SupervisionOutputPipeException>(action);
        Assert.Equal(SupervisionOutputPipeFailure.InvalidPipe, error.Failure);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task RealAnonymousPipesObserveBothEofsOnlyAfterLocalWritersClose()
    {
        var stdout = PortablePipe();
        var stderr = PortablePipe();
        var stdoutReadHandle = stdout.Read.SafePipeHandle;
        var stdoutWriteHandle = stdout.Write.SafePipeHandle;
        var stderrReadHandle = stderr.Read.SafePipeHandle;
        var stderrWriteHandle = stderr.Write.SafePipeHandle;
        await using var owner = new SupervisionOutputPipeOwnership(stdout.Read, stderr.Read, stdout.Write, stderr.Write);
        var collection = owner.BeginCollectAsync(default, receivedByteLimit: 128 * 1024, prefixByteLimit: 4);
        Assert.Same(collection, owner.JoinAsync());
        await stdout.Write.WriteAsync(Encoding.UTF8.GetBytes("output"));
        await stderr.Write.WriteAsync(Encoding.UTF8.GetBytes("err"));
        await stdout.Write.FlushAsync();
        await stderr.Write.FlushAsync();
        Assert.False(collection.IsCompleted); // At least one writer remains open, so the two EOFs cannot exist yet.
        owner.CloseWriteCopies();
        var receipt = await collection.WaitAsync(Guard);
        Assert.True(receipt.Successful);
        Assert.Equal(9, receipt.ReceivedBytes);
        Assert.Equal(2, receipt.DiscardedBytes);
        Assert.Equal("outp", Encoding.UTF8.GetString(receipt.Stdout.Prefix.AsSpan()));
        Assert.Equal("err", Encoding.UTF8.GetString(receipt.Stderr.Prefix.AsSpan()));
        Assert.True(stdoutWriteHandle.IsClosed);
        Assert.True(stderrWriteHandle.IsClosed);
        Assert.False(stdoutReadHandle.IsClosed);
        Assert.False(stderrReadHandle.IsClosed);
        await owner.DisposeAsync();
        Assert.True(stdoutReadHandle.IsClosed);
        Assert.True(stderrReadHandle.IsClosed);
    }

    [Fact]
    public async Task WorkCancellationDoesNotCancelPairedDrainOrFailItsRunLedger()
    {
        var stdout = PortablePipe();
        var stderr = PortablePipe();
        var stdoutWriteHandle = stdout.Write.SafePipeHandle;
        var stderrWriteHandle = stderr.Write.SafePipeHandle;
        using var ownerTeardown = new CancellationTokenSource();
        using var workCancellation = new CancellationTokenSource();
        using var drainDeadline = LinuxOutputPipes.CreateDrainDeadline(ownerTeardown.Token, TimeSpan.FromMinutes(1));
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 128 * 1024);
        await using var owner = new SupervisionOutputPipeOwnership(stdout.Read, stderr.Read, stdout.Write, stderr.Write);
        var collection = owner.BeginCollectAsync(drainDeadline.Token, receivedByteLimit: 128 * 1024,
            prefixByteLimit: 32, runBudget: budget);
        var output = Encoding.UTF8.GetBytes("actual-stdout-bytes");
        var error = Encoding.UTF8.GetBytes("actual-stderr-bytes");
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunWorkAsync()
        {
            await stdout.Write.WriteAsync(output).AsTask().WaitAsync(Guard);
            await stderr.Write.WriteAsync(error).AsTask().WaitAsync(Guard);
            await stdout.Write.FlushAsync().WaitAsync(Guard);
            await stderr.Write.FlushAsync().WaitAsync(Guard);
            dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, workCancellation.Token);
        }

        var work = RunWorkAsync();
        try
        {
            await dispatched.Task.WaitAsync(Guard);
            workCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(Guard));
            Assert.True(work.IsCanceled);
            Assert.False(ownerTeardown.IsCancellationRequested);
            Assert.False(drainDeadline.IsCancellationRequested);
            Assert.False(collection.IsCompleted);
            Assert.Same(collection, owner.JoinAsync());
            Assert.False(stdoutWriteHandle.IsClosed);
            Assert.False(stderrWriteHandle.IsClosed);
        }
        finally
        {
            workCancellation.Cancel();
            owner.CloseWriteCopies();
            try { await work.WaitAsync(Guard); }
            catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { }
            await collection.WaitAsync(Guard);
            await budget.CompleteAsync().WaitAsync(Guard);
        }

        var receipt = await collection;
        Assert.True(receipt.Successful);
        Assert.Equal(SupervisionOutputFailure.None, receipt.Failure);
        Assert.Equal(SupervisionOutputFailure.None, receipt.Stdout.Failure);
        Assert.Equal(SupervisionOutputFailure.None, receipt.Stderr.Failure);
        Assert.True(receipt.Stdout.EndOfStream);
        Assert.True(receipt.Stderr.EndOfStream);
        Assert.Equal(output.Length + error.Length, receipt.ReceivedBytes);
        Assert.Equal(0, receipt.DiscardedBytes);
        Assert.Equal(output, receipt.Stdout.Prefix.ToArray());
        Assert.Equal(error, receipt.Stderr.Prefix.ToArray());
        Assert.True(stdoutWriteHandle.IsClosed);
        Assert.True(stderrWriteHandle.IsClosed);

        var ledger = await budget.CompleteAsync().WaitAsync(Guard);
        Assert.True(ledger.Successful);
        Assert.Equal(SupervisionRunOutputFailure.None, ledger.Failure);
        Assert.Equal(1, ledger.RegisteredPairs);
        Assert.Equal(receipt.ReceivedBytes, ledger.ReceivedBytes);
        Assert.Single(ledger.Pairs);
        Assert.Same(receipt, ledger.Pairs[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void DrainDeadlineCannotCreateAnUnboundedOrRenewedAllowance(int milliseconds)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            LinuxOutputPipes.CreateDrainDeadline(default, TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("originalRemaining", error.ParamName);
    }

    [Fact]
    public async Task OwnerTeardownStillRejectsDrainAndCannotInventAnEofOrDetachARead()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        using var teardown = new CancellationTokenSource();
        using var deadline = LinuxOutputPipes.CreateDrainDeadline(teardown.Token, TimeSpan.FromHours(1));
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(deadline.Token);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            teardown.Cancel();
            Assert.True(deadline.IsCancellationRequested);
            Assert.False(task.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount);
            Assert.Equal(0, stderr.DisposeCount);
        }
        finally
        {
            owner.CloseWriteCopies();
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
        }
        Assert.False((await task).Successful);
        Assert.Equal(SupervisionOutputFailure.Cancelled, (await task).Failure);
    }

    [Fact]
    public async Task OneCollectionTaskIsRegisteredBeforeEitherPumpCanObserveEntry()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            Assert.Same(task, owner.JoinAsync());
            AssertClosed(() => owner.BeginCollectAsync(default));
            Assert.Equal(1, stdout.ReadCount);
            Assert.Equal(1, stderr.ReadCount);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CancellationIgnoringReadKeepsCollectionAndDisposalOwnedUntilActualJoin()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        using var stop = new CancellationTokenSource();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(stop.Token);
        Task? dispose = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stop.Cancel();
            dispose = owner.DisposeAsync().AsTask();
            Assert.Same(dispose, owner.DisposeAsync().AsTask());
            Assert.False(task.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount);
            Assert.Equal(0, stderr.DisposeCount);
            stdout.ReleaseEof();
            await stdout.Returned.Task.WaitAsync(Guard);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount); // The other original pump is still alive.
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        var receipt = await task;
        Assert.False(receipt.Successful);
        Assert.Equal(SupervisionOutputFailure.Cancelled, receipt.Failure);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
    }

    [Fact]
    public async Task WriteCloseFailureAttemptsBothAndStillJoinsBeforeReadClosure()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var first = new CountedWriteCloser(fail: true);
        var second = new CountedWriteCloser();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, first, second);
        var task = owner.BeginCollectAsync(default);
        Task? disposal = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            var error = Assert.Throws<SupervisionOutputPipeException>(() => owner.CloseWriteCopies());
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
            Assert.Equal(1, first.CloseCount);
            Assert.Equal(1, second.CloseCount);
            AssertClosed(() => owner.CloseWriteCopies());
            disposal = owner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount);
            Assert.Equal(0, stderr.DisposeCount);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            var error = await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
        }
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
        await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ReadCloseFailureCannotTurnConcurrentDisposalIntoSuccess()
    {
        var stdout = new CloseBarrierStream(fail: true);
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var collection = owner.BeginCollectAsync(default);
        Task? dispose = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            Assert.True((await collection.WaitAsync(Guard)).Successful);
            dispose = owner.DisposeAsync().AsTask();
            await stdout.CloseEntered.Task.WaitAsync(Guard);
            var concurrent = owner.DisposeAsync().AsTask();
            Assert.Same(dispose, concurrent);
            Assert.False(concurrent.IsCompleted);
            Assert.Equal(0, stderr.DisposeCount);
            AssertClosed(() => owner.BeginCollectAsync(default));
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            stdout.ReleaseClose();
            var error = await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
        }
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        var repeated = await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => owner.DisposeAsync().AsTask());
        Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, repeated.Failure);
        Assert.DoesNotContain("private-canary", repeated.ToString());
    }

    [Fact]
    public async Task ReadFailureRetainsNegativeReceiptAndDisposalStillClosesEveryLocalResource()
    {
        var stdout = new GatedReadStream(failRead: true);
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.ReleaseEof();
            await stdout.Returned.Task.WaitAsync(Guard);
            Assert.False(task.IsCompleted);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            await owner.DisposeAsync();
        }
        var receipt = await task;
        Assert.False(receipt.Successful);
        Assert.False(receipt.Stdout.EndOfStream);
        Assert.Equal(SupervisionOutputFailure.ReadFailed, receipt.Failure);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public async Task LoweredSharedBudgetCountsOverflowWithoutDetachedSiblingOrRaisedLimits()
    {
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(new MemoryStream([1, 2, 3]),
            new MemoryStream(), writes.First, writes.Second);
        var receipt = await owner.BeginCollectAsync(default, receivedByteLimit: 2, prefixByteLimit: 1).WaitAsync(Guard);
        Assert.True(receipt.QuotaExceeded);
        Assert.Equal(3, receipt.ReceivedBytes);
        Assert.Equal(3, receipt.DiscardedBytes);
        Assert.Equal(SupervisionOutputFailure.QuotaExceeded, receipt.Failure);
        Assert.False(receipt.Successful);
        Assert.Same(owner.JoinAsync(), owner.JoinAsync());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("write")]
    [InlineData("read")]
    public async Task ClosedRunLedgerRejectsDispatchAndPreservesTheFirstCleanupFailure(string closeFailure)
    {
        var expected = closeFailure == "write" ? SupervisionOutputPipeFailure.CloseFailed : SupervisionOutputPipeFailure.CollectionFailed;
        using var budget = new SupervisionRunOutputBudget();
        await budget.CompleteAsync().WaitAsync(Guard);
        GatedReadStream stdout = closeFailure == "read" ? new CloseBarrierStream(fail: true) : new GatedReadStream();
        var stderr = new GatedReadStream();
        var firstWrite = new CountedWriteCloser(fail: closeFailure == "write");
        var secondWrite = new CountedWriteCloser();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, firstWrite, secondWrite);
        if (stdout is CloseBarrierStream close) close.ReleaseClose();
        Task<SupervisionOutputReceipt>? collection = null;
        Task? disposal = null;
        try
        {
            collection = owner.BeginCollectAsync(default, runBudget: budget);
            Assert.Same(collection, owner.JoinAsync());
            var collectionError = await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => collection.WaitAsync(Guard));
            Assert.Equal(SupervisionOutputPipeFailure.CollectionFailed, collectionError.Failure);
            Assert.Null(collectionError.InnerException);
            Assert.DoesNotContain("registration", collectionError.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stdout.ReadCount);
            Assert.Equal(0, stderr.ReadCount);
            disposal = owner.DisposeAsync().AsTask();
            Assert.Same(disposal, owner.DisposeAsync().AsTask());
            var disposeError = await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => disposal.WaitAsync(Guard));
            Assert.Equal(expected, disposeError.Failure);
            Assert.Null(disposeError.InnerException);
            Assert.DoesNotContain("private-canary", disposeError.ToString());
            Assert.Equal(1, stdout.DisposeCount);
            Assert.Equal(1, stderr.DisposeCount);
            Assert.Equal(1, firstWrite.CloseCount);
            Assert.Equal(1, secondWrite.CloseCount);
            Assert.Same(disposal, owner.DisposeAsync().AsTask());
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            if (stdout is CloseBarrierStream pendingClose) pendingClose.ReleaseClose();
            try
            {
                if (collection is not null) await collection.WaitAsync(Guard);
            }
            catch (SupervisionOutputPipeException) { }
            finally
            {
                try { await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard); }
                catch (SupervisionOutputPipeException) { }
            }
        }
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(16_777_217L, 1)]
    [InlineData(1L, -1)]
    [InlineData(1L, 1_048_577)]
    public async Task InvalidLimitsDoNotDispatchOrConsumeTheOnlyValidAttempt(long total, int prefix)
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = owner.BeginCollectAsync(default, total, prefix); });
            Assert.Equal(0, stdout.ReadCount);
            Assert.Equal(0, stderr.ReadCount);
            AssertClosed(() => owner.JoinAsync());
            var task = owner.BeginCollectAsync(default, 1, 0);
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            Assert.True((await task.WaitAsync(Guard)).Successful);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
        }
    }

    [Fact]
    public async Task DisposalBeforeCollectionClosesUnstartedResourcesAndPermanentlyBlocksDispatch()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        owner.RequireWritesOpen();
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        AssertClosed(() => owner.BeginCollectAsync(default));
        AssertClosed(() => owner.JoinAsync());
        AssertClosed(() => owner.RequireWritesOpen());
        Assert.Equal(0, stdout.ReadCount);
        Assert.Equal(0, stderr.ReadCount);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public async Task LocalWriteCloseIsIdempotentAndDoesNotCloseReadsOrRequireAnInventedStartReceipt()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        owner.CloseWriteCopies();
        owner.CloseWriteCopies();
        AssertClosed(() => owner.RequireWritesOpen());
        Assert.Equal(0, stdout.DisposeCount);
        Assert.Equal(0, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public void AliasedReadsOrClosersCannotConstructAnOwnedPair()
    {
        using var stream = new MemoryStream();
        using var other = new MemoryStream();
        var writes = Writes();
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, stream, writes.First, writes.Second));
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, other, writes.First, writes.First));
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, other, stream, writes.First));
        Assert.Equal(0, writes.First.CloseCount);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForAnActualWriteCloseAndRetainsItsFailure()
    {
        var first = new WriteBarrierCloser();
        var second = new CountedWriteCloser();
        var owner = new SupervisionOutputPipeOwnership(new MemoryStream(), new MemoryStream(), first, second);
        var close = Task.Run(() => Record.Exception(() => owner.CloseWriteCopies()));
        Task? disposal = null;
        try
        {
            await first.Entered.Task.WaitAsync(Guard);
            disposal = owner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            first.Release();
            try
            {
                var error = Assert.IsType<SupervisionOutputPipeException>(await close.WaitAsync(Guard));
                Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
                await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                    await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            }
            finally { first.CloseBarrier(); }
        }
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
    }

    [Fact]
    public async Task OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs()
    {
        var stdout = PortablePipe();
        using var stdoutServer = stdout.Read;
        using var stdoutWriter = stdout.Write;
        var stderr = PortablePipe();
        using var stderrServer = stderr.Read;
        using var stderrWriter = stderr.Write;
        var stdoutWriteHandle = stdoutWriter.SafePipeHandle;
        var stderrWriteHandle = stderrWriter.SafePipeHandle;
        using var stdoutHandle = TakeOwnedReadHandle(stdoutServer);
        using var stderrHandle = TakeOwnedReadHandle(stderrServer);
        using var stdoutRead = LinuxOutputPipes.WrapOwnedRead(stdoutHandle);
        using var stderrRead = LinuxOutputPipes.WrapOwnedRead(stderrHandle);
        if (!OperatingSystem.IsWindows())
        {
            // The actual Unix raw-handle wrapping path must not force an asynchronous handle flag.
            Assert.False(stdoutRead.IsAsync);
            Assert.False(stderrRead.IsAsync);
        }
        Assert.Same(stdoutHandle, stdoutRead.SafeFileHandle);
        Assert.Same(stderrHandle, stderrRead.SafeFileHandle);
        await using var owner = new SupervisionOutputPipeOwnership(stdoutRead, stderrRead,
            stdoutWriter, stderrWriter);
        var collection = owner.BeginCollectAsync(default, receivedByteLimit: 128 * 1024, prefixByteLimit: 32);
        var output = Encoding.UTF8.GetBytes("stdout-data");
        var error = Encoding.UTF8.GetBytes("stderr-data");
        try
        {
            await stdoutWriter.WriteAsync(output).AsTask().WaitAsync(Guard);
            await stderrWriter.WriteAsync(error).AsTask().WaitAsync(Guard);
            await stdoutWriter.FlushAsync().WaitAsync(Guard);
            await stderrWriter.FlushAsync().WaitAsync(Guard);
            Assert.Same(collection, owner.JoinAsync());
            Assert.False(collection.IsCompleted);
            Assert.False(stdoutHandle.IsClosed);
            Assert.False(stderrHandle.IsClosed);
        }
        finally
        {
            owner.CloseWriteCopies();
            await collection.WaitAsync(Guard);
        }
        var receipt = await owner.JoinAsync();
        Assert.True(receipt.Successful);
        Assert.True(receipt.Stdout.EndOfStream);
        Assert.True(receipt.Stderr.EndOfStream);
        Assert.Equal(output.Length + error.Length, receipt.ReceivedBytes);
        Assert.Equal(0, receipt.DiscardedBytes);
        Assert.Equal(output, receipt.Stdout.Prefix.ToArray());
        Assert.Equal(error, receipt.Stderr.Prefix.ToArray());
        Assert.True(stdoutWriteHandle.IsClosed);
        Assert.True(stderrWriteHandle.IsClosed);
        Assert.False(stdoutHandle.IsClosed);
        Assert.False(stderrHandle.IsClosed);
        await owner.DisposeAsync();
        Assert.True(stdoutHandle.IsClosed);
        Assert.True(stderrHandle.IsClosed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EitherReadStreamCannotAlsoBeEitherWriteCloser(int alias)
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var writes = Writes();
        IDisposable first = alias == 1 ? stderr : writes.First;
        IDisposable second = alias switch { 2 => stdout, 3 => stderr, _ => writes.Second };
        var error = Assert.Throws<SupervisionOutputPipeException>(() =>
            new SupervisionOutputPipeOwnership(stdout, stderr, first, second));
        Assert.Equal(SupervisionOutputPipeFailure.InvalidPipe, error.Failure);
        Assert.Null(error.InnerException);
        Assert.True(stdout.CanRead);
        Assert.True(stderr.CanRead);
        Assert.Equal(0, writes.First.CloseCount);
        Assert.Equal(0, writes.Second.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteOnlyOwnedFileCannotBeAdoptedAsAnOutputReader(bool asStderr)
    {
        var path = Path.Combine(Path.GetTempPath(), $"output-reader-{Guid.NewGuid():N}");
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var readable = new MemoryStream([1]);
            var writes = Writes();
            var error = Assert.Throws<SupervisionOutputPipeException>(() => new SupervisionOutputPipeOwnership(
                asStderr ? readable : file, asStderr ? file : readable, writes.First, writes.Second));
            Assert.Equal(SupervisionOutputPipeFailure.InvalidPipe, error.Failure);
            Assert.Null(error.InnerException);
            Assert.False(file.SafeFileHandle.IsClosed);
            Assert.Equal(0, readable.Position);
            Assert.Equal(0, writes.First.CloseCount);
            Assert.Equal(0, writes.Second.CloseCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NullOwnershipArgumentsDoNotCloseTheResourcesNeededByAValidPair()
    {
        using var stdout = new MemoryStream([1]);
        using var stderr = new MemoryStream([2]);
        var writes = Writes();
        Assert.Throws<ArgumentNullException>(() => new SupervisionOutputPipeOwnership(null!, stderr, writes.First, writes.Second));
        Assert.Throws<ArgumentNullException>(() => new SupervisionOutputPipeOwnership(stdout, null!, writes.First, writes.Second));
        Assert.Throws<ArgumentNullException>(() => new SupervisionOutputPipeOwnership(stdout, stderr, null!, writes.Second));
        Assert.Throws<ArgumentNullException>(() => new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, null!));
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var receipt = await owner.BeginCollectAsync(default).WaitAsync(Guard);
        Assert.True(receipt.Successful);
        Assert.Equal(2, receipt.ReceivedBytes);
        await owner.DisposeAsync();
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    /// <summary>Moves one actual fixture read endpoint from its pipe handle to one owned file handle.</summary>
    /// <remarks>The server remains retained; its shared writer closes only through the original pipe owner.</remarks>
    private static SafeFileHandle TakeOwnedReadHandle(AnonymousPipeServerStream server)
    {
        var original = server.SafePipeHandle;
        var retained = false;
        try
        {
            original.DangerousAddRef(ref retained);
            var owned = new SafeFileHandle(original.DangerousGetHandle(), ownsHandle: true);
            original.SetHandleAsInvalid(); // Prevent a second owner from closing the transferred descriptor.
            return owned;
        }
        finally
        {
            if (retained) original.DangerousRelease();
        }
    }

    private static (AnonymousPipeServerStream Read, AnonymousPipeClientStream Write) PortablePipe()
    {
        var reader = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        // Same-process use shares one SafePipeHandle object, not two owners of the same raw descriptor.
        // Closing the client write handle produces EOF; the server's read handle stays open until joined.
        return (reader, new AnonymousPipeClientStream(PipeDirection.Out, reader.ClientSafePipeHandle));
    }

    private static (CountedWriteCloser First, CountedWriteCloser Second) Writes() => (new(), new());

    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<SupervisionOutputPipeException>(action);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }

    private sealed class CountedWriteCloser(bool fail = false) : IDisposable
    {
        internal int CloseCount { get; private set; }
        public void Dispose()
        {
            CloseCount++;
            if (fail) throw new IOException("private-canary");
        }
    }

    private sealed class WriteBarrierCloser : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CloseCount { get; private set; }
        internal void Release() => _release.Set();
        internal void CloseBarrier() => _release.Dispose();
        public void Dispose()
        {
            CloseCount++;
            Entered.TrySetResult();
            if (!_release.Wait(Guard)) throw new IOException("private-canary");
            throw new IOException("private-canary");
        }
    }

    private class GatedReadStream(bool failRead = false) : Stream
    {
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal void ReleaseEof() => _eof.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Entered.TrySetResult();
            await _eof.Task.ConfigureAwait(false); // Deliberately ignores cancellation; actual pump ownership is tested.
            Returned.TrySetResult();
            if (failRead) throw new IOException("private-canary");
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CloseBarrierStream(bool fail) : GatedReadStream
    {
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseClose() => _close.TrySetResult();
        public override async ValueTask DisposeAsync()
        {
            CloseEntered.TrySetResult();
            await _close.Task.ConfigureAwait(false);
            Dispose();
            if (fail) throw new IOException("private-canary");
        }
    }
}
