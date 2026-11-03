using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises application source inventory against actual root-owned Linux filesystem objects.</summary>
/// <remarks>These are native file controls only; they do not create an owner, workspace, or application acceptance.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxApplicationBundleAuditNativeTests
{
    private static readonly UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private static readonly UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly UnixFileMode ExecutableMode = DirectoryMode;

    [LinuxRootX64Fact]
    public void ActualRootTreeInventoryAcceptsSelectedBytesAndRetainsOnlyBorrowedNodes()
    {
        using var fixture = RootFixture.Create();
        var data = FixtureRows();
        fixture.WriteTree(data);
        var nodes = new List<LinuxProtectedNode>();
        var maps = NewMaps();
        try
        {
            var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
            Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default);
            Assert.Equal(data.Files.Length, maps.Files.Count);
            Assert.Equal(2, maps.Directories.Count);
            foreach (var node in nodes) node.Recheck();
            Assert.Equal(data.Rows["input.json"], File.ReadAllBytes(fixture.PathFor("input.json")));
        }
        finally { LinuxProtectedDeployment.Close(nodes); }
    }

    [LinuxRootX64Fact]
    public void ActualExtraFileIsRejectedAfterItsDescriptorJoinsTheCallersQuarantineSet()
    {
        using var fixture = RootFixture.Create();
        var data = FixtureRows();
        fixture.WriteTree(data);
        var sentinel = "outside sentinel bytes"u8.ToArray();
        File.WriteAllBytes(fixture.PathFor("unexpected.bin"), sentinel);
        File.SetUnixFileMode(fixture.PathFor("unexpected.bin"), FileMode);
        var nodes = new List<LinuxProtectedNode>();
        var maps = NewMaps();
        try
        {
            var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
            Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() =>
                Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default)).Code);
            Assert.True(nodes.Count > 4);
            foreach (var node in nodes) node.Recheck();
            Assert.Equal(sentinel, File.ReadAllBytes(fixture.PathFor("unexpected.bin")));
            foreach (var pair in data.Rows) Assert.Equal(pair.Value, File.ReadAllBytes(fixture.PathFor(pair.Key)));
        }
        finally { LinuxProtectedDeployment.Close(nodes); }
    }

    [LinuxRootX64Fact]
    public void ChangedDeclaredBytesAreRejectedAndTheActualTreeRemainsUnmodifiedByAudit()
    {
        using var fixture = RootFixture.Create();
        var data = FixtureRows();
        fixture.WriteTree(data);
        var changed = "mutate input.json"u8.ToArray();
        Assert.Equal(data.Rows["input.json"].Length, changed.Length);
        Assert.NotEqual(data.Rows["input.json"], changed);
        File.SetUnixFileMode(fixture.PathFor("input.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(fixture.PathFor("input.json"), changed);
        File.SetUnixFileMode(fixture.PathFor("input.json"), FileMode);
        var before = File.ReadAllBytes(fixture.PathFor("input.json"));
        var nodes = new List<LinuxProtectedNode>();
        var maps = NewMaps();
        try
        {
            var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
            Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() =>
                Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default)).Code);
            foreach (var node in nodes) node.Recheck();
            Assert.Equal(before, File.ReadAllBytes(fixture.PathFor("input.json")));
            Assert.Equal(data.Rows["apphost.dll"], File.ReadAllBytes(fixture.PathFor("apphost.dll")));
        }
        finally { LinuxProtectedDeployment.Close(nodes); }
    }

    [LinuxRootX64Fact]
    public void PreCancelledAuditStopsBeforeOpeningChildrenAndLeavesRootAndSourceBytesIntact()
    {
        using var fixture = RootFixture.Create();
        var data = FixtureRows();
        fixture.WriteTree(data);
        var nodes = new List<LinuxProtectedNode>();
        var maps = NewMaps();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
            var countBefore = nodes.Count;
            var error = Assert.Throws<OperationCanceledException>(() =>
                Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, cancelled.Token));
            Assert.Equal(cancelled.Token, error.CancellationToken);
            Assert.Equal(countBefore, nodes.Count);
            foreach (var node in nodes) node.Recheck();
            foreach (var pair in data.Rows) Assert.Equal(pair.Value, File.ReadAllBytes(fixture.PathFor(pair.Key)));
        }
        finally { LinuxProtectedDeployment.Close(nodes); }
    }

    [LinuxRootX64Fact]
    public void InvalidNameAndSpecialFilesystemChildrenAreRejectedWithoutChangingTheirSentinel()
    {
        foreach (var kind in new[] { "name", "symlink", "hardlink", "fifo" })
        {
            using var fixture = RootFixture.Create();
            var data = FixtureRows();
            fixture.WriteTree(data);
            var sentinelPath = fixture.PathFor("apphost.dll");
            var sentinel = File.ReadAllBytes(sentinelPath);
            switch (kind)
            {
                case "name": fixture.WriteExtra("invalid name", "extra"u8.ToArray()); break;
                case "symlink": fixture.CreateSymlink("extra-link", sentinelPath); break;
                case "hardlink": fixture.CreateHardlink(sentinelPath, fixture.PathFor("extra-hardlink")); break;
                case "fifo": fixture.CreateFifo("extra-fifo"); break;
            }

            var nodes = new List<LinuxProtectedNode>();
            var maps = NewMaps();
            try
            {
                var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
                if (kind == "symlink")
                {
                    // Mandatory openat2 no-symlink resolution rejects before metadata classification.
                    var error = Assert.Throws<IOException>(() =>
                        Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default));
                    Assert.Equal(40, Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException).NativeErrorCode);
                }
                else
                    Assert.Throws<EvidenceAdmissionException>(() =>
                        Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default));
                foreach (var node in nodes) node.Recheck();
                Assert.Equal(sentinel, File.ReadAllBytes(sentinelPath));
            }
            finally { LinuxProtectedDeployment.Close(nodes); }
        }
    }

    [LinuxRootX64Fact]
    public void ActualThirtySecondDirectoryLevelIsAcceptedAndThirtyThirdIsRejectedAfterRetention()
    {
        var data = FixtureRowsWithNestedInput(32);
        using (var fixture = RootFixture.Create())
        {
            fixture.WriteTree(data);
            var nodes = new List<LinuxProtectedNode>();
            try
            {
                var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
                var maps = NewMaps();
                Audit(data.Files, data.ByPath, nodes, maps).Inventory(root, default);
                Assert.Equal(data.Files.Length, maps.Files.Count);
                foreach (var node in nodes) node.Recheck();
            }
            finally { LinuxProtectedDeployment.Close(nodes); }
        }
        data = FixtureRowsWithNestedInput(33);
        using (var fixture = RootFixture.Create())
        {
            fixture.WriteTree(data);
            var nodes = new List<LinuxProtectedNode>();
            var sampledNodes = new List<LinuxProtectedNode>();
            try
            {
                var deepestPath = fixture.PathFor(string.Join('/', Enumerable.Repeat("n", 33)));
                var deepest = LinuxProtectedNode.OpenAbsolute(deepestPath, true, true, 0, sampledNodes, default);
                var root = LinuxProtectedNode.OpenAbsolute(fixture.Root, true, true, 0, nodes, default);
                Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() =>
                    Audit(data.Files, data.ByPath, nodes, NewMaps()).Inventory(root, default)).Code);
                Assert.Contains(nodes, node => node.Metadata.SameAncestorAs(deepest.Metadata));
                foreach (var node in nodes) node.Recheck();
                deepest.Recheck();
            }
            finally
            {
                LinuxProtectedDeployment.Close(nodes);
                LinuxProtectedDeployment.Close(sampledNodes);
            }
        }
    }

    private static LinuxApplicationBundleAudit Audit(LinuxApplicationBundleFile[] files,
        IReadOnlyDictionary<string, LinuxApplicationBundleFile> selected, List<LinuxProtectedNode> nodes,
        (Dictionary<string, LinuxProtectedNode> Directories, Dictionary<string, LinuxProtectedNode> Files,
            Dictionary<string, LinuxProtectedNode> Handles) maps) => new(files, selected, nodes,
            new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1)),
            maps.Directories, maps.Files, maps.Handles);

    private static (Dictionary<string, LinuxProtectedNode> Directories, Dictionary<string, LinuxProtectedNode> Files,
        Dictionary<string, LinuxProtectedNode> Handles) NewMaps() =>
        (new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    private static (LinuxApplicationBundleFile[] Files, Dictionary<string, LinuxApplicationBundleFile> ByPath,
        Dictionary<string, byte[]> Rows) FixtureRowsWithNestedInput(int depth)
    {
        var normal = FixtureRows();
        var nestedPath = string.Join('/', Enumerable.Repeat("n", depth)) + "/input.json";
        var files = normal.Files.Select(file => file.RelativePath == "input.json"
            ? file with { RelativePath = nestedPath } : file).ToArray();
        var rows = normal.Rows.Where(static pair => pair.Key != "input.json")
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        rows[nestedPath] = normal.Rows["input.json"];
        files = files.Select(file => file.RelativePath == nestedPath
            ? file with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(rows[nestedPath])) } : file).ToArray();
        return (files, files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal), rows);
    }

    private static (LinuxApplicationBundleFile[] Files, Dictionary<string, LinuxApplicationBundleFile> ByPath,
        Dictionary<string, byte[]> Rows) FixtureRows()
    {
        var items = new (string Path, LinuxApplicationBundleRole Role, uint Mode)[]
        {
            ("apphost.dll", LinuxApplicationBundleRole.AppHost, 0x124),
            ("apphost.runtimeconfig.json", LinuxApplicationBundleRole.AppHostRuntimeConfiguration, 0x124),
            ("resource.dll", LinuxApplicationBundleRole.Resource, 0x124),
            ("resource.runtimeconfig.json", LinuxApplicationBundleRole.ResourceRuntimeConfiguration, 0x124),
            ("dcp/dcp", LinuxApplicationBundleRole.Dcp, 0x16d),
            ("input.json", LinuxApplicationBundleRole.DeclaredInput, 0x124),
        };
        var rows = items.ToDictionary(static item => item.Path,
            static item => System.Text.Encoding.UTF8.GetBytes("actual " + item.Path), StringComparer.Ordinal);
        var files = items.Select(item => new LinuxApplicationBundleFile(item.Path, item.Role, rows[item.Path].Length,
            Convert.ToHexStringLower(SHA256.HashData(rows[item.Path])), item.Mode)).ToArray();
        return (files, files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal), rows);
    }

    private sealed class LinuxRootX64FactAttribute : FactAttribute
    {
        public LinuxRootX64FactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
                Skip = "Requires native Linux x64 with effective UID 0; a skipped case is not native acceptance evidence.";
        }
    }

    private sealed class RootFixture : IDisposable
    {
        private RootFixture(string root)
        {
            Root = root;
        }

        internal string Root { get; }
        internal string PathFor(string relative)
        {
            return TestPathUtils.PathUnder(Root, relative);
        }
        internal static RootFixture Create()
        {
            var root = TestPathUtils.PathUnder("/run", "issue779-app-bundle-audit-" + Guid.NewGuid().ToString("N"));
            if (!root.StartsWith("/run/issue779-app-bundle-audit-", StringComparison.Ordinal)
                || root.Contains("/../", StringComparison.Ordinal))
                throw new InvalidOperationException("The native audit fixture path escaped its fixed /run namespace.");
            if (MakeDirectory(root, 0x1c0) != 0)
                throw new IOException($"Exclusive native fixture mkdir failed, errno {Marshal.GetLastPInvokeError()}.");
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(root);
        }
        internal void WriteExtra(string name, byte[] bytes)
        {
            var path = PathFor(name);
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, FileMode);
        }
        internal void CreateSymlink(string name, string target) => File.CreateSymbolicLink(PathFor(name), target);
        internal void CreateHardlink(string existing, string destination)
        {
            if (Link(existing, destination) != 0)
                throw new IOException($"Creating test hardlink failed, errno {Marshal.GetLastPInvokeError()}.");
        }
        internal void CreateFifo(string name)
        {
            if (MakeFifo(PathFor(name), 0x180) != 0)
                throw new IOException($"Creating test FIFO failed, errno {Marshal.GetLastPInvokeError()}.");
        }
        internal void WriteTree((LinuxApplicationBundleFile[] Files, Dictionary<string, LinuxApplicationBundleFile> ByPath,
            Dictionary<string, byte[]> Rows) data)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in data.Files)
            {
                var parts = file.RelativePath.Split('/');
                var prefix = string.Empty;
                for (var index = 0; index < parts.Length - 1; index++)
                {
                    prefix = prefix.Length == 0 ? parts[index] : prefix + "/" + parts[index];
                    directories.Add(prefix);
                    Directory.CreateDirectory(PathFor(prefix));
                }
                var path = PathFor(file.RelativePath);
                File.WriteAllBytes(path, data.Rows[file.RelativePath]);
                File.SetUnixFileMode(path, (UnixFileMode)file.Mode);
            }
            foreach (var directory in directories) File.SetUnixFileMode(PathFor(directory), DirectoryMode);
            File.SetUnixFileMode(Root, DirectoryMode);
        }
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var directory in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(static p => p.Length))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
