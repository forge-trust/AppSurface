using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure projection and inventory tests; these fixtures do not create Linux or account authority.</summary>
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
        Assert.Equal("/run/appsurface-application-7f05e0719a9b4f40a211c9b3064f22d8", layout.Root);
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
            "/run/appsurface-application-7f05e0719a9b4f40a211c9b3064f22d8", EntryDigest,
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

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
    }
}
