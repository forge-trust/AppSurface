using ForgeTrust.AppSurface.Evidence.Contracts;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Projection, inventory and ordinary owned-FD controls; none creates a protected workspace or account authority.</summary>
public sealed class LinuxApplicationWorkspaceTests
{
    private static readonly Guid RunId = Guid.Parse("7f05e071-9a9b-4f40-a211-c9b3064f22d8");
    private const string EntryDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly LinuxApplicationLimits Limits = new(64 * 1024 * 1024, 256 * 1024 * 1024,
        8, 256 * 1024, 30, 5);

    [Fact]
    public void LayoutCopiesSelectedRowsAndProjectsBundleOutsideTheDeniedToolRoot()
    {
        var source = Files(includeExtension: true);
        var layout = Create(source);

        Assert.Equal(RunId, layout.RunId);
        Assert.Equal(EntryDigest, layout.EntryDigest);
        Assert.Equal(61001u, layout.ApplicationUid);
        Assert.Equal(61002u, layout.ApplicationGid);
        Assert.Equal("/opt/forge/tools", layout.DeniedToolRoot);
        Assert.Equal("/run/appsurface-app-7f05e0719a9b4f40a211c9b3064f22d8", layout.Root);
        Assert.Equal(layout.Root + "/control", layout.ControlRoot);
        Assert.Equal(layout.Root + "/bundle", layout.BundleRoot);
        Assert.Equal(layout.Root + "/scratch", layout.ScratchRoot);
        Assert.Equal(layout.Root + "/denied-output", layout.ProtectedOutputRoot);
        Assert.Equal(layout.BundleRoot + "/apphost.dll", layout.AppHostPath);
        Assert.Equal(layout.BundleRoot + "/dcp/dcp", layout.DcpPath);
        Assert.Equal(layout.BundleRoot + "/input.json", layout.InputPath);
        Assert.Equal(new[] { "dcp", "dcp/ext" }, layout.DirectoryPaths);
        Assert.Equal(64L * 1024 * 1024, layout.Limits.ScratchBytes);
        Assert.Equal(256L * 1024 * 1024, layout.Limits.MemoryBytes);
        Assert.Equal(8, layout.Limits.MaximumTasks);
        Assert.Equal(256L * 1024, layout.Limits.MaximumOutputBytes);
        Assert.False(layout.DeniedToolRoot.StartsWith(layout.Root + "/", StringComparison.Ordinal));
        Assert.False(layout.Root.StartsWith(layout.DeniedToolRoot + "/", StringComparison.Ordinal));

        source[0] = source[0] with { RelativePath = "mutated.dll" };
        Assert.Equal("apphost.dll", layout.BundleFiles[0].RelativePath);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LinuxApplicationBundleFile>)layout.BundleFiles)[0] = layout.BundleFiles[1]);
    }

    [Fact]
    public void ExactInventoryAcceptsTheCompleteRootOwnedReadOnlyProjection()
    {
        var files = Files(includeExtension: true);
        LinuxApplicationWorkspaceLayout.RequireExactInventory(files, Observed(files));
    }

    [Fact]
    public void ExactInventoryRejectsOmissionExtrasDuplicatesAndMetadataDrift()
    {
        var files = Files(includeExtension: false);
        var valid = Observed(files);
        LinuxApplicationWorkspaceLayout.RequireExactInventory(files, valid);

        Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files, valid.Skip(1).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files,
            valid.Append(valid[^1] with { RelativePath = "unexpected.bin" }).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files,
            valid.Append(valid[^1]).ToArray()));

        var fileIndex = Array.FindIndex(valid, static row => row.RelativePath == "apphost.dll");
        foreach (var changed in new[]
        {
            valid[fileIndex] with { Uid = 1 },
            valid[fileIndex] with { Gid = 1 },
            valid[fileIndex] with { Mode = (ushort)(valid[fileIndex].Mode | 0x2) },
            valid[fileIndex] with { LinkCount = 2 },
            valid[fileIndex] with { LengthBytes = valid[fileIndex].LengthBytes + 1 },
            valid[fileIndex] with { Sha256 = new string('f', 64) },
            valid[fileIndex] with { IsDirectory = true },
            valid[fileIndex] with { Inode = 0 },
        })
        {
            var drifted = (LinuxApplicationObservedEntry[])valid.Clone();
            drifted[fileIndex] = changed;
            Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files, drifted));
        }

        var directoryIndex = Array.FindIndex(valid, static row => row.RelativePath == "dcp");
        var writableDirectory = (LinuxApplicationObservedEntry[])valid.Clone();
        writableDirectory[directoryIndex] = valid[directoryIndex] with { Mode = 0x41ed };
        Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files, writableDirectory));
    }

    [Fact]
    public void LayoutRejectsInvalidIdentityLimitsDigestInputAndDeniedRootOverlap()
    {
        var files = Files(includeExtension: false);
        Reject(() => LinuxApplicationWorkspaceLayout.Create(Guid.Empty, "/opt/forge/tools", EntryDigest,
            61001, 61002, Limits, files, ["input.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", "not-a-digest",
            61001, 61002, Limits, files, ["input.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest,
            0, 61002, Limits, files, ["input.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest,
            61001, uint.MaxValue, Limits, files, ["input.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest,
            61001, 61002, Limits with { ScratchBytes = 0 }, files, ["input.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest,
            61001, 61002, Limits, files, ["other.json"]));
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId,
            "/run/appsurface-app-7f05e0719a9b4f40a211c9b3064f22d8", EntryDigest,
            61001, 61002, Limits, files, ["input.json"]));
    }

    [Fact]
    public void BundleRowsRejectUnsafePathsModesRoleCardinalityAndFileDirectoryAliasing()
    {
        var files = Files(includeExtension: false);
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files.Select(file =>
            file.RelativePath == "input.json" ? file with { RelativePath = "../input.json" } : file).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files.Select(file =>
            file.RelativePath == "input.json" ? file with { RelativePath = "APPHOST.DLL" } : file).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files.Select(file =>
            file.Role == LinuxApplicationBundleRole.Dcp ? file with { Mode = 0x124 } : file).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files.Select(file =>
            file.Role == LinuxApplicationBundleRole.AppHost ? file with { Role = LinuxApplicationBundleRole.Resource } : file).ToArray()));
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files.Append(
            Row("dcp", LinuxApplicationBundleRole.Dependency, 0x124, 'f')).ToArray()));

        var nestedAppHost = files.Select(file => file.Role == LinuxApplicationBundleRole.AppHost
            ? file with { RelativePath = "nested/apphost.dll" } : file).ToArray();
        Reject(() => LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest,
            61001, 61002, Limits, nestedAppHost, ["input.json"]));
    }

    [Theory]
    [InlineData("limits-null")]
    [InlineData("files-null")]
    [InlineData("inputs-null")]
    [InlineData("uid-max")]
    [InlineData("gid-zero")]
    [InlineData("digest-null")]
    [InlineData("digest-uppercase")]
    [InlineData("scratch-over")]
    [InlineData("memory-zero")]
    [InlineData("memory-over")]
    [InlineData("tasks-zero")]
    [InlineData("tasks-over")]
    [InlineData("output-zero")]
    [InlineData("output-over")]
    [InlineData("start-zero")]
    [InlineData("start-over")]
    [InlineData("stop-zero")]
    [InlineData("stop-over")]
    [InlineData("inputs-empty")]
    [InlineData("inputs-extra")]
    [InlineData("tool-ancestor")]
    [InlineData("tool-descendant")]
    public void LayoutRejectsOneIncompleteOrOutOfBoundsProjection(string mutation)
    {
        var limits = Limits;
        var files = Files(includeExtension: false);
        string[] inputs = ["input.json"];
        var uid = 61001u;
        var gid = 61002u;
        var digest = EntryDigest;
        var tool = "/opt/forge/tools";
        LinuxApplicationWorkspaceLayout Project() => LinuxApplicationWorkspaceLayout.Create(
            RunId, tool, digest, uid, gid, limits, files, inputs);
        Assert.Equal(EntryDigest, Project().EntryDigest);
        switch (mutation)
        {
            case "limits-null": limits = null!; break;
            case "files-null": files = null!; break;
            case "inputs-null": inputs = null!; break;
            case "uid-max": uid = uint.MaxValue; break;
            case "gid-zero": gid = 0; break;
            case "digest-null": digest = null!; break;
            case "digest-uppercase": digest = EntryDigest.ToUpperInvariant(); break;
            case "scratch-over": limits = limits with { ScratchBytes = 1024L * 1024 * 1024 + 1 }; break;
            case "memory-zero": limits = limits with { MemoryBytes = 0 }; break;
            case "memory-over": limits = limits with { MemoryBytes = 1024L * 1024 * 1024 + 1 }; break;
            case "tasks-zero": limits = limits with { MaximumTasks = 0 }; break;
            case "tasks-over": limits = limits with { MaximumTasks = 129 }; break;
            case "output-zero": limits = limits with { MaximumOutputBytes = 0 }; break;
            case "output-over": limits = limits with { MaximumOutputBytes = 1024 * 1024 + 1 }; break;
            case "start-zero": limits = limits with { StartSeconds = 0 }; break;
            case "start-over": limits = limits with { StartSeconds = 121 }; break;
            case "stop-zero": limits = limits with { StoppingSeconds = 0 }; break;
            case "stop-over": limits = limits with { StoppingSeconds = 31 }; break;
            case "inputs-empty": inputs = []; break;
            case "inputs-extra": inputs = ["input.json", "other.json"]; break;
            case "tool-ancestor": tool = "/run"; break;
            case "tool-descendant": tool = "/run/appsurface-app-" + RunId.ToString("N") + "/bundle"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Reject(() => Project());
    }

    [Fact]
    public void LayoutAcceptsBothExactEndsOfEveryUnitLimitAndDisjointSiblingPaths()
    {
        foreach (var limits in new[]
        {
            new LinuxApplicationLimits(1, 1, 1, 1, 1, 1),
            new LinuxApplicationLimits(1024L * 1024 * 1024, 1024L * 1024 * 1024, 128, 1024 * 1024, 120, 30),
        })
        {
            var layout = LinuxApplicationWorkspaceLayout.Create(RunId,
                "/run/appsurface-app-" + RunId.ToString("N") + "-sibling", EntryDigest,
                61001, 61002, limits, Files(includeExtension: false), ["input.json"]);
            Assert.Equal(limits, layout.Limits);
            Assert.NotSame(limits, layout.Limits);
        }
    }

    [Theory]
    [InlineData("files-null")]
    [InlineData("files-five")]
    [InlineData("files-257")]
    [InlineData("later-null")]
    [InlineData("path-null")]
    [InlineData("path-empty")]
    [InlineData("path-4096")]
    [InlineData("path-nonascii")]
    [InlineData("path-empty-segment")]
    [InlineData("path-component-256")]
    [InlineData("path-dot")]
    [InlineData("path-depth-33")]
    [InlineData("role-unknown")]
    [InlineData("length-zero")]
    [InlineData("length-over")]
    [InlineData("aggregate-over")]
    [InlineData("digest-null")]
    [InlineData("digest-length")]
    [InlineData("digest-nonhex")]
    [InlineData("dcp-path")]
    [InlineData("extension-prefix")]
    [InlineData("extension-mode")]
    [InlineData("apphost-suffix")]
    [InlineData("resource-suffix")]
    [InlineData("apphost-config-suffix")]
    [InlineData("resource-config-suffix")]
    [InlineData("manifest-suffix")]
    [InlineData("missing-apphost")]
    [InlineData("missing-apphost-config")]
    [InlineData("missing-resource")]
    [InlineData("missing-resource-config")]
    [InlineData("missing-dcp")]
    [InlineData("missing-input")]
    [InlineData("duplicate-apphost")]
    [InlineData("duplicate-input")]
    public void BundleRejectsOneMalformedRowOrIncompleteSelectedRoleSet(string mutation)
    {
        var files = Files(includeExtension: true);
        LinuxApplicationWorkspaceLayout.ValidateFiles(files);
        var input = Array.FindIndex(files, static file => file.Role == LinuxApplicationBundleRole.DeclaredInput);
        void ChangeRole(LinuxApplicationBundleRole role, string suffix) => files = files.Select(file => file.Role == role
            ? file with { RelativePath = "bundle-canary" + suffix } : file).ToArray();
        void RemoveRole(LinuxApplicationBundleRole role) => files = files.Select(file => file.Role == role
            ? file with { Role = LinuxApplicationBundleRole.Dependency } : file).ToArray();
        switch (mutation)
        {
            case "files-null": files = null!; break;
            case "files-five": files = files.Take(5).ToArray(); break;
            case "files-257": files = files.Concat(Enumerable.Range(0, 250)
                .Select(i => Row($"extra-{i}.dat", LinuxApplicationBundleRole.Dependency, 0x124, 'a'))).ToArray(); break;
            case "later-null": files = files.Append(null!).ToArray(); break;
            case "path-null": files[input] = files[input] with { RelativePath = null! }; break;
            case "path-empty": files[input] = files[input] with { RelativePath = "" }; break;
            case "path-4096":
                var prefix = string.Join('/', Enumerable.Repeat(new string('x', 254), 16)) + "/";
                files[input] = files[input] with { RelativePath = prefix + new string('y', 15) };
                Assert.Equal(4095, System.Text.Encoding.ASCII.GetByteCount(files[input].RelativePath));
                LinuxApplicationWorkspaceLayout.ValidateFiles(files);
                files[input] = files[input] with { RelativePath = prefix + new string('y', 16) };
                Assert.Equal(4096, System.Text.Encoding.ASCII.GetByteCount(files[input].RelativePath));
                break;
            case "path-nonascii": files[input] = files[input] with { RelativePath = "canary-\u00e9.dat" }; break;
            case "path-empty-segment": files[input] = files[input] with { RelativePath = "canary//input.dat" }; break;
            case "path-component-256": files[input] = files[input] with { RelativePath = new string('x', 256) }; break;
            case "path-dot": files[input] = files[input] with { RelativePath = "canary/./input.dat" }; break;
            case "path-depth-33": files[input] = files[input] with { RelativePath = string.Join('/', Enumerable.Repeat("nested", 33)) + "/input.dat" }; break;
            case "role-unknown": files[input] = files[input] with { Role = (LinuxApplicationBundleRole)99 }; break;
            case "length-zero": files[input] = files[input] with { LengthBytes = 0 }; break;
            case "length-over": files[input] = files[input] with { LengthBytes = 128L * 1024 * 1024 + 1 }; break;
            case "aggregate-over": files = files.Select(file => file with { LengthBytes = 128L * 1024 * 1024 }).ToArray(); break;
            case "digest-null": files[input] = files[input] with { Sha256 = null! }; break;
            case "digest-length": files[input] = files[input] with { Sha256 = new string('a', 63) }; break;
            case "digest-nonhex": files[input] = files[input] with { Sha256 = new string('g', 64) }; break;
            case "dcp-path": ChangeRole(LinuxApplicationBundleRole.Dcp, ".exe"); break;
            case "extension-prefix": ChangeRole(LinuxApplicationBundleRole.DcpExtension, ".extension"); break;
            case "extension-mode": files[^1] = files[^1] with { Mode = 0x124 }; break;
            case "apphost-suffix": ChangeRole(LinuxApplicationBundleRole.AppHost, ".dat"); break;
            case "resource-suffix": ChangeRole(LinuxApplicationBundleRole.Resource, ".dat"); break;
            case "apphost-config-suffix": ChangeRole(LinuxApplicationBundleRole.AppHostRuntimeConfiguration, ".json"); break;
            case "resource-config-suffix": ChangeRole(LinuxApplicationBundleRole.ResourceRuntimeConfiguration, ".json"); break;
            case "manifest-suffix": files = files.Append(Row("manifest-canary.json", LinuxApplicationBundleRole.DependencyManifest, 0x124, 'a')).ToArray(); break;
            case "missing-apphost": RemoveRole(LinuxApplicationBundleRole.AppHost); break;
            case "missing-apphost-config": RemoveRole(LinuxApplicationBundleRole.AppHostRuntimeConfiguration); break;
            case "missing-resource": RemoveRole(LinuxApplicationBundleRole.Resource); break;
            case "missing-resource-config": RemoveRole(LinuxApplicationBundleRole.ResourceRuntimeConfiguration); break;
            case "missing-dcp": RemoveRole(LinuxApplicationBundleRole.Dcp); files[4] = files[4] with { Mode = 0x124 }; break;
            case "missing-input": RemoveRole(LinuxApplicationBundleRole.DeclaredInput); break;
            case "duplicate-apphost": files = files.Append(Row("second.dll", LinuxApplicationBundleRole.AppHost, 0x124, 'a')).ToArray(); break;
            case "duplicate-input": files = files.Append(Row("second-input.dat", LinuxApplicationBundleRole.DeclaredInput, 0x124, 'a')).ToArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Reject(() => LinuxApplicationWorkspaceLayout.ValidateFiles(files));
    }

    [Fact]
    public void BundleAcceptsExactFileSizeAggregateAndTreeDepthWithoutAllocatingFileContents()
    {
        var files = Files(includeExtension: false);
        files[0] = files[0] with { LengthBytes = 128L * 1024 * 1024 };
        LinuxApplicationWorkspaceLayout.ValidateFiles(files);
        var remaining = 512L * 1024 * 1024;
        for (var index = 0; index < files.Length; index++)
        {
            var length = index < 4 ? 128L * 1024 * 1024 - 1 : 2;
            files[index] = files[index] with { LengthBytes = length };
            remaining -= length;
        }
        Assert.Equal(0, remaining);
        LinuxApplicationWorkspaceLayout.ValidateFiles(files);
        var boundary = files.Append(Row(string.Join('/', Enumerable.Repeat("nested", 32)) + "/input.dat",
            LinuxApplicationBundleRole.Dependency, 0x124, 'a')).ToArray();
        // Keep the exact aggregate after adding a one-byte dependency at the deepest permitted directory.
        boundary[0] = boundary[0] with { LengthBytes = boundary[0].LengthBytes - 1 };
        boundary[^1] = boundary[^1] with { LengthBytes = 1 };
        LinuxApplicationWorkspaceLayout.ValidateFiles(boundary);
        Assert.Contains(string.Join('/', Enumerable.Repeat("nested", 32)), LinuxApplicationWorkspaceLayout.FindDirectories(boundary));
    }

    [Theory]
    [InlineData("inventory-null")]
    [InlineData("inventory-over")]
    [InlineData("entry-null")]
    [InlineData("entry-special-mode")]
    [InlineData("directory-name")]
    [InlineData("directory-hash")]
    public void InventoryRejectsIncompleteOrUnselectedMetadataBeforeNativeUse(string mutation)
    {
        var files = Files(includeExtension: true);
        var actual = Observed(files);
        LinuxApplicationWorkspaceLayout.RequireExactInventory(files, actual);
        var directory = Array.FindIndex(actual, static row => row.RelativePath == "dcp");
        switch (mutation)
        {
            case "inventory-null": actual = null!; break;
            case "inventory-over": actual = Enumerable.Repeat(actual[0], LinuxApplicationWorkspaceLayout.MaximumTreeEntries + 1).ToArray(); break;
            case "entry-null": actual[directory] = null!; break;
            case "entry-special-mode": actual[directory] = actual[directory] with { Mode = 0x436d }; break;
            case "directory-name": actual[directory] = actual[directory] with { RelativePath = "directory-canary" }; break;
            case "directory-hash": actual[directory] = actual[directory] with { Sha256 = EntryDigest }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Reject(() => LinuxApplicationWorkspaceLayout.RequireExactInventory(files, actual));
    }

    private static LinuxApplicationWorkspaceLayout Create(IReadOnlyList<LinuxApplicationBundleFile> files) =>
        LinuxApplicationWorkspaceLayout.Create(RunId, "/opt/forge/tools", EntryDigest, 61001, 61002,
            Limits, files, ["input.json"]);

    private static LinuxApplicationBundleFile[] Files(bool includeExtension)
    {
        var files = new List<LinuxApplicationBundleFile>
        {
            Row("apphost.dll", LinuxApplicationBundleRole.AppHost, 0x124, 'a'),
            Row("apphost.runtimeconfig.json", LinuxApplicationBundleRole.AppHostRuntimeConfiguration, 0x124, 'b'),
            Row("resource.dll", LinuxApplicationBundleRole.Resource, 0x124, 'c'),
            Row("resource.runtimeconfig.json", LinuxApplicationBundleRole.ResourceRuntimeConfiguration, 0x124, 'd'),
            Row("dcp/dcp", LinuxApplicationBundleRole.Dcp, 0x16d, 'e'),
            Row("input.json", LinuxApplicationBundleRole.DeclaredInput, 0x124, 'f'),
        };
        if (includeExtension) files.Add(Row("dcp/ext/worker", LinuxApplicationBundleRole.DcpExtension, 0x16d, 'a'));
        return files.ToArray();
    }

    private static LinuxApplicationBundleFile Row(string path, LinuxApplicationBundleRole role, uint mode, char hash) =>
        new(path, role, 16, new string(hash, 64), mode);

    private static LinuxApplicationObservedEntry[] Observed(IReadOnlyList<LinuxApplicationBundleFile> files)
    {
        ulong inode = 1;
        var rows = new List<LinuxApplicationObservedEntry>();
        foreach (var directory in LinuxApplicationWorkspaceLayout.FindDirectories(files).Append(string.Empty))
            rows.Add(new(directory, true, 0, 0, 0x416d, 2, inode++, 0, null));
        foreach (var file in files)
            rows.Add(new(file.RelativePath, false, 0, 0, (ushort)(0x8000 | file.Mode), 1,
                inode++, file.LengthBytes, file.Sha256));
        return rows.ToArray();
    }

    [Fact]
    public void ManagedToolProbeRequiresActualManagedBytesAndTheSelectedResourceDigest()
    {
        var managed = File.ReadAllBytes(typeof(LinuxApplicationWorkspaceTests).Assembly.Location);
        var hash = EvidenceDigest.Sha256(managed);
        LinuxApplicationWorkspace.RequireManagedToolProbe(managed, hash);
        Reject(() => LinuxApplicationWorkspace.RequireManagedToolProbe(managed, new string('0', 64)));
        Reject(() => LinuxApplicationWorkspace.RequireManagedToolProbe(ReadOnlyMemory<byte>.Empty, hash));
        var invalid = "CANARY-not-a-managed-assembly"u8.ToArray();
        Reject(() => LinuxApplicationWorkspace.RequireManagedToolProbe(invalid, EvidenceDigest.Sha256(invalid)));
    }

    [Fact]
    public void ScratchHomeFitsPinnedAspireBackchannelEvenAtMaximumPidLength()
    {
        var layout = Create(Files(includeExtension: true));
        // Aspire 13.4.4 uses eleven AppHost-id plus eight random-id characters, dot, PID and NUL.
        // This is path-length data only, not an executed AppHost or kernel socket capability.
        var path = layout.ScratchRoot + "/.aspire/cli/bch/" + new string('a', 19) + "." + int.MaxValue;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) + 1 <= 108);
    }

    [Fact]
    public void LinuxCreatedWriteOnlyMarkerReopensAsSameReadonlyInode()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Path.Combine(Path.GetTempPath(), "application-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var parent = new SafeFileHandle((nint)OpenTestDirectory(directory, 0x10000 | 0x80000), ownsHandle: true);
            Assert.False(parent.IsInvalid);
            var path = Path.Combine(directory, "armed");
            using var writer = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            RandomAccess.Write(writer, "armed\n"u8, 0);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            RequireDescriptorAccessDenied(() => { _ = RandomAccess.Read(writer, new byte[6], 0); });
            using var readable = LinuxApplicationWorkspace.OpenMarkerReadOnly(parent, writer, default);
            writer.Dispose();
            var bytes = new byte[6];
            Assert.Equal(6, RandomAccess.Read(readable, bytes, 0));
            Assert.Equal("armed\n"u8.ToArray(), bytes);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));
            RequireDescriptorAccessDenied(() => RandomAccess.Write(readable, "changed"u8, 0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LinuxMarkerReopenRejectsDeterministicNameReplacement()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Path.Combine(Path.GetTempPath(), "application-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var parent = new SafeFileHandle((nint)OpenTestDirectory(directory, 0x10000 | 0x80000), ownsHandle: true);
            Assert.False(parent.IsInvalid);
            var path = Path.Combine(directory, "armed");
            File.WriteAllText(path, "original");
            using var original = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            File.Move(path, Path.Combine(directory, "held-original"));
            File.WriteAllText(path, "replacement");
            Reject(() => { using var rejected = LinuxApplicationWorkspace.OpenMarkerReadOnly(parent, original, default); });
            Assert.Equal("original", File.ReadAllText(Path.Combine(directory, "held-original")));
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RequireDescriptorAccessDenied(Action operation)
    {
        // Linux .NET maps EBADF to UnauthorizedAccessException with an IOException cause;
        // the managed surface can also expose IOException directly. Either must remain a rejection.
        var error = Record.Exception(operation);
        Assert.True(error is IOException or UnauthorizedAccessException { InnerException: IOException });
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenTestDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
