using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceWorkspaceTests
{
    [Fact]
    public void Create_RejectsInvalidOptionsAndParents()
    {
        var parent = NewDirectory();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MaximumBytes: -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MinimumFreeBytes: -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MaxEntries: 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MaxDepth: -1)));
            Assert.Throws<ArgumentException>(() => EvidenceWorkspace.Create(" "));
            Assert.Throws<DirectoryNotFoundException>(() => EvidenceWorkspace.Create(Path.Join(parent, "missing")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void CreateForTests_RejectsNullCounters()
    {
        var parent = NewDirectory();
        try
        {
            Assert.Throws<ArgumentNullException>(() => EvidenceWorkspace.CreateForTests(parent, null, null!, _ => 0));
            Assert.Throws<ArgumentNullException>(() => EvidenceWorkspace.CreateForTests(parent, null, _ => 0, null!));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndDispose_DeletesOnlyFreshChildAndNormalizesOwnedPaths()
    {
        var parent = NewDirectory();
        var durable = Path.Join(parent, "durable-evidence");
        Directory.CreateDirectory(durable);
        File.WriteAllText(Path.Join(durable, "manifest.json"), "durable");

        string root;
        await using (var workspace = EvidenceWorkspace.Create(parent))
        {
            root = workspace.RootPath;
            var nested = workspace.GetPath("artifacts/result.json");
            Assert.Equal(Path.GetFullPath(Path.Join(workspace.RootPath, "artifacts/result.json")), nested);
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "evidence");
            workspace.CheckBudget();
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Join(durable, "manifest.json")));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task CheckBudget_UsesInjectedCountersAndReportsBothLimits()
    {
        var parent = NewDirectory();
        await using (var workspace = EvidenceWorkspace.CreateForTests(
            parent,
            new EvidenceWorkspaceOptions(MaximumBytes: 10, MinimumFreeBytes: 20),
            _ => 11,
            _ => 19))
        {
            var exception = Assert.Throws<EvidenceWorkspaceBudgetException>(workspace.CheckBudget);
            Assert.Equal(11, exception.LogicalBytes);
            Assert.Equal(10, exception.MaximumBytes);
            Assert.Equal(19, exception.FreeBytes);
            Assert.Equal(20, exception.MinimumFreeBytes);
        }

        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task GetPath_RejectsEscapingAndDisposedWorkspace()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        Assert.Throws<ArgumentException>(() => workspace.GetPath("../outside.txt"));
        Assert.Throws<ArgumentException>(() => workspace.GetPath(Path.GetFullPath("outside.txt")));
        await workspace.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => workspace.GetPath("artifact.txt"));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task GetPath_RejectsExistingSymbolicLinkAncestors()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        Directory.CreateSymbolicLink(Path.Join(workspace.RootPath, "linked"), external);

        Assert.Throws<IOException>(() => workspace.GetPath("linked/result.json"));
        Assert.Throws<IOException>(() => workspace.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.Delete(parent, recursive: true);
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public async Task CheckBudget_RejectsMarkerSymbolicLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        var externalMarker = Path.Join(external, "marker");
        File.WriteAllText(externalMarker, "not-owned");
        var workspace = EvidenceWorkspace.Create(parent);
        var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
        File.Delete(marker);
        File.CreateSymbolicLink(marker, externalMarker);

        Assert.Throws<IOException>(workspace.CheckBudget);
        Assert.Throws<IOException>(() => workspace.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Assert.Equal("not-owned", File.ReadAllText(externalMarker));
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.Delete(parent, recursive: true);
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public async Task CheckBudget_RejectsChangedOwnershipMarkerAndCleanupPreservesRoot()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
        File.WriteAllText(marker, "different-owner");

        Assert.Throws<IOException>(workspace.CheckBudget);
        await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.True(Directory.Exists(workspace.RootPath));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public void CheckBudget_RejectsInventoryEntryAndDepthLimits()
    {
        var parent = NewDirectory();
        var tooMany = EvidenceWorkspace.CreateForTests(parent, new EvidenceWorkspaceOptions(MaxEntries: 1), _ => 0, _ => long.MaxValue);
        File.WriteAllText(tooMany.GetPath("one"), "1");
        File.WriteAllText(tooMany.GetPath("two"), "2");
        Assert.Throws<IOException>(tooMany.CheckBudget);
        Directory.Delete(tooMany.RootPath, recursive: true);

        var tooDeep = EvidenceWorkspace.CreateForTests(parent, new EvidenceWorkspaceOptions(MaxDepth: 1), _ => 0, _ => long.MaxValue);
        Directory.CreateDirectory(tooDeep.GetPath("one/two"));
        Assert.Throws<IOException>(tooDeep.CheckBudget);
        Directory.Delete(tooDeep.RootPath, recursive: true);
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public void GetPath_RejectsNullColonAndExistingFileReparsePoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        try
        {
            Assert.Throws<ArgumentNullException>(() => workspace.GetPath(null!));
            Assert.Throws<ArgumentException>(() => workspace.GetPath("artifact:name"));
            var externalFile = Path.Join(external, "external.txt");
            File.WriteAllText(externalFile, "keep");
            File.CreateSymbolicLink(Path.Join(workspace.RootPath, "linked.txt"), externalFile);
            Assert.Throws<IOException>(() => workspace.GetPath("linked.txt"));
        }
        finally
        {
            Directory.Delete(workspace.RootPath, recursive: true);
            Directory.Delete(parent, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_RejectsMissingOrReplacedRootAndPreservesCallerSentinel()
    {
        var parent = NewDirectory();
        var sentinel = Path.Join(parent, "caller-owned.txt");
        File.WriteAllText(sentinel, "keep");
        var workspace = EvidenceWorkspace.Create(parent);
        Directory.Delete(workspace.RootPath, recursive: true);

        var exception = await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.Contains("missing or was replaced", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(sentinel));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task DisposeAsync_RejectsAReplacementDirectoryAtTheOwnedPath()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(Path.Join(workspace.RootPath, "replacement.txt"), "not owned");

        await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.True(File.Exists(Path.Join(workspace.RootPath, "replacement.txt")));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task DisposeAsync_RejectsReplacementWithCopiedMarker()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
        var markerContents = File.ReadAllText(marker);
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(marker, markerContents);

        await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.True(File.Exists(marker));
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task ReparsePointInsideWorkspace_IsRejectedAndExternalTargetSurvives()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        var externalFile = Path.Join(external, "outside.txt");
        File.WriteAllText(externalFile, "keep");
        var workspace = EvidenceWorkspace.Create(parent);
        var link = Path.Join(workspace.RootPath, "linked");
        Directory.CreateSymbolicLink(link, external);

        Assert.Throws<IOException>(workspace.CheckBudget);
        Assert.Throws<IOException>(() => workspace.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Assert.True(File.Exists(externalFile));
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.Delete(parent, recursive: true);
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAfterSuccessfulCleanup()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        await workspace.DisposeAsync();
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(workspace.RootPath));
        Directory.Delete(parent, recursive: true);
    }

    private static string NewDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"appsurface-evidence-workspace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
