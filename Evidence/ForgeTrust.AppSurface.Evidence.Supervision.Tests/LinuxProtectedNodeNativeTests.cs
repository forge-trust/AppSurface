using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

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
