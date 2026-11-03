using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises retained Linux nodes against real root-owned filesystem objects.</summary>
/// <remarks>Actual filesystem controls only, not worker/application acceptance; skip without Linux x64 root.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxProtectedNodeNativeTests
{
    private const int SmallFileLimit = 64 * 1024;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ReadOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecutableFile = ReadOnlyDirectory;
    private const UnixFileMode GroupWritableFile = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

    [LinuxRootX64Fact]
    public void AbsoluteOpenHashesAndReadsTheExactRetainedBytesWithinBounds()
    {
        using var fixture = RootFixture.Create();
        var bytes = Encoding.UTF8.GetBytes(new string('p', 4096));
        fixture.WriteFile("payload.bin", bytes, ReadOnlyFile);

        var owned = new List<LinuxProtectedNode>();
        try
        {
            var node = LinuxProtectedNode.OpenAbsolute(fixture.PathFor("payload.bin"), directory: false,
                sharedRead: false, SmallFileLimit, owned, CancellationToken.None);

            Assert.Equal(0u, node.Metadata.Uid);
            Assert.Equal(1u, node.Metadata.LinkCount);
            Assert.Equal((ulong)bytes.Length, node.Metadata.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), node.Hash(CancellationToken.None));
            Assert.Equal(bytes, node.ReadBounded(bytes.Length, CancellationToken.None));
            Assert.Equal(bytes, node.ReadRequest(CancellationToken.None));
            var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));
            Assert.Equal(bytes, node.ReadManagedProbeBytes(CancellationToken.None, deadline));
            using (var destination = File.OpenHandle(fixture.PathFor("copied.bin"), FileMode.CreateNew, FileAccess.Write))
                node.CopyTo(destination, CancellationToken.None, deadline);
            Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor("copied.bin")));
            Assert.Throws<EvidenceAdmissionException>(() => node.ReadBounded(bytes.Length - 1, CancellationToken.None));
            node.Recheck();
        }
        finally
        {
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void OpenChildEnumeratesAndReadsActualProtectedDirectoriesAndFiles()
    {
        using var fixture = RootFixture.Create();
        fixture.CreateDirectory("nested", ReadOnlyDirectory);
        var contents = Encoding.UTF8.GetBytes("root-owned child bytes");
        fixture.WriteFile("payload.bin", contents, ReadOnlyFile);
        fixture.WriteFile("nested/inner.bin", contents, ReadOnlyFile);

        var owned = new List<LinuxProtectedNode>();
        LinuxProtectedNode? nested = null;
        LinuxProtectedNode? file = null;
        try
        {
            var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, directory: true,
                sharedRead: false, 0, owned, CancellationToken.None);

            Assert.True(root.IsChildDirectory("nested"));
            Assert.False(root.IsChildDirectory("payload.bin"));
            Assert.Equal(["nested", "payload.bin"], root.ReadNames(CancellationToken.None));

            nested = root.OpenChild("nested", directory: true, CancellationToken.None);
            Assert.Equal(["inner.bin"], nested.ReadNames(CancellationToken.None));
            file = nested.OpenChild("inner.bin", directory: false, CancellationToken.None);
            Assert.Equal(contents, file.ReadBounded(contents.Length, CancellationToken.None));
            file.Recheck();
            nested.Recheck();
        }
        finally
        {
            file?.Close();
            nested?.Close();
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void FileNodeCannotEnumerateAndDirectoryNodeCannotReadOrCopyPayloadBytes()
    {
        using var fixture = RootFixture.Create();
        var payload = "actual protected input"u8.ToArray();
        fixture.WriteFile("payload.bin", payload, ReadOnlyFile);
        var owned = new List<LinuxProtectedNode>();
        LinuxProtectedNode? file = null;
        try
        {
            var directory = LinuxProtectedNode.OpenAbsolute(fixture.Root, directory: true,
                sharedRead: false, 0, owned, CancellationToken.None);
            file = directory.OpenChild("payload.bin", directory: false, CancellationToken.None);
            Assert.Equal(["payload.bin"], directory.ReadNames(CancellationToken.None));
            Assert.Equal(payload, file.ReadBounded(payload.Length, CancellationToken.None));
            RequireRejected(() => file.ReadNames(CancellationToken.None));
            RequireRejected(() => directory.ReadBounded(1, CancellationToken.None));
            var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));
            RequireRejected(() => directory.ReadManagedProbeBytes(CancellationToken.None, deadline));
            using (var destination = File.OpenHandle(fixture.PathFor("destination.bin"), FileMode.CreateNew, FileAccess.Write))
            {
                RequireRejected(() => directory.CopyTo(destination, CancellationToken.None, deadline));
                Assert.False(destination.IsClosed);
            }
            Assert.Empty(File.ReadAllBytes(fixture.PathFor("destination.bin")));
            Assert.Equal(payload, file.ReadBounded(payload.Length, CancellationToken.None));
        }
        finally
        {
            file?.Close();
            LinuxProtectedDeployment.Close(owned);
        }

        static void RequireRejected(Action action)
        {
            var error = Assert.Throws<EvidenceAdmissionException>(action);
            Assert.Equal("ASEVD402", error.Code);
            Assert.Null(error.InnerException);
        }
    }

    [LinuxRootX64Fact]
    public void ActualRetainedFileRejectsInvalidReadBoundsWithoutChangingTheFileOrItsHandle()
    {
        using var fixture = RootFixture.Create();
        var payload = "unchanged retained bytes"u8.ToArray();
        fixture.WriteFile("payload.bin", payload, ReadOnlyFile);
        var owned = new List<LinuxProtectedNode>();
        try
        {
            var file = LinuxProtectedNode.OpenAbsolute(fixture.PathFor("payload.bin"), directory: false,
                sharedRead: false, SmallFileLimit, owned, CancellationToken.None);
            foreach (var maximum in new long[] { -1, 0, payload.Length - 1, EvidenceCanonicalJson.MaximumInputBytes + 1L })
            {
                var error = Assert.Throws<EvidenceAdmissionException>(() => file.ReadBounded(maximum, CancellationToken.None));
                Assert.Equal("ASEVD402", error.Code);
                Assert.Null(error.InnerException);
                Assert.Equal(payload, file.ReadBounded(payload.Length, CancellationToken.None));
                file.Recheck();
            }
            Assert.Equal(payload, File.ReadAllBytes(fixture.PathFor("payload.bin")));
        }
        finally
        {
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void OpenChildRejectsSymlinkHardLinkFifoEmptyAndWritableFilesWithoutChangingSentinel()
    {
        using var fixture = RootFixture.Create();
        var sentinel = fixture.CreateSentinel(Encoding.UTF8.GetBytes("outside sentinel remains unchanged"));
        fixture.WriteFile("hardlink-source", Encoding.UTF8.GetBytes("hardlinked bytes"), ReadOnlyFile);
        fixture.CreateHardLink("hardlink-source", "hardlink");
        fixture.CreateSymbolicLink("symlink", sentinel);
        fixture.CreateFifo("fifo");
        fixture.WriteFile("empty", [], ReadOnlyFile);
        fixture.WriteFile("writable", Encoding.UTF8.GetBytes("unsafe mode"), GroupWritableFile);

        var owned = new List<LinuxProtectedNode>();
        LinuxProtectedNode? root = null;
        try
        {
            root = LinuxProtectedNode.OpenAbsolute(fixture.Root, directory: true,
                sharedRead: false, 0, owned, CancellationToken.None);
            Assert.Equal(["empty", "fifo", "hardlink", "hardlink-source", "symlink", "writable"],
                root.ReadNames(CancellationToken.None));

            Assert.Throws<EvidenceAdmissionException>(() => root.OpenChild("empty", false, CancellationToken.None));
            Assert.Throws<EvidenceAdmissionException>(() => root.OpenChild("fifo", false, CancellationToken.None));
            Assert.Throws<EvidenceAdmissionException>(() => root.OpenChild("hardlink", false, CancellationToken.None));
            Assert.Throws<EvidenceAdmissionException>(() => root.OpenChild("writable", false, CancellationToken.None));
            Assert.Throws<IOException>(() => root.OpenChild("symlink", false, CancellationToken.None));
            Assert.Equal("outside sentinel remains unchanged", Encoding.UTF8.GetString(File.ReadAllBytes(sentinel)));
        }
        finally
        {
            root?.Close();
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void HashRejectsGrowthAfterOpeningTheActualFile()
    {
        using var fixture = RootFixture.Create();
        var initial = Encoding.UTF8.GetBytes("initial bytes");
        var path = fixture.WriteFile("growing.bin", initial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fixture.SetMode(path, ReadOnlyFile);

        var owned = new List<LinuxProtectedNode>();
        LinuxProtectedNode? root = null;
        LinuxProtectedNode? node = null;
        try
        {
            root = LinuxProtectedNode.OpenAbsolute(fixture.Root, directory: true,
                sharedRead: false, 0, owned, CancellationToken.None);
            node = root.OpenChild("growing.bin", directory: false, CancellationToken.None);
            writer.Position = writer.Length;
            writer.Write(Encoding.UTF8.GetBytes(" appended"));
            writer.Flush(flushToDisk: true);

            Assert.Throws<EvidenceAdmissionException>(() => node.Hash(CancellationToken.None));
        }
        finally
        {
            node?.Close();
            root?.Close();
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void RecheckRejectsAReplacedDirectoryNameAndCloseCanBeRepeated()
    {
        using var fixture = RootFixture.Create();
        var original = Encoding.UTF8.GetBytes("same bytes before and after replacement");
        var namedPath = fixture.WriteFile("replace.bin", original, ReadOnlyFile);
        var replacementPath = fixture.PathFor("replacement.bin");
        File.WriteAllBytes(replacementPath, original);
        fixture.SetMode(replacementPath, ReadOnlyFile);

        var owned = new List<LinuxProtectedNode>();
        LinuxProtectedNode? root = null;
        LinuxProtectedNode? node = null;
        try
        {
            root = LinuxProtectedNode.OpenAbsolute(fixture.Root, directory: true,
                sharedRead: false, 0, owned, CancellationToken.None);
            node = root.OpenChild("replace.bin", directory: false, CancellationToken.None);

            fixture.SetMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(namedPath, fixture.PathFor("original.bin"));
            File.Move(replacementPath, namedPath);
            fixture.SetMode(fixture.Root, PrivateDirectory);

            Assert.Throws<EvidenceAdmissionException>(() => node.Recheck());
            Assert.Equal(original, File.ReadAllBytes(namedPath));
        }
        finally
        {
            node?.Close();
            root?.Close();
            LinuxProtectedDeployment.Close(owned);
            LinuxProtectedDeployment.Close(owned);
        }
    }

    [LinuxRootX64Fact]
    public void DeploymentOpensHashesReadsAndRechecksAnActualSharedReadableTree()
    {
        using var fixture = RootFixture.Create();
        fixture.CreateDirectory("bin", ReadOnlyDirectory);
        var contents = Encoding.UTF8.GetBytes("measured deployment bytes");
        fixture.WriteFile("asset.dat", contents, ReadOnlyFile);
        fixture.WriteFile("bin/runner", Encoding.UTF8.GetBytes("executable tool bytes"), ExecutableFile);
        fixture.SetMode(fixture.Root, ReadOnlyDirectory);

        using var deployment = LinuxProtectedDeployment.Open(fixture.Root, CancellationToken.None);
        var digest = Convert.ToHexStringLower(SHA256.HashData(contents));
        Assert.Equal(digest, deployment.RequireFile("asset.dat"));
        Assert.Equal(digest, deployment.RequireFile("asset.dat", executable: false));
        Assert.Throws<EvidenceAdmissionException>(() => deployment.RequireFile("asset.dat", executable: true));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("executable tool bytes"))),
            deployment.RequireFile("bin/runner", executable: true));
        Assert.Throws<EvidenceAdmissionException>(() => deployment.RequireFile("missing.dat"));

        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1));
        Assert.Equal(contents, deployment.ReadFile("asset.dat", CancellationToken.None, deadline));
        Assert.Equal(contents.LongLength, deployment.RequireFileLength("asset.dat", CancellationToken.None, deadline));
        Assert.Throws<EvidenceAdmissionException>(() => deployment.RequireFileLength("missing.dat", CancellationToken.None, deadline));
        var copyPath = fixture.CreateSentinel("exclusive destination"u8.ToArray());
        using (var destination = File.OpenHandle(copyPath, FileMode.Truncate, FileAccess.Write))
            deployment.CopyFile("asset.dat", destination, CancellationToken.None, deadline);
        Assert.Equal(contents, File.ReadAllBytes(copyPath));
        deployment.Recheck(CancellationToken.None);
    }

    [LinuxRootX64Fact]
    public void DeploymentOpenRejectsEmptyHardLinkedFifoAndUnsafeModeFiles()
    {
        foreach (var kind in new[] { "empty", "hardlink", "fifo", "unsafe-mode" })
        {
            using var fixture = RootFixture.Create();
            switch (kind)
            {
                case "empty":
                    fixture.WriteFile("entry", [], ReadOnlyFile);
                    break;
                case "hardlink":
                    fixture.WriteFile("source", Encoding.UTF8.GetBytes("linked"), ReadOnlyFile);
                    fixture.CreateHardLink("source", "entry");
                    break;
                case "fifo":
                    fixture.CreateFifo("entry");
                    break;
                case "unsafe-mode":
                    fixture.WriteFile("entry", Encoding.UTF8.GetBytes("writable"), GroupWritableFile);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected native fixture case.");
            }
            fixture.SetMode(fixture.Root, ReadOnlyDirectory);

            Assert.Throws<EvidenceAdmissionException>(() =>
                LinuxProtectedDeployment.Open(fixture.Root, CancellationToken.None));
        }
    }

    [LinuxRootX64Fact]
    public void DeploymentRejectsSymlinkAndLeavesItsOutsideSentinelUnchanged()
    {
        using var fixture = RootFixture.Create();
        var sentinel = fixture.CreateSentinel(Encoding.UTF8.GetBytes("deployment sentinel"));
        fixture.CreateSymbolicLink("entry", sentinel);
        fixture.SetMode(fixture.Root, ReadOnlyDirectory);

        Assert.Throws<IOException>(() => LinuxProtectedDeployment.Open(fixture.Root, CancellationToken.None));
        Assert.Equal("deployment sentinel", Encoding.UTF8.GetString(File.ReadAllBytes(sentinel)));
    }

    [LinuxRootX64Fact]
    public void DeploymentRecheckRejectsNameReplacementAndDisposedDeploymentRejectsReads()
    {
        using var fixture = RootFixture.Create();
        var contents = Encoding.UTF8.GetBytes("same measured bytes");
        var namedPath = fixture.WriteFile("asset.dat", contents, ReadOnlyFile);
        fixture.SetMode(fixture.Root, ReadOnlyDirectory);

        var deployment = LinuxProtectedDeployment.Open(fixture.Root, CancellationToken.None);
        var replacementPath = fixture.PathFor("replacement.dat");
        try
        {
            fixture.SetMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(namedPath, fixture.PathFor("original.dat"));
            File.WriteAllBytes(replacementPath, contents);
            fixture.SetMode(replacementPath, ReadOnlyFile);
            File.Move(replacementPath, namedPath);
            fixture.SetMode(fixture.Root, ReadOnlyDirectory);

            Assert.Throws<EvidenceAdmissionException>(() => deployment.Recheck(CancellationToken.None));
        }
        finally
        {
            deployment.Dispose();
            deployment.Dispose();
        }

        Assert.Throws<EvidenceAdmissionException>(() => deployment.RequireFile("asset.dat"));
        Assert.Throws<EvidenceAdmissionException>(() => deployment.ReadFile("asset.dat", CancellationToken.None,
            new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1))));
        Assert.Throws<EvidenceAdmissionException>(() => deployment.Recheck(CancellationToken.None));
    }

    [LinuxRootX64Fact]
    public void ProducerCopiesNestedAuditedBytesIntoTwoActualDirectoriesWithOneBudget()
    {
        using var fixture = CopyFixture(("nested/a.bin", "abc"u8.ToArray()), ("nested/z.bin", "defg"u8.ToArray()));
        using var first = CopyDestination(fixture, "first");
        using var second = CopyDestination(fixture, "second");
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        var budget = new LinuxProducerCopyBudget();
        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));
        LinuxProducerFileTree.Copy(source, first, CopyUid, CopyGid, budget, deadline, default);
        LinuxProducerFileTree.Copy(source, second, CopyUid, CopyGid, budget, deadline, default);
        Assert.Equal(8, budget.Nodes);
        Assert.Equal(14, budget.Bytes);
        foreach (var name in new[] { "first", "second" })
        {
            var root = fixture.PathFor("private/" + name);
            Assert.Equal(new[] { "nested" }, Directory.GetDirectories(root).Select(System.IO.Path.GetFileName));
            AssertCopyMetadata(root + "/nested", directory: true);
            AssertCopyMetadata(root + "/nested/a.bin", directory: false);
            AssertCopyMetadata(root + "/nested/z.bin", directory: false);
            Assert.Equal("abc"u8.ToArray(), File.ReadAllBytes(root + "/nested/a.bin"));
            Assert.Equal("defg"u8.ToArray(), File.ReadAllBytes(root + "/nested/z.bin"));
        }
        Assert.False(first.IsClosed);
        Assert.False(second.IsClosed);
        Assert.Equal(CopyUid, InspectCopyDescriptor(first).Uid);
        source.Recheck(default);
        Assert.Equal("abc", File.ReadAllText(fixture.PathFor("source/nested/a.bin")));
    }

    [LinuxRootX64Fact]
    public void LaterCopyCollisionPreservesEarlierCopyOriginalSentinelAndAllReservations()
    {
        using var fixture = CopyFixture(("a.bin", "abc"u8.ToArray()), ("z.bin", "defg"u8.ToArray()));
        using var destination = CopyDestination(fixture, "selected");
        var sentinel = fixture.WriteFile("private/selected/z.bin", "outside original"u8.ToArray(), ReadOnlyFile);
        using var original = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Read);
        var before = InspectCopyDescriptor(original);
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        var budget = new LinuxProducerCopyBudget();
        Assert.Throws<IOException>(() => LinuxProducerFileTree.Copy(source, destination, CopyUid, CopyGid,
            budget, new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)), default));
        Assert.Equal(3, budget.Nodes);
        Assert.Equal(7, budget.Bytes);
        Assert.Equal("abc", File.ReadAllText(fixture.PathFor("private/selected/a.bin")));
        Assert.Equal("outside original", File.ReadAllText(sentinel));
        using var named = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Read);
        Assert.Equal(before, InspectCopyDescriptor(named));
        Assert.Equal(before, InspectCopyDescriptor(original));
        Assert.False(destination.IsClosed);
        source.Recheck(default);
    }

    [LinuxRootX64Fact]
    public void NestedCopyCollisionCannotAdoptAnExistingSymlinkOrChangeItsOutsideDirectory()
    {
        using var fixture = CopyFixture(("nested/a.bin", "abc"u8.ToArray()));
        using var destination = CopyDestination(fixture, "selected");
        fixture.CreateDirectory("outside", PrivateDirectory);
        var sentinel = fixture.WriteFile("outside/sentinel", "outside bytes"u8.ToArray(), ReadOnlyFile);
        using var original = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Read);
        var before = InspectCopyDescriptor(original);
        fixture.CreateSymbolicLink("private/selected/nested", fixture.PathFor("outside"));
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        var budget = new LinuxProducerCopyBudget();
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProducerFileTree.Copy(source, destination,
            CopyUid, CopyGid, budget, new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)), default));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal(2, budget.Nodes);
        Assert.Equal(0, budget.Bytes);
        Assert.False(File.Exists(fixture.PathFor("outside/a.bin")));
        Assert.Equal("outside bytes", File.ReadAllText(sentinel));
        Assert.Equal(before, InspectCopyDescriptor(original));
        Assert.NotNull(new DirectoryInfo(fixture.PathFor("private/selected/nested")).LinkTarget);
        Assert.False(destination.IsClosed);
    }

    [LinuxRootX64Fact]
    public void ReplacedRetainedCopySourceRejectsBeforeDestinationCreationOrFileReservation()
    {
        using var fixture = CopyFixture(("payload.bin", "original bytes"u8.ToArray()));
        using var destination = CopyDestination(fixture, "selected");
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        File.Move(fixture.PathFor("source/payload.bin"), fixture.PathFor("private/held-original"));
        fixture.WriteFile("source/payload.bin", "original bytes"u8.ToArray(), ReadOnlyFile);
        var budget = new LinuxProducerCopyBudget();
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProducerFileTree.Copy(source, destination,
            CopyUid, CopyGid, budget, new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)), default));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal(1, budget.Nodes);
        Assert.Equal(0, budget.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.PathFor("private/selected")));
        Assert.Equal("original bytes", File.ReadAllText(fixture.PathFor("private/held-original")));
        Assert.Equal("original bytes", File.ReadAllText(fixture.PathFor("source/payload.bin")));
        Assert.False(destination.IsClosed);
    }

    [LinuxRootX64Fact]
    public void CancelledCopyKeepsTheOriginalTokenUntouchedDestinationAndZeroReservations()
    {
        using var fixture = CopyFixture(("payload.bin", "original bytes"u8.ToArray()));
        using var destination = CopyDestination(fixture, "selected");
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var budget = new LinuxProducerCopyBudget();
        var error = Assert.Throws<OperationCanceledException>(() => LinuxProducerFileTree.Copy(source, destination,
            CopyUid, CopyGid, budget, new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)), cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(0, budget.Nodes);
        Assert.Equal(0, budget.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.PathFor("private/selected")));
        Assert.False(destination.IsClosed);
        Assert.Equal(CopyUid, InspectCopyDescriptor(destination).Uid);
        source.Recheck(default);
    }

    [LinuxRootX64Fact]
    public void RealKernelWriteFailureLeavesTheBorrowedAuditedSourceUsableForAnIndependentDestination()
    {
        var bytes = Enumerable.Range(0, 65553).Select(static index => (byte)(index % 251)).ToArray();
        using var fixture = CopyFixture(("payload.bin", bytes));
        using var source = LinuxProtectedDeployment.Open(fixture.PathFor("source"), default);
        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));
        using (var good = File.OpenHandle(fixture.PathFor("private/first.bin"), FileMode.CreateNew, FileAccess.Write))
            source.CopyFile("payload.bin", good, default, deadline);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor("private/first.bin")));

        // Actual kernel character-device rejection, not an injected callback or a producer/account claim.
        // This controls the retained-file I/O failure path; it does not model a regular destination's disk quota.
        using (var full = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write))
        {
            Assert.Equal(0x2000, InspectCopyDescriptor(full).Mode & 0xf000);
            Assert.ThrowsAny<IOException>(() => source.CopyFile("payload.bin", full, default, deadline));
            Assert.False(full.IsClosed);
        }
        source.Recheck(default);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor("source/payload.bin")));
        using (var fresh = File.OpenHandle(fixture.PathFor("private/second.bin"), FileMode.CreateNew, FileAccess.Write))
            source.CopyFile("payload.bin", fresh, default, deadline);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor("private/second.bin")));
    }

    [LinuxRootX64Fact]
    public async Task ActualRootPipeFactoryKeepsCollectionAndDisposalPendingUntilTheLastDuplicateWriterCloses()
    {
        LinuxOutputPipes? pipes = null;
        FileStream? outputWriter = null;
        FileStream? errorWriter = null;
        Task<SupervisionOutputReceipt>? collection = null;
        Task? disposal = null;
        var guard = TimeSpan.FromSeconds(10);
        try
        {
            pipes = LinuxOutputPipes.Create();
            var originalOutput = pipes.StandardOutput;
            var originalError = pipes.StandardError;
            outputWriter = DuplicatePipeWriter(originalOutput);
            errorWriter = DuplicatePipeWriter(originalError);
            var first = InspectCopyDescriptor(originalOutput);
            var second = InspectCopyDescriptor(originalError);
            Assert.Equal(0x1000, first.Mode & 0xf000);
            Assert.Equal(0x1000, second.Mode & 0xf000);
            Assert.NotEqual(first.Inode, second.Inode);
            Assert.Equal(first, InspectCopyDescriptor(outputWriter.SafeFileHandle));
            Assert.Equal(second, InspectCopyDescriptor(errorWriter.SafeFileHandle));
            var descriptorFlags = PipeFcntl(outputWriter.SafeFileHandle, 1, 0);
            Assert.True(descriptorFlags >= 0);
            Assert.Equal(1, descriptorFlags & 1); // Actual FD_CLOEXEC, never a failed -1 query.
            collection = pipes.BeginCollectAsync(default, receivedByteLimit: 128, prefixByteLimit: 64);
            Assert.Same(collection, pipes.JoinAsync());
            await outputWriter.WriteAsync("actual output"u8.ToArray());
            await outputWriter.FlushAsync();
            await errorWriter.WriteAsync("actual error"u8.ToArray());
            await errorWriter.FlushAsync();
            errorWriter.Dispose();
            errorWriter = null;
            pipes.CloseWriteCopies();
            Assert.True(originalOutput.IsClosed);
            Assert.True(originalError.IsClosed);
            Assert.False(collection.IsCompleted); // The genuine duplicate still holds the stdout write endpoint.
            disposal = pipes.DisposeAsync().AsTask();
            Assert.Same(disposal, pipes.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);
            outputWriter.Dispose();
            outputWriter = null;
            var receipt = await collection.WaitAsync(guard);
            await disposal.WaitAsync(guard);
            Assert.True(receipt.Successful);
            Assert.True(receipt.Stdout.EndOfStream);
            Assert.True(receipt.Stderr.EndOfStream);
            Assert.Equal(25, receipt.ReceivedBytes);
            Assert.Equal("actual output"u8.ToArray(), receipt.Stdout.Prefix.ToArray());
            Assert.Equal("actual error"u8.ToArray(), receipt.Stderr.Prefix.ToArray());
        }
        finally
        {
            try { outputWriter?.Dispose(); }
            finally
            {
                try { errorWriter?.Dispose(); }
                finally
                {
                    if (pipes is not null)
                    {
                        try { pipes.CloseWriteCopies(); }
                        finally
                        {
                            try { if (collection is not null) await collection.WaitAsync(guard); }
                            finally { await (disposal ?? pipes.DisposeAsync().AsTask()).WaitAsync(guard); }
                        }
                    }
                }
            }
        }
    }

    private static FileStream DuplicatePipeWriter(SafeFileHandle borrowed)
    {
        var descriptor = PipeFcntl(borrowed, 1030, 3); // F_DUPFD_CLOEXEC; the original handle stays borrowed.
        Assert.True(descriptor >= 0);
        var owned = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try { return new FileStream(owned, FileAccess.Write, bufferSize: 4096); }
        catch { owned.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int PipeFcntl(SafeFileHandle handle, int command, int value);

    // These IDs describe actual test-owned filesystem nodes, never authenticated or reserved run accounts.
    private const uint CopyUid = 12345;
    private const uint CopyGid = 12346;

    private static RootFixture CopyFixture(params (string Relative, byte[] Bytes)[] files)
    {
        var fixture = RootFixture.Create();
        try
        {
            fixture.SetMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            fixture.CreateDirectory("source", ReadOnlyDirectory);
            fixture.CreateDirectory("private", PrivateDirectory);
            foreach (var file in files) fixture.WriteFile("source/" + file.Relative, file.Bytes, ReadOnlyFile);
            foreach (var directory in Directory.EnumerateDirectories(fixture.PathFor("source"), "*", SearchOption.AllDirectories))
                fixture.SetMode(directory, ReadOnlyDirectory);
            fixture.SetMode(fixture.PathFor("source"), ReadOnlyDirectory);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static SafeFileHandle CopyDestination(RootFixture fixture, string name)
    {
        fixture.CreateDirectory("private/" + name, PrivateDirectory);
        var path = fixture.PathFor("private/" + name);
        Assert.Equal(0, ChownCopyDirectory(path, CopyUid, CopyGid));
        var fd = OpenCopyDirectory(path, 0x10000 | 0x80000);
        Assert.True(fd >= 0);
        return new((nint)fd, ownsHandle: true);
    }

    private static void AssertCopyMetadata(string path, bool directory)
    {
        using var handle = directory
            ? new SafeFileHandle((nint)OpenCopyDirectory(path, 0x10000 | 0x80000), ownsHandle: true)
            : File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        Assert.False(handle.IsInvalid);
        var actual = InspectCopyDescriptor(handle);
        Assert.Equal(CopyUid, actual.Uid);
        Assert.Equal(CopyGid, actual.Gid);
        Assert.Equal((ushort)(directory ? 0x41c0 : 0x8180), actual.Mode);
        if (!directory) Assert.Equal(1u, actual.LinkCount);
    }

    private static LinuxProtectedMetadata InspectCopyDescriptor(SafeFileHandle handle)
    {
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            Assert.Equal(0, StatxCopy(handle.DangerousGetHandle().ToInt32(), string.Empty, 0x1000, 0x7ff, out var value));
            return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid,
                value.Mode, value.Nlink, value.Size, value.ChangeSeconds, value.ChangeNanoseconds,
                value.ModifySeconds, value.ModifyNanoseconds);
        }
        finally { if (held) handle.DangerousRelease(); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct CopyStatxData
    {
        [FieldOffset(16)] internal uint Nlink;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(24)] internal uint Gid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangeSeconds;
        [FieldOffset(104)] internal uint ChangeNanoseconds;
        [FieldOffset(112)] internal long ModifySeconds;
        [FieldOffset(120)] internal uint ModifyNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxCopy(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out CopyStatxData value);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChownCopyDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenCopyDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed class LinuxRootX64FactAttribute : FactAttribute
    {
        public LinuxRootX64FactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || GetEffectiveUid() != 0)
                Skip = "Requires native Linux x64 with effective UID 0; a skipped case is not native acceptance evidence.";
        }
    }

    private sealed class RootFixture : IDisposable
    {
        private readonly List<string> _siblings = [];

        private RootFixture(string path) => Root = path;

        internal string Root { get; }

        internal string PathFor(string name) => System.IO.Path.Combine(Root, name);

        internal static RootFixture Create()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            var root = System.IO.Path.Combine("/run", $"issue779-node-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root, PrivateDirectory);
            File.SetUnixFileMode(root, PrivateDirectory);
            return new(root);
        }

        internal string WriteFile(string name, byte[] contents, UnixFileMode mode)
        {
            var path = PathFor(name);
            var parent = System.IO.Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent, PrivateDirectory);
            File.WriteAllBytes(path, contents);
            SetMode(path, mode);
            return path;
        }

        internal void CreateDirectory(string name, UnixFileMode mode)
        {
            var path = PathFor(name);
            Directory.CreateDirectory(path, PrivateDirectory);
            SetMode(path, mode);
        }

        internal string CreateSentinel(byte[] contents)
        {
            var path = Root + "-sentinel";
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(contents);
            SetMode(path, ReadOnlyFile);
            _siblings.Add(path);
            return path;
        }

        internal void CreateHardLink(string existingName, string newName)
        {
            var source = PathFor(existingName);
            var destination = PathFor(newName);
            if (Link(source, destination) != 0)
                throw new IOException($"Creating the test-owned hard link failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        internal void CreateSymbolicLink(string name, string target) =>
            File.CreateSymbolicLink(PathFor(name), target);

        internal void CreateFifo(string name)
        {
            if (MakeFifo(PathFor(name), 0x180) != 0)
                throw new IOException($"Creating the test-owned FIFO failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        internal void SetMode(string path, UnixFileMode mode) => ApplyMode(path, mode);

        public void Dispose()
        {
            if (Directory.Exists(Root)) DeleteOwnedTree(Root);
            foreach (var sibling in _siblings)
                if (File.Exists(sibling)) File.Delete(sibling);
        }

        private static void DeleteOwnedTree(string directory)
        {
            ApplyMode(directory, PrivateDirectory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                    DeleteOwnedTree(entry);
                else File.Delete(entry);
            }
            Directory.Delete(directory);
        }

        private static void ApplyMode(string path, UnixFileMode mode)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(path, mode);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
