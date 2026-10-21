using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable real-file read-engine and detached data guards; these establish no native root custody.</summary>
public sealed class LinuxRetainedReportSetTests
{
    private static LinuxInputDeadline Deadline => new(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(10));

    [Fact]
    public void InventoryIsCopiedSortedAndAllowsEmptyWithoutGrantingAuthority()
    {
        var source = new[] { "z/report.xml", "a.json" };
        var copy = LinuxRetainedReportSet.ValidatePaths(source);
        source[0] = "changed";
        Assert.Equal(new[] { "a.json", "z/report.xml" }, copy);
        Assert.Empty(LinuxRetainedReportSet.ValidatePaths([]));
        Assert.Equal(64, LinuxRetainedReportSet.ValidatePaths(
            Enumerable.Range(0, 64).Select(i => $"{i}.json").ToArray()).Length);
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths(
            Enumerable.Range(0, 65).Select(i => $"{i}.json").ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("a/./report")]
    [InlineData("a//report")]
    [InlineData("a/")]
    [InlineData("a\\report")]
    [InlineData("drive:report")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("a\nreport")]
    public void NoncanonicalNamesReject(string path) =>
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths([path]));

    [Fact]
    public void InventoryRejectsCaseAliasesPrefixCollisionsAndEncodingBounds()
    {
        foreach (var paths in new[]
        {
            new[] { "same", "same" }, new[] { "same", "Same" },
            new[] { "file", "file/child" }, new[] { "FILE", "file/child" },
            new[] { "dir/a", "Dir/b" }, new[] { new string('x', 256) },
            new[] { string.Join('/', Enumerable.Repeat("a", 33)) }, new[] { "\ud800" },
            new[] { string.Join('/', Enumerable.Repeat(new string('x', 255), 17)) },
        }) Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths(paths));
    }

    [Fact]
    public void InventoryRejectsNullAndACollectionChangedDuringItsCopy()
    {
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths(null!));
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths([null!]));
        var changing = new ChangingInventory();
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths(changing));
        Assert.Equal(1, changing.Reads);
    }

    [Fact]
    public void InventoryAcceptsExactUtf8AndDepthLimitsAndRejectsTheNextByte()
    {
        var component = new string('x', 255);
        var maximumPath = string.Join('/', Enumerable.Repeat(component, 16));
        Assert.Equal(4095, System.Text.Encoding.UTF8.GetByteCount(maximumPath));
        Assert.Equal(maximumPath, Assert.Single(LinuxRetainedReportSet.ValidatePaths([maximumPath])));
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths([maximumPath + "x"]));
        var unicodeComponent = new string('\u00e9', 127) + "x";
        Assert.Equal(255, System.Text.Encoding.UTF8.GetByteCount(unicodeComponent));
        Assert.Equal(unicodeComponent, Assert.Single(LinuxRetainedReportSet.ValidatePaths([unicodeComponent])));
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths([unicodeComponent + "x"]));
        var deepest = string.Join('/', Enumerable.Repeat("a", 32));
        Assert.Equal(deepest, Assert.Single(LinuxRetainedReportSet.ValidatePaths([deepest])));
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.ValidatePaths([deepest + "/a"]));
    }

    [Fact]
    public void SizeGuardsCheckIndependentFileAndAggregateBoundsWithoutOverflow()
    {
        Assert.Equal(0, LinuxRetainedReportSet.AddLength(0, 0));
        Assert.Equal(LinuxRetainedReportSet.MaximumFileBytes,
            LinuxRetainedReportSet.AddLength(0, (ulong)LinuxRetainedReportSet.MaximumFileBytes));
        Assert.Equal(LinuxRetainedReportSet.MaximumAggregateBytes,
            LinuxRetainedReportSet.AddLength(LinuxRetainedReportSet.MaximumAggregateBytes - 1, 1));
        foreach (var (current, length) in new[]
        {
            (-1L, 0UL), (0L, ulong.MaxValue), (0L, (ulong)LinuxRetainedReportSet.MaximumFileBytes + 1),
            (LinuxRetainedReportSet.MaximumAggregateBytes, 1UL), (long.MaxValue, 1UL),
        }) Assert.Throws<IOException>(() => LinuxRetainedReportSet.AddLength(current, length));
    }

    [Fact]
    public void DetachedMetadataRejectsEachUnsafeIdentityTypeModeLinkAndTimeField()
    {
        var file = Metadata();
        LinuxRetainedReportSet.RequireMetadata(file, false, 123, 456);
        foreach (var invalid in new[]
        {
            file with { Inode = 0 }, file with { Uid = 124 }, file with { Gid = 457 },
            file with { Mode = 0xa180 }, file with { Mode = 0x4180 }, file with { Mode = 0x9180 },
            file with { Mode = 0x8182 }, file with { Mode = 0x8190 }, file with { Mode = 0x8980 },
            file with { Mode = 0x8080 }, file with { LinkCount = 0 }, file with { LinkCount = 2 },
            file with { Length = (ulong)LinuxRetainedReportSet.MaximumFileBytes + 1 },
            file with { ChangeNanoseconds = 1_000_000_000 }, file with { ModifyNanoseconds = 1_000_000_000 },
        }) Assert.Throws<IOException>(() => LinuxRetainedReportSet.RequireMetadata(invalid, false, 123, 456));
        var directory = file with { Mode = 0x41c0, LinkCount = 2 };
        LinuxRetainedReportSet.RequireMetadata(directory, true, 123, 456);
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.RequireMetadata(
            directory with { Mode = 0x4180 }, true, 123, 456));
    }

    [Fact]
    public void RootFactoryCannotFallBackToPortablePathAccessOnMac()
    {
        if (OperatingSystem.IsLinux()) return; // Platform rejection only; this is not skipped native proof.
        Assert.Throws<EvidenceAdmissionException>(() => LinuxRetainedReportSet.OpenActual(
            "/not-opened", Metadata() with { Mode = 0x41c0 }, [], 123, 456, Deadline));
    }

    [Fact]
    public void RootSelectionComparisonBindsDeviceInodeOwnerGroupAndMode()
    {
        var expected = Metadata() with { Mode = 0x41c0, LinkCount = 2 };
        LinuxRetainedReportSet.RequireRootIdentity(expected, expected, 123, 456);
        foreach (var actual in new[]
        {
            expected with { DeviceMajor = 2 }, expected with { DeviceMinor = 3 },
            expected with { Inode = 4 }, expected with { Uid = 124 }, expected with { Gid = 457 },
            expected with { Mode = 0x41e8 },
        }) Assert.Throws<IOException>(() => LinuxRetainedReportSet.RequireRootIdentity(actual, expected, 123, 456));
    }

    [Fact]
    public void RealPortableFileHashesEntireContentAndReadsOnlyMonotonicChunks()
    {
        using var fixture = new PortableFile(Enumerable.Range(0, 170003).Select(i => (byte)i).ToArray());
        using var reader = fixture.Capture();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)), reader.Sha256);
        using var output = new MemoryStream();
        long offset = 0;
        while (offset < reader.Length)
        {
            var bytes = reader.ReadChunk(offset, 7777);
            Assert.InRange(bytes.Length, 1, 7777);
            output.Write(bytes);
            offset += bytes.Length;
        }
        Assert.Equal(fixture.Bytes, output.ToArray());
        Assert.Throws<IOException>(() => reader.ReadChunk(offset, 1));
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
    }

    [Fact]
    public void EmptyFileRequiresActualEofAndAllowsOnlyOneEmptyChunk()
    {
        using var fixture = new PortableFile([]);
        using var reader = fixture.Capture();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), reader.Sha256);
        Assert.Empty(reader.ReadChunk(0, 1));
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 65537)]
    public void InvalidOffsetOrBoundPoisonsSubsequentReads(long offset, int maximum)
    {
        using var fixture = new PortableFile([1, 2, 3]);
        using var reader = fixture.Capture();
        Assert.Throws<IOException>(() => reader.ReadChunk(offset, maximum));
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
    }

    [Fact]
    public void ReplayOfAlreadyReturnedChunkRejectsWithoutResettingPosition()
    {
        using var fixture = new PortableFile([1, 2, 3]);
        using var reader = fixture.Capture();
        Assert.Equal(new byte[] { 1 }, reader.ReadChunk(0, 1));
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
        Assert.Throws<IOException>(() => reader.ReadChunk(1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void GrowthOrTruncationOfActualFileRejectsAndRemainsSticky(int length)
    {
        using var fixture = new PortableFile([1, 2, 3]);
        using var reader = fixture.Capture();
        using (var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            writer.SetLength(length);
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
        File.WriteAllBytes(fixture.Path, fixture.Bytes);
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
    }

    [Fact]
    public void MutationInsideReadSamplingPreventsReturningAnyBytes()
    {
        using var fixture = new PortableFile([1, 2, 3]);
        var armed = false;
        using var reader = fixture.Capture(() =>
        {
            if (!armed) return;
            using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.SetLength(4);
        });
        armed = true;
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 2));
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 2));
    }

    [Fact]
    public void SampleFailureAndCancellationStayFailedAndCloseIsIdempotent()
    {
        using var fixture = new PortableFile([1, 2, 3]);
        var fail = false;
        using var reader = fixture.Capture(() => { if (fail) throw new IOException("sample"); });
        fail = true;
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
        fail = false;
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadChunk(0, 1));
        using var fixture2 = new PortableFile([1]);
        using var cancelledReader = fixture2.Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cancelledReader.ReadChunk(0, 1, cancellation.Token));
        Assert.Throws<IOException>(() => cancelledReader.ReadChunk(0, 1));
    }

    [Fact]
    public void ExpiredOriginalDeadlineClosesTransferredStreamAndCannotRenew()
    {
        using var fixture = new PortableFile([1]);
        var stream = fixture.Open();
        var handle = stream.SafeFileHandle;
        Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxRetainedReportSet.FileReader.CapturePortableFileForTesting(stream,
                new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.Zero)));
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void CaptureRejectsAnOversizedActualFileAndClosesItsTransferredHandle()
    {
        using var fixture = new PortableFile([1]);
        using (var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            writer.SetLength(LinuxRetainedReportSet.MaximumFileBytes + 1);
        var stream = fixture.Open();
        var handle = stream.SafeFileHandle;
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.FileReader.CapturePortableFileForTesting(stream, Deadline));
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureRejectsActualEofMutationAndClosesItsTransferredHandle(bool grow)
    {
        using var fixture = new PortableFile([1]);
        var stream = fixture.Open();
        var handle = stream.SafeFileHandle;
        var samples = 0;
        var mutated = false;
        Assert.Throws<IOException>(() => LinuxRetainedReportSet.FileReader.CapturePortableFileForTesting(stream, Deadline, () =>
        {
            // The existing sampling seam brackets real reads. Change the actual file after the
            // length sample immediately before reading, or immediately before the final EOF read.
            if (++samples != (grow ? 3 : 2)) return;
            using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.SetLength(grow ? 2 : 0);
            mutated = true;
        }));
        Assert.True(mutated);
        Assert.Equal(grow ? 3 : 2, samples);
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalChunkRejectsActualEofMutationAndPoisonsFurtherReads(bool grow)
    {
        using var fixture = new PortableFile([1]);
        var armed = false;
        var samples = 0;
        var mutated = false;
        using var reader = fixture.Capture(() =>
        {
            if (!armed || ++samples != (grow ? 3 : 2)) return;
            using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.SetLength(grow ? 2 : 0);
            mutated = true;
        });
        armed = true;
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
        Assert.True(mutated);
        Assert.Equal(grow ? 3 : 2, samples);
        File.WriteAllBytes(fixture.Path, fixture.Bytes);
        Assert.Throws<IOException>(() => reader.ReadChunk(0, 1));
    }

    [Fact]
    public async Task ConcurrentCloseWaitsForActualReadSamplingBeforeClosingTheHandle()
    {
        using var fixture = new PortableFile([1, 2, 3]);
        var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stream = fixture.Open();
        var handle = stream.SafeFileHandle;
        using var reader = LinuxRetainedReportSet.FileReader.CapturePortableFileForTesting(stream, Deadline, () =>
        {
            if (!armed) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("test barrier expired");
        });
        armed = true;
        var read = Task.Run(() => reader.ReadChunk(0, 1));
        Task? close = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            close = Task.Run(reader.Dispose);
            Assert.False(handle.IsClosed);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            release.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            if (close is not null) await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(handle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadChunk(1, 1));
    }

    private static LinuxProtectedMetadata Metadata() => new(1, 2, 3, 123, 456, 0x8180, 1, 3, 10, 2, 10, 3);

    private sealed class ChangingInventory : IReadOnlyList<string>
    {
        public int Reads { get; private set; }
        public int Count => Reads == 0 ? 1 : 2;
        public string this[int index] { get { Reads++; return "report.xml"; } }
        public IEnumerator<string> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PortableFile : IDisposable
    {
        internal PortableFile(byte[] bytes)
        {
            Bytes = bytes;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "retained-report-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(Path, bytes);
        }

        internal byte[] Bytes { get; }
        internal string Path { get; }
        internal FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        internal LinuxRetainedReportSet.FileReader Capture(Action? sample = null) =>
            LinuxRetainedReportSet.FileReader.CapturePortableFileForTesting(Open(), Deadline, sample);
        public void Dispose() => File.Delete(Path);
    }
}
