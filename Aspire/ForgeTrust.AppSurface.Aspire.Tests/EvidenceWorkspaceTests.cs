using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceWorkspaceTests
{
    [Fact]
    public async Task DisposeAsync_RemovesReadOnlyOwnedFilesAndPreservesReadOnlyCallerFiles()
    {
        var parent = NewDirectory();
        var sentinel = Path.Join(parent, "caller.txt");
        try
        {
            File.WriteAllText(sentinel, "caller bytes");
            File.SetAttributes(sentinel, File.GetAttributes(sentinel) | FileAttributes.ReadOnly);
            var workspace = EvidenceWorkspace.Create(parent);
            var owned = workspace.GetPath("object.dat");
            File.WriteAllText(owned, "owned bytes");
            File.SetAttributes(owned, File.GetAttributes(owned) | FileAttributes.ReadOnly);

            await workspace.DisposeAsync();

            Assert.False(Directory.Exists(workspace.RootPath));
            Assert.Equal("caller bytes", File.ReadAllText(sentinel));
            Assert.True(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(sentinel)) File.SetAttributes(sentinel, File.GetAttributes(sentinel) & ~FileAttributes.ReadOnly);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CheckBudget_RejectsInPlaceMarkerTamperingAndPreservesItsBytes()
    {
        var parent = NewDirectory();
        try
        {
            var workspace = EvidenceWorkspace.Create(parent);
            var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
            var created = File.GetCreationTimeUtc(marker);
            var original = File.ReadAllBytes(marker);
            var changed = original.Select(value => value == (byte)'a' ? (byte)'b' : (byte)'a').ToArray();
            using (var output = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.None))
                output.Write(changed);
            if (!OperatingSystem.IsLinux()) File.SetCreationTimeUtc(marker, created);

            var exception = Assert.Throws<IOException>(workspace.CheckBudget);

            // Filesystems without a stable file birth time detect the marker metadata change first.
            Assert.Equal(File.GetCreationTimeUtc(marker) == created
                ? "Evidence workspace ownership marker is invalid."
                : "Evidence workspace root is missing or was replaced.", exception.Message);
            await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
            Assert.Equal(changed, File.ReadAllBytes(marker));
            Assert.True(Directory.Exists(workspace.RootPath));
            File.WriteAllBytes(marker, original);
            if (OperatingSystem.IsLinux()) File.SetLastWriteTimeUtc(marker, created);
            else File.SetCreationTimeUtc(marker, created);
            Assert.Equal(created, File.GetCreationTimeUtc(marker));
            await workspace.DisposeAsync();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionFailure_RemovesOnlyTheVerifiedEmptyRootAndPreservesTheOriginalException(bool identityRetained)
    {
        var parent = NewDirectory();
        try
        {
            var sentinel = Path.Join(parent, "caller.txt");
            File.WriteAllText(sentinel, "caller bytes");
            var failure = new IOException("admission failure");
            string? allocatedRoot = null;
            void Fail(string root) { allocatedRoot = root; throw failure; }

            var observed = Assert.Throws<IOException>(() => EvidenceWorkspace.CreateWithAdmissionHooksForTests(
                parent, identityRetained ? null : Fail, identityRetained ? Fail : null));

            Assert.Same(failure, observed);
            Assert.NotNull(allocatedRoot);
            Assert.False(Directory.Exists(allocatedRoot));
            Assert.Equal("caller bytes", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.EnumerateFileSystemEntries(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionFailure_PreservesUnknownRootEntriesRatherThanDeletingThem(bool identityRetained)
    {
        var parent = NewDirectory();
        try
        {
            var failure = new IOException("admission failure");
            string? allocatedRoot = null;
            string? unknownFile = null;
            void Fail(string root)
            {
                allocatedRoot = root;
                unknownFile = Path.Join(root, "unknown.txt");
                File.WriteAllText(unknownFile, "unknown bytes");
                throw failure;
            }

            var observed = Assert.Throws<IOException>(() => EvidenceWorkspace.CreateWithAdmissionHooksForTests(
                parent, identityRetained ? null : Fail, identityRetained ? Fail : null));

            Assert.Same(failure, observed);
            Assert.NotNull(allocatedRoot);
            Assert.NotNull(unknownFile);
            Assert.True(Directory.Exists(allocatedRoot));
            Assert.Equal("unknown bytes", File.ReadAllText(unknownFile));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void AdmissionFailure_PreservesAnAmbiguousPartialMarker()
    {
        var parent = NewDirectory();
        try
        {
            var failure = new IOException("marker write failed");
            string? marker = null;
            var observed = Assert.Throws<IOException>(() => EvidenceWorkspace.CreateWithAdmissionHooksForTests(parent, null, root =>
            {
                marker = Path.Join(root, ".appsurface-evidence-workspace-owner");
                File.WriteAllText(marker, "partial marker");
                throw failure;
            }));

            Assert.Same(failure, observed);
            Assert.NotNull(marker);
            Assert.Equal("partial marker", File.ReadAllText(marker));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void LinuxIdentity_RejectsNonDirectoriesAndCannotMatchAfterItsHandleIsReleased()
    {
        if (!OperatingSystem.IsLinux()) return;
        var parent = NewDirectory();
        try
        {
            var file = Path.Join(parent, "caller.txt");
            File.WriteAllText(file, "caller bytes");
            Assert.Throws<IOException>(() => EvidenceWorkspaceLinuxIdentity.Open(file));
            using var identity = EvidenceWorkspaceLinuxIdentity.Open(parent);
            Assert.True(identity.Matches(parent));
            identity.Dispose();
            Assert.False(identity.Matches(parent));
            Assert.Equal("caller bytes", File.ReadAllText(file));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

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
            // Root entry changes may change CreationTimeUtc on Linux filesystems without stable birth times.
            // Ordinary producer writes must preserve ownership; replacement tests below still reject a new root.
            var direct = workspace.GetPath("direct-result.json");
            File.WriteAllText(direct, "producer output");
            workspace.CheckBudget();
            File.Delete(direct);
            workspace.CheckBudget();
            Assert.Equal(nested, workspace.GetPath("artifacts/result.json"));
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
    public async Task CreateForTests_DefersInjectedMeasurementsUntilAnExplicitBudgetCheck()
    {
        var parent = NewDirectory();
        var logicalChecks = 0;
        var freeChecks = 0;

        try
        {
            await using var workspace = EvidenceWorkspace.CreateForTests(
                parent,
                new EvidenceWorkspaceOptions(MaximumBytes: 10, MinimumFreeBytes: 20),
                _ =>
                {
                    logicalChecks++;
                    return 1;
                },
                _ =>
                {
                    freeChecks++;
                    return 20;
                });

            Assert.Equal(0, logicalChecks);
            Assert.Equal(0, freeChecks);

            workspace.CheckBudget();

            Assert.Equal(1, logicalChecks);
            Assert.Equal(1, freeChecks);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
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
    public async Task CheckBudget_RejectsUseAfterSuccessfulDispose()
    {
        var parent = NewDirectory();
        try
        {
            var workspace = EvidenceWorkspace.Create(parent);
            await workspace.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(workspace.CheckBudget);
            Assert.False(Directory.Exists(workspace.RootPath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
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
        var created = File.GetCreationTimeUtc(marker);
        var retainedMarker = Path.Join(parent, "original-owner-marker");
        File.Move(marker, retainedMarker);
        try
        {
            File.WriteAllText(marker, "different-owner");
            if (!OperatingSystem.IsLinux()) File.SetCreationTimeUtc(marker, created);

            Assert.Throws<IOException>(workspace.CheckBudget);
            await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
            Assert.True(Directory.Exists(workspace.RootPath));
            Assert.Equal("different-owner", File.ReadAllText(marker));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
            File.Move(retainedMarker, marker);
            await workspace.DisposeAsync();
        }
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task CheckBudget_RejectsInventoryEntryAndDepthLimits()
    {
        var parent = NewDirectory();
        var tooMany = EvidenceWorkspace.CreateForTests(parent, new EvidenceWorkspaceOptions(MaxEntries: 1), _ => 0, _ => long.MaxValue);
        File.WriteAllText(tooMany.GetPath("one"), "1");
        File.WriteAllText(tooMany.GetPath("two"), "2");
        Assert.Throws<IOException>(tooMany.CheckBudget);
        File.Delete(Path.Join(tooMany.RootPath, "one"));
        File.Delete(Path.Join(tooMany.RootPath, "two"));
        await tooMany.DisposeAsync();

        var tooDeep = EvidenceWorkspace.CreateForTests(parent, new EvidenceWorkspaceOptions(MaxDepth: 1), _ => 0, _ => long.MaxValue);
        Directory.CreateDirectory(tooDeep.GetPath("one/two"));
        Assert.Throws<IOException>(tooDeep.CheckBudget);
        Directory.Delete(Path.Join(tooDeep.RootPath, "one"), recursive: true);
        await tooDeep.DisposeAsync();
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
        var retained = Path.Join(parent, "original");
        Directory.Move(workspace.RootPath, retained);

        var exception = await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.Contains("missing or was replaced", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(sentinel));
        Directory.Move(retained, workspace.RootPath);
        await workspace.DisposeAsync();
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task DisposeAsync_RejectsAReplacementDirectoryAtTheOwnedPath()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        var retained = Path.Join(parent, "original");
        Directory.Move(workspace.RootPath, retained);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(Path.Join(workspace.RootPath, "replacement.txt"), "not owned");

        await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.True(File.Exists(Path.Join(workspace.RootPath, "replacement.txt")));
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.Move(retained, workspace.RootPath);
        await workspace.DisposeAsync();
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task DisposeAsync_RejectsReplacementWithCopiedMarker()
    {
        var parent = NewDirectory();
        var workspace = EvidenceWorkspace.Create(parent);
        var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
        var markerContents = File.ReadAllText(marker);
        var rootCreated = Directory.GetCreationTimeUtc(workspace.RootPath);
        var markerCreated = File.GetCreationTimeUtc(marker);
        var retained = Path.Join(parent, "original");
        Directory.Move(workspace.RootPath, retained);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(marker, markerContents);
        var replacement = Path.Join(workspace.RootPath, "replacement.txt");
        File.WriteAllText(replacement, "preserve replacement");
        if (OperatingSystem.IsWindows())
        {
            Directory.SetCreationTimeUtc(workspace.RootPath, rootCreated);
            File.SetCreationTimeUtc(marker, markerCreated);
            Assert.Equal(rootCreated, Directory.GetCreationTimeUtc(workspace.RootPath));
            Assert.Equal(markerCreated, File.GetCreationTimeUtc(marker));
        }

        Assert.Throws<IOException>(workspace.CheckBudget);
        Assert.Throws<IOException>(() => workspace.GetPath("new-output.txt"));
        await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
        Assert.True(File.Exists(marker));
        Assert.Equal("preserve replacement", File.ReadAllText(replacement));
        Assert.True(Directory.Exists(retained));
        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.Move(retained, workspace.RootPath);
        await workspace.DisposeAsync();
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

    [Fact]
    public async Task Inventory_ShouldAcceptTheLargestDeclaredEntryBudgetWithoutOverflow()
    {
        var parent = NewDirectory();
        try
        {
            await using var workspace = EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MinimumFreeBytes: 0, MaxEntries: int.MaxValue));
            File.WriteAllText(workspace.GetPath("report.txt"), "evidence");
            workspace.CheckBudget();
            Assert.Equal("evidence", File.ReadAllText(workspace.GetPath("report.txt")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_ShouldCleanItsOwnRootWhenTheInitialBudgetCannotBeAdmitted(bool logicalLimit)
    {
        var parent = NewDirectory();
        try
        {
            var sentinel = Path.Join(parent, "caller.txt");
            File.WriteAllText(sentinel, "preserve");
            var options = logicalLimit
                ? new EvidenceWorkspaceOptions(MaximumBytes: 0, MinimumFreeBytes: 0)
                : new EvidenceWorkspaceOptions(MinimumFreeBytes: long.MaxValue);

            Assert.Throws<EvidenceWorkspaceBudgetException>(() => EvidenceWorkspace.Create(parent, options));

            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.Empty(Directory.EnumerateDirectories(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Create_ShouldRejectALinkedParentWithoutTouchingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        var parent = NewDirectory();
        var target = NewDirectory();
        try
        {
            var link = Path.Join(parent, "linked-parent");
            Directory.CreateSymbolicLink(link, target);
            File.WriteAllText(Path.Join(target, "caller.txt"), "preserve");

            Assert.Throws<IOException>(() => EvidenceWorkspace.Create(link));

            Assert.Equal("preserve", File.ReadAllText(Path.Join(target, "caller.txt")));
            Assert.Empty(Directory.EnumerateDirectories(target));
        }
        finally { Directory.Delete(parent, recursive: true); Directory.Delete(target, recursive: true); }
    }

    [Fact]
    public async Task CreateForTests_AllowsDefaultOptionsAndAWorkspaceWithinBothBudgets()
    {
        var parent = NewDirectory();
        try
        {
            await using var workspace = EvidenceWorkspace.CreateForTests(parent, null, _ => 9, _ => long.MaxValue);
            workspace.CheckBudget();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task CheckBudget_ReportsFreeSpaceFailureWhenLogicalBudgetIsWithinLimit()
    {
        var parent = NewDirectory();
        try
        {
            await using var workspace = EvidenceWorkspace.CreateForTests(
                parent,
                new EvidenceWorkspaceOptions(MaximumBytes: 10, MinimumFreeBytes: 20),
                _ => 9,
                _ => 19);

            var exception = Assert.Throws<EvidenceWorkspaceBudgetException>(workspace.CheckBudget);
            Assert.Equal(9, exception.LogicalBytes);
            Assert.Equal(10, exception.MaximumBytes);
            Assert.Equal(19, exception.FreeBytes);
            Assert.Equal(20, exception.MinimumFreeBytes);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task CheckBudget_RejectsMissingOwnershipMarkerAndPreservesWorkspace()
    {
        var parent = NewDirectory();
        try
        {
            var workspace = EvidenceWorkspace.Create(parent);
            var marker = Path.Join(workspace.RootPath, ".appsurface-evidence-workspace-owner");
            var retainedMarker = Path.Join(parent, "original-owner-marker");
            File.Move(marker, retainedMarker);
            try
            {
                Assert.Throws<IOException>(workspace.CheckBudget);
                await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
                Assert.True(Directory.Exists(workspace.RootPath));
                Assert.False(File.Exists(marker));
            }
            finally
            {
                File.Move(retainedMarker, marker);
                await workspace.DisposeAsync();
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task CheckBudget_RejectsRootReplacedWithALinkAndPreservesTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        try
        {
            var workspace = EvidenceWorkspace.Create(parent);
            var sentinel = Path.Join(external, "caller-owned.txt");
            File.WriteAllText(sentinel, "preserve");
            Directory.Delete(workspace.RootPath, recursive: true);
            Directory.CreateSymbolicLink(workspace.RootPath, external);

            Assert.Throws<IOException>(workspace.CheckBudget);
            await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void Create_PreservesItsRootWhenCleanupCannotReinventoryAfterAnInitialFailure()
    {
        var parent = NewDirectory();
        try
        {
            var sentinel = Path.Join(parent, "caller.txt");
            File.WriteAllText(sentinel, "preserve");

            Assert.Throws<IOException>(() => EvidenceWorkspace.Create(parent, new EvidenceWorkspaceOptions(MaxDepth: 0)));

            Assert.Equal("preserve", File.ReadAllText(sentinel));
            var roots = Directory.EnumerateDirectories(parent).ToArray();
            Assert.Single(roots);
            Directory.Delete(roots[0], recursive: true);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task GetPath_RejectsLiveLinkInNestedChildDirectoryAndPreservesTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = NewDirectory();
        var external = NewDirectory();
        try
        {
            var workspace = EvidenceWorkspace.Create(parent);
            var nested = Path.Join(workspace.RootPath, "nested");
            Directory.CreateDirectory(nested);
            var targetFile = Path.Join(external, "keep.txt");
            File.WriteAllText(targetFile, "keep");
            var link = Path.Join(nested, "linked");
            Directory.CreateSymbolicLink(link, external);

            Assert.Throws<IOException>(() => workspace.GetPath("nested/linked/result.json"));
            Assert.Equal("keep", File.ReadAllText(targetFile));
            Directory.Delete(link);
            await workspace.DisposeAsync();
            Assert.False(Directory.Exists(workspace.RootPath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsJunctions_AreRejectedWithoutDeletingTheCallerTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = NewDirectory();
        var target = NewDirectory();
        try
        {
            var sentinel = Path.Join(target, "caller.txt");
            File.WriteAllText(sentinel, "preserve");
            await using var workspace = EvidenceWorkspace.Create(parent);
            var link = Path.Join(workspace.RootPath, "linked");
            var create = await new EvidenceCommandRunner().RunAsync(new EvidenceCommandRequest("cmd.exe",
                ["/d", "/c", "mklink", "/J", link, target], parent, TimeSpan.FromSeconds(10)));
            Assert.True(create.ExitCode == 0 && !create.OutputTruncated, create.StandardError);

            Assert.Throws<IOException>(() => workspace.GetPath("linked/result.json"));
            Assert.Throws<IOException>(workspace.CheckBudget);
            await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Directory.Delete(link);
            await workspace.DisposeAsync();
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(parent, recursive: true); Directory.Delete(target, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"appsurface-evidence-workspace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
