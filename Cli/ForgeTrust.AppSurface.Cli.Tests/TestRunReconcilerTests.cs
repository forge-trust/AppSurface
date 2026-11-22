using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class TestRunReconcilerTests
{
    private static readonly TestCaseResult PassA = new("A", TestCaseOutcome.Passed);
    private static readonly TestCaseResult PassB = new("B", TestCaseOutcome.Passed);
    private static readonly TestCaseResult FailB = new("B", TestCaseOutcome.Failed);
    private static readonly TestCaseResult SkipB = new("B", TestCaseOutcome.Skipped);

    [Fact]
    public void Complete_ShouldRequireEveryDiscoveredTestToRunOnceAndPass()
    {
        var result = TestRunReconciler.Reconcile(["A", "B"], [PassB, PassA], 2);

        Assert.Equal(TestRunReason.Complete, result.Reason);
        Assert.Equal(new TestRunCounts(2, 2, 2, 0, 0), result.Counts);
    }

    [Fact]
    public void EmptyDiscovery_ShouldReportZeroCounts()
    {
        var result = TestRunReconciler.Reconcile([], [], 1);

        Assert.Equal(TestRunReason.EmptyDiscovery, result.Reason);
        Assert.Equal(TestRunCounts.Zero, result.Counts);
    }

    [Fact]
    public void BelowMinimum_ShouldApplyWhenFewerTestsAreDiscoveredThanDeclared()
    {
        Assert.Equal(TestRunReason.BelowMinimum, TestRunReconciler.Reconcile(["A"], [PassA], 2).Reason);
        Assert.Equal(TestRunReason.Complete, TestRunReconciler.Reconcile(["A"], [PassA], 1).Reason);
    }

    [Fact]
    public void AmbiguousIdentity_ShouldApplyToDuplicateInventoryNames()
    {
        var result = TestRunReconciler.Reconcile(["A", "A"], [PassA, PassA], 1);

        Assert.Equal(TestRunReason.AmbiguousIdentity, result.Reason);
        Assert.Equal(new TestRunCounts(2, 2, 2, 0, 0), result.Counts);
    }

    [Fact]
    public void AmbiguousIdentity_ShouldApplyWhenOneTestIsReportedTwice() =>
        Assert.Equal(TestRunReason.AmbiguousIdentity, TestRunReconciler.Reconcile(["A", "B"], [PassA, PassA], 1).Reason);

    [Fact]
    public void MissingResult_ShouldApplyWhenTheExecutedSetIsSmallerThanTheDiscoveredSet()
    {
        var result = TestRunReconciler.Reconcile(["A", "B"], [PassA], 1);

        Assert.Equal(TestRunReason.MissingResult, result.Reason);
        Assert.Equal(new TestRunCounts(2, 1, 1, 0, 0), result.Counts);
    }

    [Fact]
    public void MissingResult_ShouldApplyWhenAResultNameDiffersFromItsListedName() =>
        Assert.Equal(
            TestRunReason.MissingResult,
            TestRunReconciler.Reconcile(["Rows"], [new TestCaseResult("Rows(o: Object { })", TestCaseOutcome.Passed)], 1).Reason);

    [Fact]
    public void UnexpectedResult_ShouldApplyWhenAResultWasNeverDiscovered() =>
        Assert.Equal(TestRunReason.UnexpectedResult, TestRunReconciler.Reconcile(["A"], [PassA, PassB], 1).Reason);

    [Fact]
    public void TestsFailed_ShouldCountFailures()
    {
        var result = TestRunReconciler.Reconcile(["A", "B"], [PassA, FailB], 1);

        Assert.Equal(TestRunReason.TestsFailed, result.Reason);
        Assert.Equal(new TestRunCounts(2, 2, 1, 1, 0), result.Counts);
    }

    [Fact]
    public void TestsSkipped_ShouldLeaveSkippedTestsOutOfExecuted()
    {
        var result = TestRunReconciler.Reconcile(["A", "B"], [PassA, SkipB], 1);

        Assert.Equal(TestRunReason.TestsSkipped, result.Reason);
        Assert.Equal(new TestRunCounts(2, 1, 1, 0, 1), result.Counts);
    }

    [Fact]
    public void UndefinedOutcome_ShouldCountAsFailed() =>
        Assert.Equal(
            new TestRunCounts(1, 1, 0, 1, 0),
            TestRunReconciler.Reconcile(["A"], [new TestCaseResult("A", (TestCaseOutcome)99)], 1).Counts);

    [Fact]
    public void Identity_ShouldBeOrdinalAndCaseSensitive() =>
        Assert.Equal(
            TestRunReason.MissingResult,
            TestRunReconciler.Reconcile(["Alpha"], [new TestCaseResult("alpha", TestCaseOutcome.Passed)], 1).Reason);

    /// <summary>Each row triggers two adjacent reasons at once and expects the earlier one.</summary>
    [Theory]
    [MemberData(nameof(PrecedencePairs))]
    public void Precedence_ShouldReturnTheEarlierReason(string[] inventory, string[] results, int minimum, int expected)
    {
        var parsed = results
            .Select(static text => new TestCaseResult(text[1..], text[0] switch { 'P' => TestCaseOutcome.Passed, 'F' => TestCaseOutcome.Failed, _ => TestCaseOutcome.Skipped }))
            .ToArray();

        Assert.Equal((TestRunReason)expected, TestRunReconciler.Reconcile(inventory, parsed, minimum).Reason);
    }

    public static TheoryData<string[], string[], int, int> PrecedencePairs() => new()
    {
        // Empty beats everything a result list could add.
        { [], ["PA", "PA", "FB"], 5, (int)TestRunReason.EmptyDiscovery },
        // Below minimum beats ambiguity, missing, unexpected, failed and skipped.
        { ["A", "A"], ["FA", "SB"], 3, (int)TestRunReason.BelowMinimum },
        // Ambiguity beats missing, unexpected, failed and skipped.
        { ["A", "A", "B"], ["FA", "SC"], 1, (int)TestRunReason.AmbiguousIdentity },
        { ["A", "B"], ["FA", "FA", "SC"], 1, (int)TestRunReason.AmbiguousIdentity },
        // Missing beats unexpected, failed and skipped.
        { ["A", "B"], ["FA", "SC"], 1, (int)TestRunReason.MissingResult },
        // Unexpected beats failed and skipped.
        { ["A"], ["FA", "SC"], 1, (int)TestRunReason.UnexpectedResult },
        // Failed beats skipped.
        { ["A", "B"], ["FA", "SB"], 1, (int)TestRunReason.TestsFailed },
        // Skipped beats complete.
        { ["A", "B"], ["PA", "SB"], 1, (int)TestRunReason.TestsSkipped },
    };

    [Fact]
    public void Reconcile_ShouldRejectInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => TestRunReconciler.Reconcile(null!, [], 1));
        Assert.Throws<ArgumentNullException>(() => TestRunReconciler.Reconcile([], null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestRunReconciler.Reconcile([], [], 0));
    }

    [Fact]
    public void Describe_ShouldMapEveryReasonToAnOutcomeAndRecoveryClass()
    {
        foreach (var reason in Enum.GetValues<TestRunReason>())
        {
            var description = TestRunReasons.Describe(reason);

            Assert.Contains(
                description.Outcome,
                new[] { EvidenceProducerOutcome.Passed, EvidenceProducerOutcome.Failed, EvidenceProducerOutcome.Unavailable, EvidenceProducerOutcome.TimedOut });
            Assert.True(Enum.IsDefined(description.Recovery));
            Assert.Equal(reason == TestRunReason.Complete, description.Outcome == EvidenceProducerOutcome.Passed);
            Assert.Equal(reason == TestRunReason.Complete, description.Recovery == TestRunRecoveryClass.None);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => TestRunReasons.Describe((TestRunReason)999));
    }

    [Theory]
    [InlineData(TestRunReason.ProjectNotFound, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.ToolchainUnavailable, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.MultiTargetUnsupported, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision)]
    [InlineData(TestRunReason.RunSettingsUnsupported, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision)]
    [InlineData(TestRunReason.UnsupportedRunner, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision)]
    [InlineData(TestRunReason.RestoreFailed, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.RerunUnchanged)]
    [InlineData(TestRunReason.BuildFailed, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.DiscoveryFailed, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision)]
    [InlineData(TestRunReason.RunAborted, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.RerunUnchanged)]
    [InlineData(TestRunReason.ResultsMissingOrStale, EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.RerunUnchanged)]
    [InlineData(TestRunReason.EmptyDiscovery, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.BelowMinimum, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.AmbiguousIdentity, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.NeedsDecision)]
    [InlineData(TestRunReason.MissingResult, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.UnexpectedResult, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.TestsFailed, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.TestsSkipped, EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.Complete, EvidenceProducerOutcome.Passed, TestRunRecoveryClass.None)]
    [InlineData(TestRunReason.DeadlineExceeded, EvidenceProducerOutcome.TimedOut, TestRunRecoveryClass.FixAndRerun)]
    [InlineData(TestRunReason.NotRun, EvidenceProducerOutcome.TimedOut, TestRunRecoveryClass.FixAndRerun)]
    internal void Describe_ShouldMatchTheSpecifiedTable(TestRunReason reason, EvidenceProducerOutcome outcome, TestRunRecoveryClass recovery) =>
        Assert.Equal(new TestRunReasonDescription(outcome, recovery), TestRunReasons.Describe(reason));

    [Fact]
    public void ReasonOrder_ShouldMatchTheSpecifiedPrecedence() =>
        Assert.Equal(
            [
                "ProjectNotFound", "ToolchainUnavailable", "MultiTargetUnsupported", "RunSettingsUnsupported", "UnsupportedRunner",
                "RestoreFailed", "BuildFailed", "DiscoveryFailed", "RunAborted", "ResultsMissingOrStale", "EmptyDiscovery",
                "BelowMinimum", "AmbiguousIdentity", "MissingResult", "UnexpectedResult", "TestsFailed", "TestsSkipped", "Complete",
                "DeadlineExceeded", "NotRun",
            ],
            Enum.GetNames<TestRunReason>());

    [Fact]
    public void Aggregate_ShouldReturnTheMostSevereProjectOutcome()
    {
        Assert.Equal(EvidenceProducerOutcome.Passed, TestRunReasons.Aggregate([TestRunReason.Complete, TestRunReason.Complete]));
        Assert.Equal(EvidenceProducerOutcome.Failed, TestRunReasons.Aggregate([TestRunReason.Complete, TestRunReason.TestsFailed]));
        Assert.Equal(EvidenceProducerOutcome.Unavailable, TestRunReasons.Aggregate([TestRunReason.TestsFailed, TestRunReason.ProjectNotFound, TestRunReason.Complete]));
        Assert.Equal(EvidenceProducerOutcome.TimedOut, TestRunReasons.Aggregate([TestRunReason.ProjectNotFound, TestRunReason.NotRun, TestRunReason.TestsFailed]));
        Assert.Throws<ArgumentException>(() => TestRunReasons.Aggregate([]));
        Assert.Throws<ArgumentNullException>(() => TestRunReasons.Aggregate(null!));
    }
}
