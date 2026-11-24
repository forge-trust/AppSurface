using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptTreeContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public void DocumentedTreeBudgetBoundariesAcceptAnEmptyRootAndOneFile(int budget)
    {
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "artifacts"), true, true)!;
        EvidenceReceiptPayload.VerifyExactTree(root, [], maximumTreeEntries: budget);
        using (var file = root.OpenArtifactFile("report.txt", createNew: true)) file.Write("report"u8);

        EvidenceReceiptPayload.VerifyExactTree(root, ["report.txt"], maximumTreeEntries: budget);

        Assert.Equal("report.txt", Assert.Single(root.EnumerateArtifactEntries(8)).Name);
        Assert.Equal("report", File.ReadAllText(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "report.txt")));
    }

    [Fact]
    public void ExactTreeRequiresTheCallerRootAndExpectedInventory()
    {
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "artifacts"), true, true)!;

        Assert.Throws<ArgumentNullException>(() =>
            EvidenceReceiptPayload.VerifyExactTree(null!, []));
        Assert.Throws<ArgumentNullException>(() =>
            EvidenceReceiptPayload.VerifyExactTree(root, null!));

        _ = root.EnumerateArtifactEntries(8);
        Assert.Empty(Directory.GetFileSystemEntries(root.ArtifactDirectoryPath));
    }

    [Fact]
    public void ExactTreeBudgetIncludesDirectoriesAndPreservesCallerOwnershipOnSuccessAndRefusal()
    {
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "artifacts"), true, true)!;
        using (var reports = root.CreateArtifactDirectory("reports"))
        using (var first = reports.OpenArtifactFile("a.txt", createNew: true)) first.Write("a"u8);
        HashSet<string> expected = ["reports/a.txt"];

        EvidenceReceiptPayload.VerifyExactTree(root, expected, maximumTreeEntries: 2);
        _ = root.EnumerateArtifactEntries(8);
        using (var reports = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "reports"), false, true)!)
        using (var second = reports.OpenArtifactFile("b.txt", createNew: true)) second.Write("b"u8);
        expected.Add("reports/b.txt");

        var refused = Assert.Throws<InvalidDataException>(() =>
            EvidenceReceiptPayload.VerifyExactTree(root, expected, maximumTreeEntries: 2));

        Assert.Contains("tree exceeds its bound", refused.Message, StringComparison.Ordinal);
        _ = root.EnumerateArtifactEntries(8);
        EvidenceReceiptPayload.VerifyExactTree(root, expected, maximumTreeEntries: 3);
        Assert.Equal("a", File.ReadAllText(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "reports/a.txt")));
        Assert.Equal("b", File.ReadAllText(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "reports/b.txt")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void ExactTreeBudgetCannotDisableOrRaiseTheProductionBound(int budget)
    {
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "artifacts"), true, true)!;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvidenceReceiptPayload.VerifyExactTree(root, [], maximumTreeEntries: budget));

        _ = root.EnumerateArtifactEntries(8);
        Assert.Empty(Directory.GetFileSystemEntries(root.ArtifactDirectoryPath));
    }
}
