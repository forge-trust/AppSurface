using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunHangPolicyTests
{
    [Theory]
    [InlineData(89, null)]
    [InlineData(90, 60)]
    [InlineData(150, 100)]
    [InlineData(180, 120)]
    [InlineData(600, 540)]
    public void Plan_ReservesTimeForSequenceFlush(int budgetSeconds, int? expectedTimeoutSeconds)
    {
        var request = CreateRequest() with { NoProgressTimeout = TimeSpan.FromSeconds(budgetSeconds) };

        var plan = CoverageRunHangPolicy.Plan(request);

        Assert.Equal(expectedTimeoutSeconds is null ? CoverageRunHangSource.None : CoverageRunHangSource.Automatic, plan.Source);
        Assert.Equal(expectedTimeoutSeconds, plan.Timeout is null ? null : (int)plan.Timeout.Value.TotalSeconds);
        if (expectedTimeoutSeconds is null) Assert.Equal(CoverageRunHangPlanStatus.SkippedShortBudget, plan.Status);
    }

    [Fact]
    public void Plan_UsesRemainingMonotonicProducerBudgetAfterDiscovery()
    {
        var clock = new ManualClock();
        var deadline = new CoverageProducerDeadline(clock, clock.GetTimestamp(), TimeSpan.FromSeconds(180));
        clock.Advance(TimeSpan.FromSeconds(30));

        var plan = CoverageRunHangPolicy.Plan(CreateRequest() with { ProducerDeadline = deadline });

        Assert.Equal(TimeSpan.FromSeconds(100), plan.Timeout);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(CoverageRunHangSource.None, CoverageRunHangPolicy.Plan(CreateRequest() with { ProducerDeadline = deadline }).Source);
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Theory]
    [InlineData("--blame")]
    [InlineData("--blame-hang")]
    [InlineData("--blame-crash")]
    [InlineData("--blame-hang-timeout=120s")]
    [InlineData("--BLAME-HANG-DUMP-TYPE:none")]
    public void Plan_ManualBlameSuppressesAutomaticTuple(string token)
    {
        var request = CreateRequest() with { TestArguments = [token] };

        var plan = CoverageRunHangPolicy.Plan(request);

        Assert.Equal(CoverageRunHangSource.Manual, plan.Source);
        Assert.Null(plan.Timeout);
        Assert.False(CoverageRunHangPolicy.OwnsMsbuildResults(request));
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("Off")]
    public void Plan_OptOutDisablesAutomaticBlame(string mode)
    {
        var plan = CoverageRunHangPolicy.Plan(CreateRequest() with { WatchdogMode = Enum.Parse<CoverageRunWatchdogMode>(mode) });
        Assert.Equal(CoverageRunHangSource.None, plan.Source);
        Assert.Equal(CoverageRunHangPlanStatus.Disabled, plan.Status);
    }

    [Theory]
    [InlineData("--settings")]
    [InlineData("-s")]
    [InlineData("--settings=custom.runsettings")]
    [InlineData("--settings:custom.runsettings")]
    public void Plan_MsbuildSettingsClaimManualPolicy(string token)
    {
        var arguments = token is "--settings" or "-s" ? new[] { token, "my.runsettings" } : new[] { token };
        var request = CreateRequest() with { TestArguments = arguments };
        Assert.Equal(CoverageRunHangSource.Manual, CoverageRunHangPolicy.Plan(request).Source);
        Assert.False(CoverageRunHangPolicy.OwnsMsbuildResults(request));
    }

    [Fact]
    public void Plan_IgnoresTestHostArgumentsAfterVstestSeparator()
    {
        var request = CreateRequest() with
        {
            TestArguments = ["--", "--blame-hang", "--settings", "host-value"],
        };

        Assert.Equal(CoverageRunHangSource.Automatic, CoverageRunHangPolicy.Plan(request).Source);
        Assert.True(CoverageRunHangPolicy.OwnsMsbuildResults(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateHangArguments_ReportsIncompleteSplitResultsOption(bool beforeSeparator)
    {
        var arguments = beforeSeparator
            ? new[] { "--results-directory", "--" }
            : new[] { "--results-directory" };
        var request = CreateRequest() with { TestArguments = arguments };

        var exception = Assert.Throws<CoverageExecutionException>(() => CoverageRunDriverStrategy.ValidateHangArguments(request));

        Assert.Contains("incomplete owned option", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--results-directory=caller-results")]
    [InlineData("--results-directory:caller-results")]
    public void ValidateHangArguments_RejectsJoinedOwnedResultsOption(string argument)
    {
        var request = CreateRequest() with { TestArguments = [argument] };

        var exception = Assert.Throws<CoverageExecutionException>(() => CoverageRunDriverStrategy.ValidateHangArguments(request));

        Assert.Contains("cannot override AppSurface-owned hang results", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateHangArguments_StopsAtVstestSeparator()
    {
        var request = CreateRequest() with
        {
            TestArguments = ["--filter", "Category=Fast", "--", "--results-directory", "host-value"],
        };

        CoverageRunDriverStrategy.ValidateHangArguments(request);
    }

    [Fact]
    public void ValidateHangArguments_RejectsEmptySplitOwnedResultsValue()
    {
        var request = CreateRequest() with { TestArguments = ["--results-directory", ""] };

        var exception = Assert.Throws<CoverageExecutionException>(() => CoverageRunDriverStrategy.ValidateHangArguments(request));

        Assert.Contains("cannot override AppSurface-owned hang results", exception.Message, StringComparison.Ordinal);
    }

    private static CoverageRunRequest CreateRequest() => new(
        SolutionPath: null, TestProjects: [], ExcludeTestProjects: [], OutputDirectory: "output",
        Configuration: "Debug", Parallelism: 1, ScheduleMode: CoverageRunScheduleMode.InputOrder,
        ScheduleTimingsPath: null, PriorityTestProjects: [], NoRestore: false, Build: false,
        NoBuild: false, IncludeFilter: null, ExcludeFilter: "", DryRun: false,
        NoDiscoverExclusive: false, ExclusiveTestProjects: [], Loggers: [], TestArguments: [],
        TestResults: CoverageRunTestResultFormat.None, SlowTestDiagnostics: false, Clean: true,
        Verbosity: "minimal", HeartbeatInterval: TimeSpan.Zero,
        NoProgressTimeout: TimeSpan.FromMinutes(10), WatchdogMode: CoverageRunWatchdogMode.Fail,
        CoverageDriver: CoverageRunDriver.Msbuild, RequireNonSandbox: false);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
