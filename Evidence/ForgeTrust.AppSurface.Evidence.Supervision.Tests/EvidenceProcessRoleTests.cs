using ForgeTrust.AppSurface.Evidence.Supervision;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class EvidenceProcessRoleTests
{
    [Theory]
    [InlineData("worker", "--control", "/run/proof/control.sock", 0)]
    [InlineData("supervise", "--request", "/run/proof/request.json", 1)]
    public void ExactRoleAcceptsOnlyItsPath(string role, string option, string path, int expected)
    {
        var selected = EvidenceProcessRoleParser.Parse(["evidence", role, option, path]);
        Assert.Equal(expected, (int)selected.Role);
        Assert.Equal(path, selected.Path);
        Assert.False(selected.Help);
        Assert.Equal(selected, EvidenceProcessRoleParser.Parse(["evidence", role, option + "=" + path]));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("supervise")]
    public void HelpIsDataOnlyAndHasNoPath(string role)
    {
        var selected = EvidenceProcessRoleParser.Parse(["evidence", role, "--help"]);
        Assert.True(selected.Help);
        Assert.Empty(selected.Path);
        Assert.Equal(selected, EvidenceProcessRoleParser.Parse(["evidence", role, "-h"]));
    }

    [Theory]
    [InlineData("evidence", "worker", true)]
    [InlineData("Evidence", "Worker", true)]
    [InlineData("evidence", "supervise", true)]
    [InlineData("evidence", "Supervise", true)]
    [InlineData("evidence", "explain", false)]
    [InlineData("coverage", "run", false)]
    public void ReservationAlsoCatchesAliases(string first, string second, bool expected) =>
        Assert.Equal(expected, EvidenceProcessRoleParser.IsReserved([first, second]));

    [Theory]
    [InlineData("worker", "--request", "/run/a")]
    [InlineData("supervise", "--control", "/run/a")]
    [InlineData("Worker", "--control", "/run/a")]
    [InlineData("worker", "--Control", "/run/a")]
    [InlineData("worker", "--control", "relative/canary")]
    [InlineData("worker", "--control", "/run/../canary")]
    [InlineData("worker", "--control", "/run/./canary")]
    [InlineData("worker", "--control", "/run//canary")]
    [InlineData("worker", "--control", "/run/canary/")]
    [InlineData("worker", "--control", "/run/canary\n")]
    [InlineData("worker", "--control", "")]
    public void InvalidGrammarNeverEchoesSuppliedValues(string role, string option, string path)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(["evidence", role, option, path]));
        Assert.StartsWith("ASEVD402:", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void MissingRepeatedAndExtraArgumentsRejectInsteadOfFallingThrough()
    {
        foreach (var arguments in new string[][]
        {
            [], ["evidence"], ["evidence", "worker"], ["evidence", "worker", "--control"],
            ["evidence", "worker", "--control", "/run/a", "--control", "/run/b"],
            ["evidence", "worker", "--control", "/run/a", "--help"],
            ["Evidence", "worker", "--control", "/run/a"],
        })
        {
            var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(arguments));
            Assert.StartsWith("ASEVD402:", error.Message, StringComparison.Ordinal);
        }
        Assert.False(EvidenceProcessRoleParser.IsReserved([]));
        Assert.False(EvidenceProcessRoleParser.IsReserved(["evidence"]));
        Assert.Throws<ArgumentNullException>(() => EvidenceProcessRoleParser.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => EvidenceProcessRoleParser.IsReserved(null!));
    }

    [Theory]
    [InlineData("worker", "--control", 100)]
    [InlineData("supervise", "--request", 4096)]
    public void PathLimitCountsUtf8Bytes(string role, string option, int maximum)
    {
        var prefix = "/run/";
        var exact = prefix + new string('x', maximum - prefix.Length);
        Assert.Equal(exact, EvidenceProcessRoleParser.Parse(["evidence", role, option, exact]).Path);
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(["evidence", role, option, exact + "x"]));
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(["evidence", role, option,
            prefix + new string('é', (maximum - prefix.Length) / 2 + 1)]));
    }
}
