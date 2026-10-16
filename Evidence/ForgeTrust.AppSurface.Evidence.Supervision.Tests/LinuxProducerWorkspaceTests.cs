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

    [Fact]
    public void ResultsTokenGrammarCannotSelectAPath()
    {
        Assert.True(LinuxProducerWorkspaceLayout.IsResultsToken("coverage-8c4d2f"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken(""));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("../outside"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken("coverage_token"));
        Assert.False(LinuxProducerWorkspaceLayout.IsResultsToken(new string('a', 97)));
    }
}
