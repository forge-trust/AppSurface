using System.Text;
using ForgeTrust.AppSurface.Evidence;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInGitPatchPathTests
{
    [Theory]
    [InlineData('a', '\a')]
    [InlineData('b', '\b')]
    [InlineData('t', '\t')]
    [InlineData('n', '\n')]
    [InlineData('v', '\v')]
    [InlineData('f', '\f')]
    [InlineData('r', '\r')]
    [InlineData('\\', '\\')]
    [InlineData('"', '"')]
    public void TryDecode_AcceptsEveryNamedGitCEscape(char escape, char decoded)
    {
        var field = "\"prefix\\" + escape + "suffix\"";

        Assert.True(GitPatchPathHeader.TryDecode(field, out var path));
        Assert.Equal(string.Concat("prefix", decoded, "suffix"), path);
        AssertPlannerPath(field, path);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("12", 10)]
    [InlineData("123", 83)]
    public void TryDecode_AcceptsOneTwoAndThreeDigitOctalEscapes(string digits, int decodedCodePoint)
    {
        var field = $"\"prefix\\{digits}suffix\"";

        Assert.True(GitPatchPathHeader.TryDecode(field, out var path));
        Assert.Equal(string.Concat("prefix", (char)decodedCodePoint, "suffix"), path);
        AssertPlannerPath(field, path);
    }

    [Theory]
    [InlineData("\"src/café-雪.cs\"", "src/café-雪.cs")]
    [InlineData("\"src/caf\\303\\251.cs\"", "src/café.cs")]
    public void TryDecode_AcceptsUtf8LiteralAndOctalEncodedUnicode(string field, string expectedPath)
    {
        Assert.True(GitPatchPathHeader.TryDecode(field, out var path));
        Assert.Equal(expectedPath, path);
        AssertPlannerPath(field, expectedPath);
    }

    [Theory]
    [InlineData("src/file.cs", "src/file.cs")]
    [InlineData("src/file.cs\t2026-11-07 12:00:00", "src/file.cs")]
    [InlineData("\"src/with space.cs\"", "src/with space.cs")]
    [InlineData("\"src/with space.cs\"\t2026-11-07 12:00:00", "src/with space.cs")]
    public void TryDecode_AcceptsOptionalTabDelimitedTimestamp(string field, string expectedPath)
    {
        Assert.True(GitPatchPathHeader.TryDecode(field, out var path));
        Assert.Equal(expectedPath, path);
        AssertPlannerPath(field, expectedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("   ")]
    [InlineData("\"src/file.cs\" trailing")]
    [InlineData("\"src/file.cs\" ")]
    [InlineData("\"src/unknown\\q.cs\"")]
    [InlineData("\"src/overflow\\400.cs\"")]
    [InlineData("\"src/unclosed.cs")]
    [InlineData("\"src/trailing\\")]
    [InlineData("\"src/invalid\\200.cs\"")]
    [InlineData("\"src/incomplete\\303.cs\"")]
    public void TryDecode_RejectsMalformedOrInvalidUtf8Fields(string field)
    {
        Assert.False(GitPatchPathHeader.TryDecode(field, out _));
        AssertPlannerRejects(field);
    }

    [Fact]
    public void TryDecode_RejectsEmbeddedNulInQuotedAndUnquotedPaths()
    {
        Assert.False(GitPatchPathHeader.TryDecode(string.Concat("src/", '\0', ".cs"), out _));
        Assert.False(GitPatchPathHeader.TryDecode("\"src/zero\\000.cs\"", out _));
        AssertPlannerRejects(string.Concat("src/", '\0', ".cs"));
        AssertPlannerRejects("\"src/zero\\000.cs\"");
    }

    [Fact]
    public void TryDecode_RejectsLoneSurrogateInQuotedPath()
    {
        var field = "\"src/" + (char)0xD800 + ".cs\"";

        Assert.False(GitPatchPathHeader.TryDecode(field, out _));
        AssertPlannerRejects(field);
    }

    [Theory]
    [InlineData("\\t", false)]
    [InlineData("\\n", false)]
    [InlineData("\\r", false)]
    [InlineData("\\v", false)]
    [InlineData("\\f", false)]
    [InlineData("\\\\", false)]
    [InlineData("\\a", true)]
    [InlineData("\\b", true)]
    public void PlannerNeverReassignsEscapedSourceToTheUnescapedCoveredPath(string escape, bool distinctPathAccepted)
    {
        var policy = new EvidencePolicy("path-contract", "1", "all",
            [new EvidenceProfile("all", EvidenceProfileScope.Targeted, [],
                [new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["behavior"], [], 60)],
                [new EvidenceObligation("coverage", "behavior", "Path policy", ["coverage"], "behavior")])], []);
        var patch = Encoding.UTF8.GetBytes("--- /dev/null\n+++ \"b/src/Widget.cs" + escape + "\"\n");
        var planner = new EvidencePlanner();
        if (distinctPathAccepted)
        {
            var plan = planner.ResolvePatch(policy, patch);
            Assert.NotEqual("src/Widget.cs", Assert.Single(plan.ChangedPaths).Path);
            Assert.Equal("all", plan.Profile.Id);
        }
        else
        {
            var error = Assert.Throws<EvidencePlanningException>(() => planner.ResolvePatch(policy, patch));
            Assert.Equal("ASEVD118", error.Code);
        }
    }

    private static void AssertPlannerPath(string field, string expected)
    {
        var path = Assert.Single(EvidenceUnifiedDiffReader.Read("--- /dev/null\n+++ " + field + "\n"));
        Assert.Equal(expected, path.Path);
        Assert.Equal("added", path.Kind);
    }

    private static void AssertPlannerRejects(string field)
    {
        var error = Assert.Throws<EvidencePlanningException>(() =>
            EvidenceUnifiedDiffReader.Read("--- /dev/null\n+++ " + field + "\n"));
        Assert.Equal("ASEVD133", error.Code);
    }
}
