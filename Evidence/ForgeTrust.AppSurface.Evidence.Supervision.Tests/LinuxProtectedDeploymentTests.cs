using System.Buffers.Binary;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Metadata and kernel-record grammar controls; these tests issue no retained deployment or admission.</summary>
public sealed class LinuxProtectedDeploymentTests
{
    [Fact]
    public void OrdinaryProtectedMetadataAcceptsOnlyTheExpectedTypeAndSharedAccess()
    {
        Directory().Require(true, true, 0);
        File().Require(false, true, 10);
        (Directory() with { Mode = 0x41c0 }).Require(true, false, 0); // Private root 0700.
        (File() with { Mode = 0x8180 }).Require(false, false, 10); // Private root 0600.
        Reject(Directory() with { Mode = 0x41c0 }, true, true, 0);
        Reject(File() with { Mode = 0x8180 }, false, true, 10);
    }

    [Fact]
    public void SearchOnlyAncestorsDoNotRequireDirectoryInventoryReadAccess()
    {
        var searchOnly = Directory() with { Mode = 0x41c9 }; // Root 0711 outer directory.
        searchOnly.RequireAncestor(true);
        Assert.Throws<EvidenceAdmissionException>(() => searchOnly.Require(true, true, 0));
        (Directory() with { Mode = 0x41c0 }).RequireAncestor(false);
        Assert.Throws<EvidenceAdmissionException>(() => (Directory() with { Mode = 0x41c0 }).RequireAncestor(true));
        Assert.Throws<EvidenceAdmissionException>(() => (searchOnly with { Uid = 1 }).RequireAncestor(true));
        Assert.Throws<EvidenceAdmissionException>(() => (searchOnly with { Mode = 0x41db }).RequireAncestor(true));
        var churn = searchOnly with { Length = 8192, LinkCount = 3, ChangeSeconds = 2, ModifySeconds = 2 };
        churn.RequireAncestor(true);
        Assert.True(churn.SameAncestorAs(searchOnly));
        Assert.False((churn with { Inode = 4 }).SameAncestorAs(searchOnly));
        Assert.False((churn with { Gid = 4 }).SameAncestorAs(searchOnly));
        Assert.False((churn with { Mode = 0x41ed }).SameAncestorAs(searchOnly));
    }

    [Fact]
    public void InputDeadlineChecksActualMonotonicExpiryAndCancellationWithoutIo()
    {
        var now = TimeProvider.System.GetTimestamp();
        new LinuxInputDeadline(now, TimeSpan.FromSeconds(30)).Check(default);
        Assert.Throws<EvidenceAdmissionException>(() => new LinuxInputDeadline(now, TimeSpan.Zero).Check(default));
        Assert.Throws<EvidenceAdmissionException>(() => new LinuxInputDeadline(now, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)).Check(default));
        Assert.Throws<EvidenceAdmissionException>(() => new LinuxInputDeadline(0, TimeSpan.FromTicks(1)).Check(default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LinuxInputDeadline(now, TimeSpan.FromSeconds(30)).Check(cancelled.Token));
    }

    [Theory]
    [InlineData(0x41ff)] // Directory 0777.
    [InlineData(0x43ed)] // Directory sticky 0755 is still root/untrusted-unwritable.
    [InlineData(0x45ed)] // setgid.
    [InlineData(0x49ed)] // setuid.
    [InlineData(0x81a4)] // File supplied where a directory is required.
    [InlineData(0x40ed)] // Owner cannot read/search.
    public void DirectoryMetadataRejectsUnsafePolicies(int mode)
    {
        var value = Directory() with { Mode = (ushort)mode };
        if (mode == 0x43ed) value.Require(true, true, 0);
        else Reject(value, true, true, 0);
    }

    [Theory]
    [InlineData(0x81b4)] // group write.
    [InlineData(0x81a6)] // other write.
    [InlineData(0x91a4)] // FIFO.
    [InlineData(0x61a4)] // Block device.
    [InlineData(0xa1a4)] // Symlink.
    [InlineData(0x41ed)] // Directory.
    [InlineData(0x88a4)] // Setuid and owner read absent.
    public void FileMetadataRejectsUnsafeTypesAndModes(int mode) =>
        Reject(File() with { Mode = (ushort)mode }, false, true, 10);

    [Fact]
    public void FileBoundsOwnerAndLinksAreCheckedIndependently()
    {
        Reject(File() with { Uid = 1 }, false, true, 10);
        Reject(File() with { Inode = 0 }, false, true, 10);
        Reject(File() with { LinkCount = 0 }, false, true, 10);
        Reject(File() with { LinkCount = 2 }, false, true, 10);
        Reject(File() with { Length = 0 }, false, true, 10);
        Reject(File() with { Length = 11 }, false, true, 10);
        Reject(File(), false, true, 0);
        Reject(File(), false, true, -1);
        (File() with { Length = 10 }).Require(false, true, 10);
        Reject(Directory() with { Uid = 1 }, true, true, 0);
        Reject(Directory() with { Inode = 0 }, true, true, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("relative")]
    [InlineData("/one//two")]
    [InlineData("/one/./two")]
    [InlineData("/one/../two")]
    [InlineData("/one/two/")]
    [InlineData("/one/canary\n")]
    [InlineData("/one/canary\\name")]
    [InlineData("/one/canary:name")]
    public void UnsafeAbsoluteSyntaxRejectsBeforeAnyNativeOpen(string path)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProtectedDeployment.ValidateAbsolute(path));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Fact]
    public void AbsoluteSyntaxChecksUtf8AndComponentLimits()
    {
        LinuxProtectedDeployment.ValidateAbsolute("/run/protected/input.json");
        LinuxProtectedDeployment.ValidateAbsolute("/" + new string('a', 255));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxProtectedDeployment.ValidateAbsolute("/bad/\ud800"));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxProtectedDeployment.ValidateAbsolute("/" + new string('a', 256)));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxProtectedDeployment.ValidateAbsolute("/" + string.Join('/', Enumerable.Repeat(new string('a', 255), 17))));
    }

    [Fact]
    public void KernelDirectoryGrammarReturnsSortedDetachedNamesAndIgnoresDots()
    {
        var bytes = Block(".", "..", "z.dll", "a.dll");
        var names = LinuxDirectoryData.Parse(bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(["a.dll", "z.dll"], names);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)names)[0] = "canary");
        Assert.Empty(LinuxDirectoryData.Parse([]));
    }

    [Theory]
    [InlineData("canary/name")]
    [InlineData("canary\\name")]
    [InlineData("canary:name")]
    [InlineData("canary\n")]
    public void KernelDirectoryNamesRejectUnsafeCharacters(string name)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(Block(name)));
        Assert.Equal("ASEVD402", error.Code);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Fact]
    public void KernelDirectoryRecordBoundsDuplicatesAndUtf8FailClosed()
    {
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(new byte[19]));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(new byte[32 * 1024 + 1]));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(Block("same", "same")));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(Block(new string('a', 256))));
        var bytes = Block("one");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 23);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 32);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(bytes));
        bytes = Block("one");
        Array.Fill(bytes, (byte)65, 19, bytes.Length - 19);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(bytes));
        bytes = Block("one"); bytes[19] = 0;
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(bytes));
        bytes = Block("one"); bytes[19] = 0xff;
        Assert.Throws<EvidenceAdmissionException>(() => LinuxDirectoryData.Parse(bytes));
    }

    private static LinuxProtectedMetadata Directory() => new(1, 1, 2, 0, 0, 0x41ed, 2, 4096, 1, 0, 1, 0);
    private static LinuxProtectedMetadata File() => new(1, 1, 3, 0, 0, 0x81a4, 1, 1, 1, 0, 1, 0);
    private static void Reject(LinuxProtectedMetadata value, bool directory, bool sharedRead, long maximumBytes) =>
        Assert.Throws<EvidenceAdmissionException>(() => value.Require(directory, sharedRead, maximumBytes));

    private static byte[] Block(params string[] names)
    {
        using var output = new MemoryStream();
        foreach (var name in names)
        {
            var encoded = Encoding.UTF8.GetBytes(name);
            var length = (19 + encoded.Length + 1 + 7) / 8 * 8;
            var record = new byte[length];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), checked((ushort)length));
            encoded.CopyTo(record, 19);
            output.Write(record);
        }
        return output.ToArray();
    }
}
