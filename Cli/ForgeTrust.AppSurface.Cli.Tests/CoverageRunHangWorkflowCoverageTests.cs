using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunHangWorkflowCoverageTests
{
    [Theory]
    [InlineData("NotStarted", "not-started")]
    [InlineData("Disabled", "disabled")]
    [InlineData("SkippedShortBudget", "skipped-short-budget")]
    public void HangPlan_StatusName_UsesStableValue(string status, string expected)
    {
        var plan = new CoverageRunHangPlan(CoverageRunHangSource.None, Enum.Parse<CoverageRunHangPlanStatus>(status), null);

        Assert.Equal(expected, plan.StatusName);
    }

    [Fact]
    public void HangPlan_StatusName_RejectsUnknownStatus()
    {
        var plan = new CoverageRunHangPlan(CoverageRunHangSource.None, (CoverageRunHangPlanStatus)int.MaxValue, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = plan.StatusName);
    }

    [Fact]
    public void AppendHangArguments_PlacesOwnedOptionsBeforeVstestSeparator()
    {
        var arguments = new List<string> { "existing" };

        CoverageRunWorkflow.AppendHangArguments(
            arguments,
            ["--logger:console;verbosity=quiet", "--", "test-host-argument"],
            new CoverageRunHangPlan(CoverageRunHangSource.Automatic, CoverageRunHangPlanStatus.NotStarted, TimeSpan.FromSeconds(120)));

        Assert.Equal(
            ["existing", "--logger:console;verbosity=quiet", "--blame-hang", "--blame-hang-timeout", "120s", "--blame-hang-dump-type", "none", "--", "test-host-argument"],
            arguments);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("None")]
    [InlineData("Automatic")]
    public void AppendHangArguments_WithoutActiveAutomaticTimeout_PreservesCallerArguments(
        string source)
    {
        var arguments = new List<string>();

        CoverageRunWorkflow.AppendHangArguments(
            arguments,
            ["--", "host-argument"],
            new CoverageRunHangPlan(Enum.Parse<CoverageRunHangSource>(source), CoverageRunHangPlanStatus.Disabled, null));

        Assert.Equal(["--", "host-argument"], arguments);
    }

    [Fact]
    public void AppendHangArguments_WithoutSeparator_AppendsOwnedOptionsAfterCallerArguments()
    {
        var arguments = new List<string>();

        CoverageRunWorkflow.AppendHangArguments(
            arguments,
            ["--logger:console;verbosity=quiet"],
            new CoverageRunHangPlan(CoverageRunHangSource.Automatic, CoverageRunHangPlanStatus.NotStarted, TimeSpan.FromSeconds(90)));

        Assert.Equal(
            ["--logger:console;verbosity=quiet", "--blame-hang", "--blame-hang-timeout", "90s", "--blame-hang-dump-type", "none"],
            arguments);
    }

    [Theory]
    [InlineData("malformed", "The sequence could not be safely interpreted.", "Inspect the scoped artifact locally and retain the primary failure.")]
    [InlineData("future-status", "No safe sequence observation was available.", "Inspect the project log.")]
    public void HangInspectionAdvice_ReturnsStatusSpecificOrFallbackGuidance(string status, string expectedCause, string expectedNext)
    {
        var advice = CoverageRunWorkflow.HangInspectionAdvice(status);

        Assert.Equal(expectedCause, advice.Cause);
        Assert.Equal(expectedNext, advice.Next);
    }
}
