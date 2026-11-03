using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Ordinary local-owner closure controls; no native process, account, settlement or custody is fabricated.</summary>
public sealed class SupervisionCloseSequenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ACloseFailureCannotSkipAnyLaterOwnerOrTurnTheSequenceSuccessful(int failureIndex)
    {
        var order = new List<int>();
        var owners = Enumerable.Range(0, 4).Select(index => new CloseOwner(() =>
        {
            order.Add(index);
            if (index == failureIndex) throw new IOException("private-close-canary");
        })).ToArray();
        var failed = 0;
        SupervisionCloseSequence.CloseAll(owners, ref failed);
        Assert.Equal(1, failed);
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
        Assert.All(owners, owner => Assert.Equal(1, owner.Calls));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("access")]
    public void OriginalProducerCatchAllStillAttemptsLaterLocalClosesForEveryExceptionClass(string kind)
    {
        Exception failure = kind switch
        {
            "memory" => new OutOfMemoryException(),
            "stack" => new StackOverflowException(),
            "access" => new AccessViolationException(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var failed = new CloseOwner(() => throw failure);
        var next = new CloseOwner(() => { });
        var latch = 0;
        SupervisionCloseSequence.CloseAll([failed, next], ref latch);
        Assert.Equal(1, latch);
        Assert.Equal(1, failed.Calls);
        Assert.Equal(1, next.Calls);
    }

    [Fact]
    public void EveryFailureIsAttemptedAndAnAbsentOwnerDoesNotGrantAReplacementOrRetry()
    {
        var first = new CloseOwner(() => throw new IOException("private-first"));
        var second = new CloseOwner(() => throw new InvalidOperationException("private-second"));
        var failed = 0;
        SupervisionCloseSequence.CloseAll([first, null, second], ref failed);
        Assert.Equal(1, failed);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        SupervisionCloseSequence.CloseAll([], ref failed);
        SupervisionCloseSequence.CloseAll([null], ref failed);
        Assert.Equal(1, failed);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void ActualFileOwnersCloseOnBothSidesOfAFailureAndTheirOriginalBytesRemainUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "issue779-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "first"), "first sentinel");
            File.WriteAllText(Path.Combine(path, "last"), "last sentinel");
            using SafeFileHandle first = File.OpenHandle(Path.Combine(path, "first"), FileMode.Open, FileAccess.Read);
            using SafeFileHandle last = File.OpenHandle(Path.Combine(path, "last"), FileMode.Open, FileAccess.Read);
            var failed = new CloseOwner(() => throw new IOException("private-close"));
            var latch = 0;
            SupervisionCloseSequence.CloseAll([first, failed, last], ref latch);
            Assert.Equal(1, latch);
            Assert.True(first.IsClosed);
            Assert.True(last.IsClosed);
            Assert.Equal(1, failed.Calls);
            Assert.Equal("first sentinel", File.ReadAllText(Path.Combine(path, "first")));
            Assert.Equal("last sentinel", File.ReadAllText(Path.Combine(path, "last")));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void SuccessfulCloseCallsEachOriginalOwnerOnceInOrder()
    {
        var order = new List<int>();
        var first = new CloseOwner(() => order.Add(1));
        var second = new CloseOwner(() => order.Add(2));
        var failed = 0;
        SupervisionCloseSequence.CloseAll([first, null, second], ref failed);
        Assert.Equal(0, failed);
        Assert.Equal(new[] { 1, 2 }, order);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void TheOriginalFailureLatchIsVisibleBeforeTheNextCloseAndCannotBeClearedByLaterSuccess()
    {
        var failed = 0;
        var first = new CloseOwner(() => throw new IOException("private-close"));
        var second = new CloseOwner(() => Assert.Equal(1, Volatile.Read(ref failed)));
        SupervisionCloseSequence.CloseAll([first, second], ref failed);
        Assert.Equal(1, failed);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void MissingSequenceRejectsBeforeAnyCloseCanDispatch()
    {
        var failed = 0;
        var error = Assert.Throws<ArgumentNullException>(() => SupervisionCloseSequence.CloseAll(null!, ref failed));
        Assert.Equal("owners", error.ParamName);
    }

    private sealed class CloseOwner(Action close) : IDisposable
    {
        internal int Calls { get; private set; }
        public void Dispose() { Calls++; close(); }
    }
}
