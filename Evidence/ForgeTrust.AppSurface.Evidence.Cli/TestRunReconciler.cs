using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// The single reason recorded for one declared test project of a preview <c>test</c> producer.
/// </summary>
/// <remarks>
/// Declaration order is precedence order: a project gets the first reason that applies, which is also the order of
/// the producer's steps (file checks, property evaluation, restore, build, listing, run, reconciliation).
/// <see cref="DeadlineExceeded"/> and <see cref="NotRun"/> sit outside that order because the producer deadline
/// assigns them. Every value must appear in <see cref="TestRunReasons.Describe"/> and in the Evidence CLI README
/// reason table.
/// </remarks>
internal enum TestRunReason
{
    /// <summary>The declared project file does not exist or resolves outside the repository root. A policy error.</summary>
    ProjectNotFound,

    /// <summary>The <c>dotnet</c> host could not start or reported no compatible SDK.</summary>
    ToolchainUnavailable,

    /// <summary>The project evaluates to more than one target framework, which this preview does not run.</summary>
    MultiTargetUnsupported,

    /// <summary>The project evaluates a run-settings file, which can select tests without the policy naming it.</summary>
    RunSettingsUnsupported,

    /// <summary>The project or its nearest <c>global.json</c> selects a runner other than VSTest.</summary>
    UnsupportedRunner,

    /// <summary><c>dotnet restore</c> failed, so the project could not be observed.</summary>
    RestoreFailed,

    /// <summary><c>dotnet build</c> failed.</summary>
    BuildFailed,

    /// <summary>The test listing exited non-zero, had an unrecognized shape, or exceeded the capture bound.</summary>
    DiscoveryFailed,

    /// <summary>The test host crashed, or the run exited non-zero without reporting a failed test.</summary>
    RunAborted,

    /// <summary>The results file is missing, stale, malformed, inconsistent with its own counters, or too large.</summary>
    ResultsMissingOrStale,

    /// <summary>The listing selected no test.</summary>
    EmptyDiscovery,

    /// <summary>The listing selected fewer tests than the declared minimum.</summary>
    BelowMinimum,

    /// <summary>Two discovered tests or two results share one identity, so results cannot be matched without guessing.</summary>
    AmbiguousIdentity,

    /// <summary>A discovered test produced no result.</summary>
    MissingResult,

    /// <summary>A result has no discovered test.</summary>
    UnexpectedResult,

    /// <summary>Every discovered test has one result and at least one failed.</summary>
    TestsFailed,

    /// <summary>Every discovered test has one result, none failed, and at least one was skipped.</summary>
    TestsSkipped,

    /// <summary>Every discovered test ran exactly once and passed.</summary>
    Complete,

    /// <summary>The project was in flight when the producer's internal deadline fired.</summary>
    DeadlineExceeded,

    /// <summary>The project was not reached before the producer's internal deadline fired.</summary>
    NotRun,
}

/// <summary>
/// What a person can do about an incomplete test project.
/// </summary>
internal enum TestRunRecoveryClass
{
    /// <summary>Nothing to do; the project is complete.</summary>
    None,

    /// <summary>Rerunning without changes can succeed, for example after a feed outage or a host crash.</summary>
    RerunUnchanged,

    /// <summary>Something in the repository, policy, or environment must change before a rerun can succeed.</summary>
    FixAndRerun,

    /// <summary>The project has a shape this preview cannot close; someone must decide how to proceed.</summary>
    NeedsDecision,
}

/// <summary>
/// The producer outcome and recovery class one <see cref="TestRunReason"/> maps to.
/// </summary>
/// <param name="Outcome">Producer outcome contributed by a project with this reason.</param>
/// <param name="Recovery">What a person can do about it.</param>
internal readonly record struct TestRunReasonDescription(EvidenceProducerOutcome Outcome, TestRunRecoveryClass Recovery);

/// <summary>
/// The one mapping from <see cref="TestRunReason"/> to producer outcome and recovery class.
/// </summary>
/// <remarks>
/// <c>Unavailable</c> means the producer could not observe the project; <c>Failed</c> means it observed the project
/// and the discovery-complete assertion does not hold. The producer never returns <c>Invalid</c>.
/// </remarks>
internal static class TestRunReasons
{
    /// <summary>Maps a reason to its outcome and recovery class.</summary>
    /// <param name="reason">A defined reason value.</param>
    /// <returns>The outcome and recovery class.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined reason.</exception>
    public static TestRunReasonDescription Describe(TestRunReason reason) => reason switch
    {
        TestRunReason.ProjectNotFound => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.ToolchainUnavailable => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.MultiTargetUnsupported => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision),
        TestRunReason.RunSettingsUnsupported => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision),
        TestRunReason.UnsupportedRunner => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision),
        TestRunReason.RestoreFailed => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.RerunUnchanged),
        TestRunReason.BuildFailed => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.DiscoveryFailed => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.NeedsDecision),
        TestRunReason.RunAborted => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.RerunUnchanged),
        TestRunReason.ResultsMissingOrStale => new(EvidenceProducerOutcome.Unavailable, TestRunRecoveryClass.RerunUnchanged),
        TestRunReason.EmptyDiscovery => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.BelowMinimum => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.AmbiguousIdentity => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.NeedsDecision),
        TestRunReason.MissingResult => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.UnexpectedResult => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.TestsFailed => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.TestsSkipped => new(EvidenceProducerOutcome.Failed, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.Complete => new(EvidenceProducerOutcome.Passed, TestRunRecoveryClass.None),
        TestRunReason.DeadlineExceeded => new(EvidenceProducerOutcome.TimedOut, TestRunRecoveryClass.FixAndRerun),
        TestRunReason.NotRun => new(EvidenceProducerOutcome.TimedOut, TestRunRecoveryClass.FixAndRerun),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown test run reason."),
    };

    /// <summary>
    /// Combines per-project reasons into one producer outcome: the most severe of
    /// <c>TimedOut</c>, <c>Unavailable</c>, <c>Failed</c>, <c>Passed</c>, in that order.
    /// </summary>
    /// <param name="reasons">One reason per declared project; must not be empty.</param>
    /// <returns>The producer outcome. Only <c>Passed</c> may close the assertion.</returns>
    /// <exception cref="ArgumentException">No reason was supplied.</exception>
    public static EvidenceProducerOutcome Aggregate(IEnumerable<TestRunReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var worst = -1;
        foreach (var reason in reasons)
        {
            worst = Math.Max(worst, Severity(Describe(reason).Outcome));
        }

        return worst switch
        {
            3 => EvidenceProducerOutcome.TimedOut,
            2 => EvidenceProducerOutcome.Unavailable,
            1 => EvidenceProducerOutcome.Failed,
            0 => EvidenceProducerOutcome.Passed,
            _ => throw new ArgumentException("At least one project reason is required.", nameof(reasons)),
        };
    }

    private static int Severity(EvidenceProducerOutcome outcome) => outcome switch
    {
        EvidenceProducerOutcome.TimedOut => 3,
        EvidenceProducerOutcome.Unavailable => 2,
        EvidenceProducerOutcome.Failed => 1,
        _ => 0,
    };
}

/// <summary>Terminal outcome of one test result as the reconciler sees it.</summary>
internal enum TestCaseOutcome
{
    /// <summary>The test ran and passed.</summary>
    Passed,

    /// <summary>The test ran and failed, or reported an outcome the source does not recognize.</summary>
    Failed,

    /// <summary>The test was selected but not executed.</summary>
    Skipped,
}

/// <summary>One terminal result reported by a test run.</summary>
/// <param name="Identity">Case identity, compared ordinally and case-sensitively.</param>
/// <param name="Outcome">Terminal outcome.</param>
internal readonly record struct TestCaseResult(string Identity, TestCaseOutcome Outcome);

/// <summary>Per-project counts recorded in the receipt. They never include test names.</summary>
/// <param name="Discovered">Number of discovered identities, with multiplicity.</param>
/// <param name="Executed">Results that passed or failed.</param>
/// <param name="Passed">Results that passed.</param>
/// <param name="Failed">Results that failed.</param>
/// <param name="Skipped">Results that were not executed.</param>
internal readonly record struct TestRunCounts(int Discovered, int Executed, int Passed, int Failed, int Skipped)
{
    /// <summary>Counts for a project whose steps never produced an inventory.</summary>
    public static TestRunCounts Zero => default;
}

/// <summary>The reconciler's verdict for one project.</summary>
/// <param name="Reason">The first reconciliation reason that applies, or <see cref="TestRunReason.Complete"/>.</param>
/// <param name="Counts">Counts derived from the same inputs.</param>
internal readonly record struct TestRunReconciliation(TestRunReason Reason, TestRunCounts Counts);

/// <summary>
/// Decides whether every discovered test of one project ran exactly once and passed.
/// </summary>
/// <remarks>
/// <para>
/// A pure function with no process, file, or clock access, so every reason is testable without a test host.
/// It exists because a results file alone cannot report a selected test that produced no result; completeness
/// needs an independent inventory reconciled by identity and multiplicity.
/// </para>
/// <para>
/// Precedence, first match wins:
/// </para>
/// <code>
/// inventory empty ............................ EmptyDiscovery
/// inventory count &lt; minimumTests ............. BelowMinimum
/// duplicate identity in inventory or results . AmbiguousIdentity
/// inventory identity without a result ........ MissingResult
/// result without an inventory identity ....... UnexpectedResult
/// any result failed .......................... TestsFailed
/// any result skipped ......................... TestsSkipped
/// otherwise .................................. Complete
/// </code>
/// <para>
/// Pitfall: identity is whatever the source supplies. With the v1 display-name source, two different cases that swap
/// one name between the listing and the run are not detected. <see cref="TestRunReason.AmbiguousIdentity"/> is the
/// fail-closed rule: the reconciler never matches duplicates by count.
/// </para>
/// </remarks>
internal static class TestRunReconciler
{
    /// <summary>Reconciles one project's discovered inventory with its reported results.</summary>
    /// <param name="inventory">Discovered identities, with multiplicity.</param>
    /// <param name="results">One terminal outcome per reported result, with multiplicity.</param>
    /// <param name="minimumTests">Minimum discovered count, 1 or greater.</param>
    /// <returns>The reason and the counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumTests"/> is below 1.</exception>
    public static TestRunReconciliation Reconcile(
        IReadOnlyList<string> inventory,
        IReadOnlyList<TestCaseResult> results,
        int minimumTests)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumTests, 1);

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var resultIdentities = new HashSet<string>(StringComparer.Ordinal);
        var duplicateResult = false;
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case TestCaseOutcome.Passed:
                    passed++;
                    break;
                case TestCaseOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }

            duplicateResult |= !resultIdentities.Add(result.Identity);
        }

        var counts = new TestRunCounts(inventory.Count, passed + failed, passed, failed, skipped);
        return new TestRunReconciliation(Decide(), counts);

        TestRunReason Decide()
        {
            if (inventory.Count == 0)
            {
                return TestRunReason.EmptyDiscovery;
            }

            if (inventory.Count < minimumTests)
            {
                return TestRunReason.BelowMinimum;
            }

            var inventoryIdentities = new HashSet<string>(inventory, StringComparer.Ordinal);
            if (duplicateResult || inventoryIdentities.Count != inventory.Count)
            {
                return TestRunReason.AmbiguousIdentity;
            }

            if (!inventoryIdentities.IsSubsetOf(resultIdentities))
            {
                return TestRunReason.MissingResult;
            }

            if (resultIdentities.Count != inventoryIdentities.Count)
            {
                return TestRunReason.UnexpectedResult;
            }

            if (failed > 0)
            {
                return TestRunReason.TestsFailed;
            }

            return skipped > 0 ? TestRunReason.TestsSkipped : TestRunReason.Complete;
        }
    }
}
