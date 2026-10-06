using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable layout and sampled-policy controls; none creates a native workspace, account or lease.</summary>
public sealed class LinuxRunWorkspaceTests
{
    private static readonly Guid Id = Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574");
    private static readonly LinuxRunAccountSnapshot Accounts = new(65010, 65011, 65012, 65013, 65014);

    [Fact]
    public void ClosedLayoutSeparatesWorkerAndSubjectDirectoriesWithoutAllocatingArtifactSlot()
    {
        var layout = LinuxWorkspaceLayout.Create(Id, Accounts);
        Assert.Equal("appsurface-evidence-4523b7bf72a34fc7a36786857556e574", layout.Name);
        Assert.Equal("/run/" + layout.Name, layout.Root);
        Assert.Equal(layout.Root + "/worker/broker/control.sock", layout.ControlSocket);
        Assert.InRange(Encoding.UTF8.GetByteCount(layout.ControlSocket), 1, 100);
        Assert.Equal(layout.Root + "/worker/worker-control.json", layout.DescriptorPath);
        Assert.Equal(layout.Root + "/output", layout.OutputParent);
        Assert.Equal(layout.Root + "/raw-results", layout.RawResultsRoot);
        Assert.Equal("evidence", LinuxWorkspaceLayout.ArtifactSlot);
        Assert.Equal(new[]
        {
            new LinuxWorkspaceDirectoryPolicy("", null, 0, 65011, 0x1e8),
            new LinuxWorkspaceDirectoryPolicy("worker", "", 0, 65011, 0x1c8),
            new LinuxWorkspaceDirectoryPolicy("broker", "worker", 0, 65011, 0x1c8),
            new LinuxWorkspaceDirectoryPolicy("output", "", 65010, 65011, 0x1c0),
            new LinuxWorkspaceDirectoryPolicy("raw-results", "", 65012, 65014, 0x1c8),
        }, layout.Directories);
        Assert.DoesNotContain(layout.Directories, row => row.Name == LinuxWorkspaceLayout.ArtifactSlot);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LinuxWorkspaceDirectoryPolicy>)layout.Directories)[0] = layout.Directories[1]);
    }

    [Fact]
    public void ControlRootMatchesDescriptorContractAndBrokerCannotUseGenerationOrOutputAsParent()
    {
        var layout = LinuxWorkspaceLayout.Create(Id, Accounts);
        Assert.Equal(layout.Root + "/worker", layout.ControlRoot);
        Assert.Equal(layout.ControlRoot + "/worker-control.json", layout.DescriptorPath);
        Assert.Equal(layout.ControlRoot + "/broker/control.sock", layout.ControlSocket);
        Assert.False(Overlaps(layout.ControlRoot, layout.OutputParent));
        Assert.True(Overlaps(layout.Root, layout.OutputParent)); // The previous descriptor parent was invalid.
        Assert.Null(layout.GenerationDirectory.ParentName);
        Assert.Equal("", layout.ControlDirectory.ParentName);
        Assert.Equal("worker", layout.BrokerDirectory.ParentName);
        Assert.Equal("", layout.OutputDirectory.ParentName);
        Assert.Equal("", layout.RawResultsDirectory.ParentName);
        LinuxWorkspaceLayout.RequireParent(layout.GenerationDirectory, null);
        LinuxWorkspaceLayout.RequireParent(layout.ControlDirectory, layout.GenerationDirectory);
        LinuxWorkspaceLayout.RequireParent(layout.BrokerDirectory, layout.ControlDirectory);
        LinuxWorkspaceLayout.RequireParent(layout.OutputDirectory, layout.GenerationDirectory);
        LinuxWorkspaceLayout.RequireParent(layout.RawResultsDirectory, layout.GenerationDirectory);
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.BrokerDirectory, layout.GenerationDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.BrokerDirectory, layout.OutputDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.ControlDirectory, layout.OutputDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.OutputDirectory, layout.ControlDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.RawResultsDirectory, layout.ControlDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.GenerationDirectory, layout.ControlDirectory));
        Reject(() => LinuxWorkspaceLayout.RequireParent(layout.ControlDirectory, null));
    }

    [Fact]
    public void GenerationAndIdentityDataAreCopiedAndCannotSelectAnArbitraryPath()
    {
        var layout = LinuxWorkspaceLayout.Create(Id, Accounts);
        var other = LinuxWorkspaceLayout.Create(Guid.Parse("13df3145-cf2c-4b04-8ca1-43f677d095a6"), Accounts);
        Assert.NotEqual(layout.Root, other.Root);
        var changed = Accounts with { WorkerGid = 65111 };
        Assert.Equal(65011u, layout.WorkerGid);
        Assert.Equal(65111u, LinuxWorkspaceLayout.Create(Id, changed).WorkerGid);
        Reject(() => LinuxWorkspaceLayout.Create(Guid.Empty, Accounts));
        Reject(() => LinuxWorkspaceLayout.Create(Id, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EverySelectedIdRejectsRootAndUnmappedValue(int position)
    {
        foreach (var invalid in new[] { 0u, uint.MaxValue })
        {
            var value = position switch
            {
                0 => Accounts with { WorkerUid = invalid },
                1 => Accounts with { WorkerGid = invalid },
                2 => Accounts with { SubjectUid = invalid },
                3 => Accounts with { SubjectGid = invalid },
                _ => Accounts with { ResultsGid = invalid },
            };
            Reject(() => LinuxWorkspaceLayout.Create(Id, value));
        }
        Assert.Equal(5, LinuxWorkspaceLayout.Create(Id, Accounts).Directories.Count);
    }

    [Fact]
    public void WorkerSubjectAndResultsCannotAliasRequiredDistinctIdentities()
    {
        foreach (var invalid in new[]
        {
            Accounts with { SubjectUid = Accounts.WorkerUid },
            Accounts with { SubjectGid = Accounts.WorkerGid },
            Accounts with { ResultsGid = Accounts.WorkerGid },
            Accounts with { ResultsGid = Accounts.SubjectGid },
        }) Reject(() => LinuxWorkspaceLayout.Create(Id, invalid));
    }

    [Fact]
    public void DirectoryChildChurnDoesNotInvalidatePinnedIdentityOrAccess()
    {
        foreach (var policy in LinuxWorkspaceLayout.Create(Id, Accounts).Directories)
        {
            var before = Directory(policy);
            var after = before with
            {
                LinkCount = 9, Length = 8192, ChangeSeconds = 4, ChangeNanoseconds = 8,
                ModifySeconds = 9, ModifyNanoseconds = 10,
            };
            LinuxWorkspaceLayout.RequireDirectory(after, policy);
            Assert.True(after.SameAncestorAs(before));
            Assert.False((after with { Inode = after.Inode + 1 }).SameAncestorAs(before));
            Assert.False((after with { DeviceMinor = after.DeviceMinor + 1 }).SameAncestorAs(before));
            Assert.False((after with { Uid = after.Uid + 1 }).SameAncestorAs(before));
            Assert.False((after with { Gid = after.Gid + 1 }).SameAncestorAs(before));
            Assert.False((after with { Mode = (ushort)(after.Mode | 0x2) }).SameAncestorAs(before));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DirectoryRequiresExactOwnerGroupTypeAndModes(int index)
    {
        var policy = LinuxWorkspaceLayout.Create(Id, Accounts).Directories[index];
        var valid = Directory(policy);
        LinuxWorkspaceLayout.RequireDirectory(valid, policy);
        foreach (var invalid in new[]
        {
            valid with { Uid = valid.Uid + 1 }, valid with { Gid = valid.Gid + 1 },
            valid with { Inode = 0 }, valid with { Mode = (ushort)(valid.Mode | 0x10) },
            valid with { Mode = (ushort)(valid.Mode | 0x2) },
            valid with { Mode = (ushort)(valid.Mode | 0x800) },
            valid with { Mode = (ushort)(0x8000 | policy.Permissions) },
            valid with { Mode = (ushort)(0xa000 | policy.Permissions) },
        }) Reject(() => LinuxWorkspaceLayout.RequireDirectory(invalid, policy));
    }

    [Theory]
    [InlineData(0x41c9)] // 0711 searchable root ancestor.
    [InlineData(0x41ed)] // 0755 readable root ancestor.
    public void AncestorsRequireSearchRatherThanRead(int mode)
    {
        var value = Metadata(mode: (ushort)mode, uid: 0, gid: 0);
        LinuxWorkspaceLayout.RequireAncestor(value);
        Assert.True((value with { LinkCount = 20, Length = 8192 }).SameAncestorAs(value));
    }

    [Theory]
    [InlineData(0x41c0)] // No other search.
    [InlineData(0x41ff)] // Other/group write.
    [InlineData(0x41dd)] // Group write.
    [InlineData(0x45ed)] // Setgid.
    [InlineData(0xa1ed)] // Symlink.
    public void AncestorsRejectUnsafeAccessAndNonDirectoryKinds(int mode) =>
        Reject(() => LinuxWorkspaceLayout.RequireAncestor(Metadata(mode: (ushort)mode, uid: 0, gid: 0)));

    [Fact]
    public void AncestorsCannotUseWorkerOwnershipOrEmptyIdentity()
    {
        var valid = Metadata(mode: 0x41ed, uid: 0, gid: 0);
        Reject(() => LinuxWorkspaceLayout.RequireAncestor(valid with { Uid = 65010 }));
        Reject(() => LinuxWorkspaceLayout.RequireAncestor(valid with { Gid = 65011 }));
        Reject(() => LinuxWorkspaceLayout.RequireAncestor(valid with { Inode = 0 }));
    }

    [Fact]
    public void DescriptorRequiresRootSingleLinkWorkerReadAndExactCountedLength()
    {
        var valid = Metadata(mode: 0x8120, uid: 0, gid: 65011) with { LinkCount = 1, Length = 1024 };
        LinuxWorkspaceLayout.RequireDescriptor(valid, 65011, 1024);
        foreach (var invalid in new[]
        {
            valid with { Uid = 65010 }, valid with { Gid = 65013 }, valid with { Inode = 0 },
            valid with { LinkCount = 2 }, valid with { Length = 1023 }, valid with { Mode = 0x81a0 },
            valid with { Mode = 0x8124 }, valid with { Mode = 0x4120 }, valid with { Mode = 0xa120 },
        }) Reject(() => LinuxWorkspaceLayout.RequireDescriptor(invalid, 65011, 1024));
        Reject(() => LinuxWorkspaceLayout.RequireDescriptor(valid, 0, 1024));
        Reject(() => LinuxWorkspaceLayout.RequireDescriptor(valid, uint.MaxValue, 1024));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65536)]
    public void DescriptorBoundariesCopyBytesWithoutParsingOrAliasing(int length)
    {
        var source = Enumerable.Repeat((byte)0xff, length).ToArray();
        var copy = LinuxWorkspaceLayout.CopyDescriptor(source);
        Array.Fill(source, (byte)0);
        Assert.Equal(length, copy.Length);
        Assert.All(copy, value => Assert.Equal((byte)0xff, value));
        LinuxWorkspaceLayout.RequireDescriptor(
            Metadata(mode: 0x8120, uid: 0, gid: 65011) with { LinkCount = 1, Length = (ulong)length },
            65011, length);
    }

    [Fact]
    public void EmptyOversizedAndNullDescriptorDataRejectBeforeNativeCreation()
    {
        Reject(() => LinuxWorkspaceLayout.CopyDescriptor([]));
        Reject(() => LinuxWorkspaceLayout.CopyDescriptor(new byte[65537]));
        Reject(() => LinuxWorkspaceLayout.CopyDescriptor(null!));
        var value = Metadata(mode: 0x8120, uid: 0, gid: 65011) with { LinkCount = 1, Length = 65537 };
        Reject(() => LinuxWorkspaceLayout.RequireDescriptor(value, 65011, 65537));
        Reject(() => LinuxWorkspaceLayout.RequireDescriptor(value with { Length = 0 }, 65011, 0));
    }

    private static LinuxProtectedMetadata Directory(LinuxWorkspaceDirectoryPolicy policy) =>
        Metadata((ushort)(0x4000 | policy.Permissions), policy.Uid, policy.Gid);

    private static LinuxProtectedMetadata Metadata(ushort mode, uint uid, uint gid) =>
        new(0, 27, 83, uid, gid, mode, 2, 4096, 1, 2, 3, 4);

    private static bool Overlaps(string left, string right) =>
        left == right || left.StartsWith(right + "/", StringComparison.Ordinal)
        || right.StartsWith(left + "/", StringComparison.Ordinal);

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/run", error.Message, StringComparison.Ordinal);
    }
}
