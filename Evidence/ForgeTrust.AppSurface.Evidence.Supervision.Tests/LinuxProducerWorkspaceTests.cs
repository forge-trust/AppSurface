using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable checks for producer workspace path data; these tests never construct native workspace state.</summary>
public sealed class LinuxProducerWorkspaceTests
{
    private static readonly LinuxRunAccountSnapshot Accounts = new(21001, 21002, 21003, 21004, 21005);

    [Fact]
    public void CombinedCopyReservationsRejectBeforeTheNextTreeCanWriteBeyondItsBudget()
    {
        var budget = new LinuxProducerCopyBudget();
        for (var i = 0; i < 4; i++) budget.ReserveFile(LinuxProducerWorkspaceLayout.MaximumFileBytes);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes, budget.Bytes);
        var writes = 0;
        Assert.Throws<EvidenceAdmissionException>(() => { budget.ReserveFile(1); writes++; });
        Assert.Equal(0, writes);
        Assert.Equal(4, budget.Nodes);
        var nodes = new LinuxProducerCopyBudget();
        for (var i = 0; i < LinuxProducerWorkspaceLayout.MaximumNodes; i++) nodes.ReserveDirectory();
        Assert.Throws<EvidenceAdmissionException>(nodes.ReserveDirectory);
        Assert.Throws<EvidenceAdmissionException>(() => new LinuxProducerCopyBudget().ReserveFile(0));
        Assert.Throws<EvidenceAdmissionException>(() => new LinuxProducerCopyBudget().ReserveFile(LinuxProducerWorkspaceLayout.MaximumFileBytes + 1));
    }

    [Fact]
    public void LayoutMapsOriginalSolutionIntoFreshRunScopedProducerRoot()
    {
        var runId = Guid.Parse("c98ae387-23dc-4d04-9c3d-6a08f79c3712");

        var layout = LinuxProducerWorkspaceLayout.Create(runId, Accounts,
            "/run/appsurface-evidence-input-subject", "/run/appsurface-evidence-input-subject/tests/Subject.csproj");

        Assert.Equal("appsurface-evidence-producer-data-c98ae38723dc4d049c3d6a08f79c3712", layout.RootName);
        Assert.Equal("/run/" + layout.RootName, layout.Root);
        Assert.Equal(layout.Root + "/results", layout.ResultsRoot);
        Assert.Equal(layout.Root + "/subject", layout.SubjectRoot);
        Assert.Equal(layout.Root + "/subject/tests/Subject.csproj", layout.SolutionPath);
        Assert.Equal(layout.Root + "/scratch", layout.ScratchRoot);
        Assert.Equal(layout.ScratchRoot + "/nuget", layout.PackageRoot);
    }

    [Theory]
    [InlineData("/run/other/Subject.csproj")]
    [InlineData("/run/input/../other/Subject.csproj")]
    [InlineData("/run/input/Subject.csproj/")]
    public void LayoutRejectsSolutionsOutsideTheExactSubjectRoot(string solution) =>
        Assert.Throws<EvidenceAdmissionException>(() => LinuxProducerWorkspaceLayout.Create(Guid.NewGuid(),
            Accounts, "/run/input", solution));

    [Theory]
    [InlineData("generation")]
    [InlineData("accounts")]
    [InlineData("solution")]
    [InlineData("worker-uid-zero")]
    [InlineData("worker-uid-max")]
    [InlineData("subject-uid-zero")]
    [InlineData("subject-uid-max")]
    [InlineData("worker-gid-zero")]
    [InlineData("worker-gid-max")]
    [InlineData("subject-gid-zero")]
    [InlineData("subject-gid-max")]
    [InlineData("results-gid-zero")]
    [InlineData("results-gid-max")]
    [InlineData("shared-uid")]
    [InlineData("shared-primary-gid")]
    [InlineData("worker-results-gid")]
    [InlineData("subject-results-gid")]
    public void LayoutRejectsOneIncompleteOrOverlappingIdentityBeforeProjectingAnyPaths(string mutation)
    {
        var runId = Guid.Parse("a487f5b8-f334-4bbe-ae4e-586cbf585d0b");
        var accounts = Accounts;
        string? solution = "/run/layout-canary/Subject.csproj";
        LinuxProducerWorkspaceLayout Create() => LinuxProducerWorkspaceLayout.Create(runId, accounts, "/run/layout-canary", solution);
        Assert.EndsWith("/subject/Subject.csproj", Create().SolutionPath, StringComparison.Ordinal);
        switch (mutation)
        {
            case "generation": runId = Guid.Empty; break;
            case "accounts": accounts = null!; break;
            case "solution": solution = null; break;
            case "worker-uid-zero": accounts = accounts with { WorkerUid = 0 }; break;
            case "worker-uid-max": accounts = accounts with { WorkerUid = uint.MaxValue }; break;
            case "subject-uid-zero": accounts = accounts with { SubjectUid = 0 }; break;
            case "subject-uid-max": accounts = accounts with { SubjectUid = uint.MaxValue }; break;
            case "worker-gid-zero": accounts = accounts with { WorkerGid = 0 }; break;
            case "worker-gid-max": accounts = accounts with { WorkerGid = uint.MaxValue }; break;
            case "subject-gid-zero": accounts = accounts with { SubjectGid = 0 }; break;
            case "subject-gid-max": accounts = accounts with { SubjectGid = uint.MaxValue }; break;
            case "results-gid-zero": accounts = accounts with { ResultsGid = 0 }; break;
            case "results-gid-max": accounts = accounts with { ResultsGid = uint.MaxValue }; break;
            case "shared-uid": accounts = accounts with { SubjectUid = accounts.WorkerUid }; break;
            case "shared-primary-gid": accounts = accounts with { SubjectGid = accounts.WorkerGid }; break;
            case "worker-results-gid": accounts = accounts with { ResultsGid = accounts.WorkerGid }; break;
            case "subject-results-gid": accounts = accounts with { ResultsGid = accounts.SubjectGid }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var error = Assert.Throws<EvidenceAdmissionException>(() => Create());
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("/run/layout-canary", error.ToString());
    }

    [Theory]
    [InlineData("inode")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("file-type")]
    [InlineData("group-search")]
    [InlineData("other-read")]
    [InlineData("set-id")]
    public void DirectoryComparisonRequiresTheExactPrivateDirectoryData(string mutation)
    {
        var original = new LinuxProtectedMetadata(1, 2, 3, Accounts.SubjectUid, Accounts.SubjectGid,
            0x41c0, 2, 0, 0, 0, 0, 0);
        LinuxProducerWorkspaceLayout.RequireDirectory(original, Accounts.SubjectUid, Accounts.SubjectGid, 0x1c0);
        var changed = mutation switch
        {
            "inode" => original with { Inode = 0 },
            "uid" => original with { Uid = Accounts.WorkerUid },
            "gid" => original with { Gid = Accounts.WorkerGid },
            "file-type" => original with { Mode = 0x81c0 },
            "group-search" => original with { Mode = 0x41c8 },
            "other-read" => original with { Mode = 0x41c4 },
            "set-id" => original with { Mode = 0x49c0 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProducerWorkspaceLayout.RequireDirectory(changed, Accounts.SubjectUid, Accounts.SubjectGid, 0x1c0));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void OpaqueResultsTokenAcceptsAsciiClassesAndExactBoundsButNeverControlOrUnicodePaths()
    {
        Assert.True(LinuxProducerWorkspaceLayout.IsResultsToken("A"));
        Assert.True(LinuxProducerWorkspaceLayout.IsResultsToken("Coverage-Az09"));
        Assert.True(LinuxProducerWorkspaceLayout.IsResultsToken(new string('Z', 96)));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken(null!));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("\0"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("é"));
    }

    [Fact]
    public void ResultsTokenGrammarCannotSelectAPath()
    {
        Assert.True(LinuxProducerWorkspaceLayout.IsResultsToken("coverage-8c4d2f"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken(""));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("../outside"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("coverage_token"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken(new string('a', 97)));
    }

    /// <summary>A rejected two-byte reservation preserves the final byte and all previous consumption.</summary>
    [Fact]
    public void MixedCopyReservationsPreserveOneByteAfterAdjacentTwoByteRejection()
    {
        static LinuxProducerCopyBudget WithOneByteRemaining()
        {
            var budget = new LinuxProducerCopyBudget();
            for (var i = 0; i < 3; i++) budget.ReserveFile(LinuxProducerWorkspaceLayout.MaximumFileBytes);
            budget.ReserveFile(LinuxProducerWorkspaceLayout.MaximumFileBytes - 1);
            return budget;
        }

        var valid = WithOneByteRemaining();
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes - 1, valid.Bytes);
        valid.ReserveFile(1);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes, valid.Bytes);
        Assert.Equal(5, valid.Nodes);

        var candidate = WithOneByteRemaining();
        var error = Assert.Throws<EvidenceAdmissionException>(() => candidate.ReserveFile(2));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal("ASEVD402: The protected producer workspace is unavailable or changed."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes - 1, candidate.Bytes);
        Assert.Equal(4, candidate.Nodes);
        candidate.ReserveFile(1);
        Assert.Equal(valid.Bytes, candidate.Bytes);
        Assert.Equal(valid.Nodes, candidate.Nodes);
    }

    /// <summary>The final node may contain bytes, and a rejected next file cannot consume either counter.</summary>
    [Fact]
    public void MixedCopyReservationsPreserveBytesWhenTheLastNodeIsAlreadyConsumed()
    {
        var budget = new LinuxProducerCopyBudget();
        for (var i = 0; i < LinuxProducerWorkspaceLayout.MaximumNodes - 1; i++) budget.ReserveDirectory();
        budget.ReserveFile(1);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumNodes, budget.Nodes);
        Assert.Equal(1L, budget.Bytes);
        var error = Assert.Throws<EvidenceAdmissionException>(() => budget.ReserveFile(1));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal("ASEVD402: The protected producer workspace is unavailable or changed."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumNodes, budget.Nodes);
        Assert.Equal(1L, budget.Bytes);
    }

    /// <summary>A directory still consumes a node at the byte limit, while the next file remains rejected.</summary>
    [Fact]
    public void FullByteBudgetAllowsDirectoryWithoutReleasingEarlierConsumption()
    {
        var budget = new LinuxProducerCopyBudget();
        for (var i = 0; i < 4; i++) budget.ReserveFile(LinuxProducerWorkspaceLayout.MaximumFileBytes);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes, budget.Bytes);
        Assert.Equal(4, budget.Nodes);
        budget.ReserveDirectory();
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes, budget.Bytes);
        Assert.Equal(5, budget.Nodes);
        var error = Assert.Throws<EvidenceAdmissionException>(() => budget.ReserveFile(1));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal("ASEVD402: The protected producer workspace is unavailable or changed."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(LinuxProducerWorkspaceLayout.MaximumTreeBytes, budget.Bytes);
        Assert.Equal(5, budget.Nodes);
    }

    /// <summary>Ancestor metadata requires root UID independently of its otherwise valid root group and mode.</summary>
    [Fact]
    public void AncestorComparisonRejectsOnlyTheChangedRootUid()
    {
        var valid = new LinuxProtectedMetadata(1, 2, 3, 0, 0, 0x41ed, 2, 0, 0, 0, 0, 0);
        LinuxProducerWorkspaceLayout.RequireAncestor(valid);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProducerWorkspaceLayout.RequireAncestor(valid with { Uid = 1 }));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal("ASEVD402: The protected producer workspace is unavailable or changed."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Ancestor metadata requires root GID independently of its otherwise valid root owner and mode.</summary>
    [Fact]
    public void AncestorComparisonRejectsOnlyTheChangedRootGid()
    {
        var valid = new LinuxProtectedMetadata(1, 2, 3, 0, 0, 0x41ed, 2, 0, 0, 0, 0, 0);
        LinuxProducerWorkspaceLayout.RequireAncestor(valid);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProducerWorkspaceLayout.RequireAncestor(valid with { Gid = 1 }));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Equal("ASEVD402: The protected producer workspace is unavailable or changed."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
    }
}
