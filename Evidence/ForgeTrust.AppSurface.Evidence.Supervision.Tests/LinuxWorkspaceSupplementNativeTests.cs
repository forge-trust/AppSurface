using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual-root file-tree checks used by the application and run workspace inventory code.</summary>
/// <remarks>
/// These tests use only real root-owned fixture nodes and retained descriptors. They exercise selected bundle
/// inventory and workspace metadata predicates; they do not construct an owner, run account holder, workspace,
/// application launch, worker settlement or accepted evidence receipt.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxWorkspaceSupplementNativeTests
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private const UnixFileMode ReadOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ReadOnlyExecutable = ReadOnlyDirectory;
    [RootWorkspaceFact]
    public void ApplicationWorkspaceInventoryAcceptsTheActualRetainedReadOnlyBundleTree()
    {
        using var fixture = BundleFixture.Create();
        var rows = fixture.SelectedRows();
        using var retained = fixture.OpenInventory();

        LinuxApplicationWorkspaceLayout.RequireExactInventory(rows, retained.Observed);
        Assert.Equal(rows.Length + LinuxApplicationWorkspaceLayout.FindDirectories(rows).Count + 1,
            retained.Observed.Length);
        Assert.Contains(retained.Nodes, node => node.Metadata.Uid == 0 && node.Metadata.Gid == 0);
        Assert.Contains(retained.Observed, row => row.RelativePath == "dcp/dcp" && !row.IsDirectory);
        Assert.Contains(retained.Observed, row => row.RelativePath == "dcp" && row.IsDirectory);
        foreach (var node in retained.Nodes) node.Recheck();
    }

    [RootWorkspaceTheory]
    [InlineData("same-length-bytes")]
    [InlineData("extra-leaf")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    public void ApplicationWorkspaceInventoryRejectsChangedActualBundleNodes(string mutation)
    {
        using var fixture = BundleFixture.Create();
        var rows = fixture.SelectedRows();
        switch (mutation)
        {
            case "same-length-bytes": fixture.ReplaceBytes("input.json"); break;
            case "extra-leaf": fixture.WriteExtra("unexpected.bin", "unselected"u8.ToArray()); break;
            case "hardlink": fixture.Hardlink("input.json", "input-alias.json"); break;
            case "symlink": fixture.Symlink("unselected-link", fixture.PathFor("input.json")); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        if (mutation == "hardlink")
        {
            Assert.Throws<EvidenceAdmissionException>(() => fixture.OpenInventory());
            return;
        }

        if (mutation == "symlink")
        {
            Assert.ThrowsAny<IOException>(() => fixture.OpenInventory());
            return;
        }

        using var observed = fixture.OpenInventory();
        Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxApplicationWorkspaceLayout.RequireExactInventory(rows, observed.Observed));
        Assert.True(File.Exists(fixture.PathFor("input.json")));
    }

    [RootWorkspaceFact]
    public void RetainedBundleDescriptorDetectsSamePathReplacementAfterInventory()
    {
        using var fixture = BundleFixture.Create();
        using var retained = fixture.OpenInventory();
        var selected = retained.Nodes.Single(node => (node.Metadata.Mode & 0xf000) == 0x8000
            && node.Metadata.Length == (ulong)fixture.BytesFor("input.json").Length
            && node.Hash(CancellationToken.None) == Convert.ToHexStringLower(SHA256.HashData(fixture.BytesFor("input.json"))));
        var originalPath = fixture.PathFor("input.json");
        var movedPath = fixture.PathFor("input.original");
        File.Move(originalPath, movedPath);
        fixture.WriteExtra("input.json", fixture.BytesFor("input.json"));

        Assert.Throws<EvidenceAdmissionException>(() => selected.Recheck());
        Assert.Equal(fixture.BytesFor("input.json"), File.ReadAllBytes(movedPath));
        Assert.Equal(fixture.BytesFor("input.json"), File.ReadAllBytes(originalPath));
    }

    [RootWorkspaceFact]
    public void ProducerAndRunAncestorPoliciesAcceptActualRootOwnedRunAncestors()
    {
        using var retained = Open("/run", directory: true, sharedRead: true);
        LinuxProducerWorkspaceLayout.RequireAncestor(retained.Node.Metadata);
        LinuxWorkspaceLayout.RequireAncestor(retained.Node.Metadata);
        retained.Node.Recheck();

        var daemon = GetUser("daemon");
        var nobody = GetUser("nobody");
        var nogroup = GetGroup("nogroup");
        var adm = GetGroup("adm");
        Assert.NotEqual(daemon.Uid, nobody.Uid);
        Assert.NotEqual(daemon.Gid, nobody.Gid);
        Assert.NotEqual(nogroup.Gid, adm.Gid);
        var rootSnapshot = new LinuxRunAccountSnapshot(daemon.Uid, daemon.Gid,
            nobody.Uid, nobody.Gid, adm.Gid);
        // These are existing NSS identities used only to derive closed path data; this test creates no accounts.
        var runLayout = LinuxWorkspaceLayout.Create(Guid.NewGuid(), rootSnapshot);
        Assert.Equal("/run/" + runLayout.Name, runLayout.Root);
        Assert.Equal(runLayout.Root + "/worker/broker/control.sock", runLayout.ControlSocket);

        using var fixture = BundleFixture.Create();
        var nativeGeneration = fixture.Root + "/workspace-generation";
        Directory.CreateDirectory(nativeGeneration, PrivateDirectory);
        foreach (var policy in runLayout.Directories)
        {
            var path = policy.Name.Length == 0 ? nativeGeneration : policy.Name switch
            {
                "worker" => nativeGeneration + "/worker",
                "broker" => nativeGeneration + "/worker/broker",
                "output" => nativeGeneration + "/output",
                "raw-results" => nativeGeneration + "/raw-results",
                _ => throw new ArgumentOutOfRangeException(nameof(policy)),
            };
            Directory.CreateDirectory(path, PrivateDirectory);
            Assert.Equal(0, Chown(path, policy.Uid, policy.Gid));
            File.SetUnixFileMode(path, (UnixFileMode)policy.Permissions);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var actual = LinuxProtectedMetadata.From(EvidenceLinuxFileSystem.StatFd(handle));
            LinuxWorkspaceLayout.RequireDirectory(actual, policy);
            if (policy.Name == "output")
                LinuxProducerWorkspaceLayout.RequireDirectory(actual, daemon.Uid, daemon.Gid, 0x1c0);
        }

        var producerLayout = LinuxProducerWorkspaceLayout.Create(Guid.NewGuid(), rootSnapshot,
            fixture.Root, fixture.Root + "/subject/Subject.csproj");
        Assert.Equal(producerLayout.SubjectRoot + "/subject/Subject.csproj", producerLayout.SolutionPath);
        var applicationLayout = LinuxApplicationWorkspaceLayout.Create(Guid.NewGuid(), fixture.Root,
            new string('a', 64), nobody.Uid, nobody.Gid,
            new LinuxApplicationLimits(16 * 1024 * 1024, 64 * 1024 * 1024, 4, 64 * 1024, 30, 5),
            fixture.SelectedRows(), ["input.json"]);
        Assert.Equal(applicationLayout.BundleRoot + "/dcp/dcp", applicationLayout.DcpPath);
    }

    [RootWorkspaceIdentityFact]
    public void WorkspaceDirectoryAndDescriptorPredicatesUseActualRootFilesystemMetadata()
    {
        using var fixture = BundleFixture.Create();
        var privatePolicyDirectory = fixture.Root + "/policy";
        Directory.CreateDirectory(privatePolicyDirectory, PrivateDirectory);
        File.SetUnixFileMode(privatePolicyDirectory, PrivateDirectory);
        using var directory = Open(privatePolicyDirectory, directory: true, sharedRead: false);
        var policy = new LinuxWorkspaceDirectoryPolicy("fixture", null, 0, 0, 0x1c0);
        LinuxWorkspaceLayout.RequireDirectory(directory.Node.Metadata, policy);
        LinuxProducerWorkspaceLayout.RequireDirectory(directory.Node.Metadata, 0, 0, 0x1c0);

        var descriptor = fixture.PathFor("worker-control.json");
        File.WriteAllBytes(descriptor, "bounded descriptor bytes"u8.ToArray());
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var group = GetGroup("nogroup").Gid;
        Assert.Equal(0, Chown(descriptor, 0, group));
        using var descriptorHandle = File.OpenHandle(descriptor, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var metadata = LinuxProtectedMetadata.From(EvidenceLinuxFileSystem.StatFd(descriptorHandle));
        LinuxWorkspaceLayout.RequireDescriptor(metadata, group, checked((int)metadata.Length));

        File.SetUnixFileMode(descriptor, ReadOnlyFile | UnixFileMode.GroupWrite);
        var changed = LinuxProtectedMetadata.From(EvidenceLinuxFileSystem.StatFd(descriptorHandle));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxWorkspaceLayout.RequireDescriptor(
            changed, group, checked((int)changed.Length)));
        Assert.Equal("bounded descriptor bytes"u8.ToArray(), File.ReadAllBytes(descriptor));
    }

    private static NodeSnapshot Open(string path,
        bool directory, bool sharedRead)
    {
        var nodes = new List<LinuxProtectedNode>();
        try
        {
            var node = LinuxProtectedNode.OpenAbsolute(path, directory, sharedRead,
                directory ? 0 : 1024 * 1024, nodes, CancellationToken.None);
            return new NodeSnapshot(nodes, node);
        }
        catch
        {
            LinuxProtectedDeployment.Close(nodes);
            throw;
        }
    }

    private sealed class BundleFixture : IDisposable
    {
        private static readonly (string Path, LinuxApplicationBundleRole Role, UnixFileMode Mode)[] Selected =
        [
            ("apphost.dll", LinuxApplicationBundleRole.AppHost, ReadOnlyFile),
            ("apphost.runtimeconfig.json", LinuxApplicationBundleRole.AppHostRuntimeConfiguration, ReadOnlyFile),
            ("resource.dll", LinuxApplicationBundleRole.Resource, ReadOnlyFile),
            ("resource.runtimeconfig.json", LinuxApplicationBundleRole.ResourceRuntimeConfiguration, ReadOnlyFile),
            ("dcp/dcp", LinuxApplicationBundleRole.Dcp, ReadOnlyExecutable),
            ("input.json", LinuxApplicationBundleRole.DeclaredInput, ReadOnlyFile),
        ];

        private BundleFixture(string root)
        {
            Root = root;
        }

        internal string Root { get; }
        private string Bundle => TestPathUtils.PathUnder(Root, "bundle");
        internal string PathFor(string relative) => TestPathUtils.PathUnder(Bundle, relative);

        internal static BundleFixture Create()
        {
            var root = "/run/issue779-workspace-supplement-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root, PrivateDirectory | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(root, PrivateDirectory | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var fixture = new BundleFixture(root);
            Directory.CreateDirectory(fixture.Bundle, PrivateDirectory);
            foreach (var selected in Selected)
            {
                var path = fixture.PathFor(selected.Path);
                var parent = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(parent, PrivateDirectory);
                File.WriteAllBytes(path, fixture.BytesFor(selected.Path));
                File.SetUnixFileMode(path, selected.Mode);
            }
            foreach (var directory in Directory.GetDirectories(fixture.Bundle, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(directory, ReadOnlyDirectory);
            File.SetUnixFileMode(fixture.Bundle, ReadOnlyDirectory);
            return fixture;
        }

        internal LinuxApplicationBundleFile[] SelectedRows() => Selected.Select(item =>
        {
            var bytes = BytesFor(item.Path);
            return new LinuxApplicationBundleFile(item.Path, item.Role, bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), (uint)item.Mode);
        }).ToArray();

        internal byte[] BytesFor(string relative) => System.Text.Encoding.UTF8.GetBytes("fixture:" + relative);

        internal void ReplaceBytes(string relative)
        {
            var path = PathFor(relative);
            var bytes = BytesFor(relative);
            bytes[0] ^= 0x01;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, ReadOnlyFile);
        }

        internal void WriteExtra(string relative, byte[] bytes)
        {
            var path = PathFor(relative);
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, ReadOnlyFile);
        }

        internal void Hardlink(string relative, string alias)
        {
            if (Link(PathFor(relative), PathFor(alias)) != 0)
                throw new IOException("Creating the native inventory hardlink failed.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }

        internal void Symlink(string relative, string target) => File.CreateSymbolicLink(PathFor(relative), target);

        internal InventorySnapshot OpenInventory()
        {
            var nodes = new List<LinuxProtectedNode>();
            try
            {
                var root = LinuxProtectedNode.OpenAbsolute(Bundle, directory: true, sharedRead: true,
                    maximumBytes: 0, nodes, CancellationToken.None);
                var entries = new List<LinuxApplicationObservedEntry>();
                Observe(root, string.Empty, entries, nodes);
                return new InventorySnapshot(nodes, entries.ToArray());
            }
            catch
            {
                LinuxProtectedDeployment.Close(nodes);
                throw;
            }
        }

        private static void Observe(LinuxProtectedNode node, string relative,
            List<LinuxApplicationObservedEntry> entries, List<LinuxProtectedNode> nodes)
        {
            var metadata = node.Metadata;
            if ((metadata.Mode & 0xf000) == 0x4000)
            {
                entries.Add(new(relative, true, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                    metadata.Inode, checked((long)metadata.Length), null));
                foreach (var name in node.ReadNames(CancellationToken.None))
                {
                    var isDirectory = node.IsChildDirectory(name);
                    var child = node.OpenChild(name, isDirectory, CancellationToken.None);
                    nodes.Add(child);
                    var childPath = relative.Length == 0 ? name : relative + "/" + name;
                    Observe(child, childPath, entries, nodes);
                }
                return;
            }

            entries.Add(new(relative, false, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), node.Hash(CancellationToken.None)));
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var directory in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                .OrderByDescending(static path => path.Length))
                File.SetUnixFileMode(directory, PrivateDirectory);
            File.SetUnixFileMode(Bundle, PrivateDirectory);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class InventorySnapshot(List<LinuxProtectedNode> nodes,
        LinuxApplicationObservedEntry[] observed) : IDisposable
    {
        internal List<LinuxProtectedNode> Nodes { get; } = nodes;
        internal LinuxApplicationObservedEntry[] Observed { get; } = observed;
        public void Dispose() => LinuxProtectedDeployment.Close(Nodes);
    }

    private sealed class NodeSnapshot(List<LinuxProtectedNode> nodes, LinuxProtectedNode node) : IDisposable
    {
        internal LinuxProtectedNode Node { get; } = node;
        public void Dispose() => LinuxProtectedDeployment.Close(nodes);
    }

    private sealed class RootWorkspaceFactAttribute : FactAttribute
    {
        public RootWorkspaceFactAttribute()
        {
            if (!Available()) Skip = "Requires actual Linux x64 root filesystem descriptor operations.";
        }
    }

    private sealed class RootWorkspaceTheoryAttribute : TheoryAttribute
    {
        public RootWorkspaceTheoryAttribute()
        {
            if (!Available()) Skip = "Requires actual Linux x64 root filesystem descriptor operations.";
        }
    }

    private sealed class RootWorkspaceIdentityFactAttribute : FactAttribute
    {
        public RootWorkspaceIdentityFactAttribute()
        {
            if (!Available()) Skip = "Requires actual Linux x64 root filesystem descriptor operations.";
            else if (!TryIdentityData()) Skip = "Requires existing daemon, nobody, nogroup and adm NSS identities.";
        }
    }

    private static bool Available() => OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64 && GetEffectiveUid() == 0;

    private static bool TryIdentityData()
    {
        return GetPasswdByName("daemon") != IntPtr.Zero && GetPasswdByName("nobody") != IntPtr.Zero
            && GetGroupByName("nogroup") != IntPtr.Zero && GetGroupByName("adm") != IntPtr.Zero;
    }

    private static LinuxPasswd GetUser(string name) =>
        Marshal.PtrToStructure<LinuxPasswd>(GetPasswdByName(name));

    private static LinuxGroup GetGroup(string name) =>
        Marshal.PtrToStructure<LinuxGroup>(GetGroupByName(name));

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxPasswd
    {
        internal IntPtr Name;
        internal IntPtr Password;
        internal uint Uid;
        internal uint Gid;
        internal IntPtr Gecos;
        internal IntPtr HomeDirectory;
        internal IntPtr Shell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxGroup
    {
        internal IntPtr Name;
        internal IntPtr Password;
        internal uint Gid;
        internal IntPtr Members;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    [DllImport("libc", EntryPoint = "getgrnam", SetLastError = true)]
    private static extern IntPtr GetGroupByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libc", EntryPoint = "getpwnam", SetLastError = true)]
    private static extern IntPtr GetPasswdByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
