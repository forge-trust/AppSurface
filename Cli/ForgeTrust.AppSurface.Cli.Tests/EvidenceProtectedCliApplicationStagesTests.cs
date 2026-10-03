using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Serial budget controls only; no application registration, transport or native admission is issued.</summary>
public sealed class EvidenceProtectedCliApplicationStagesTests
{
    [Fact]
    public void ApplicationStagesReserveEveryResourceAndProducerBeforeStartup()
    {
        var stages = EvidenceProtectedCliExecution.CreateDeclaredStages(Profile(), 5, 7);
        var clock = new FrozenTimeProvider();
        // 2*5 admission + 7 start + 11+13 readiness + 17+19 producers + 23 collection + 29 cleanup.
        Assert.False(CreateBudget(clock, stages, 128, out _));
        Assert.True(CreateBudget(clock, stages, 129, out var budget));
        var expected = new[] { EvidenceRunStage.Admission, EvidenceRunStage.Admission, EvidenceRunStage.Start,
            EvidenceRunStage.Resource, EvidenceRunStage.Resource, EvidenceRunStage.Producer, EvidenceRunStage.Producer };
        foreach (var kind in expected)
        {
            Assert.True(budget!.TryBeginNextStage(default, out var stage));
            Assert.Equal(kind, stage!.Stage);
            Assert.False(budget.TryBeginNextStage(default, out _));
            Assert.True(budget.CompleteCurrentStage());
        }
        Assert.False(budget!.TryBeginNextStage(default, out _));
        Assert.True(budget.TryAbandonStagesAndBeginCleanup(stageClosed: true));
        Assert.True(budget.CompleteCleanup());
        Assert.True(budget.TryBeginCollection());
    }

    [Fact]
    public void ProducerOnlyScheduleKeepsItsExistingAllowanceAndContainsNoApplicationWork()
    {
        var stages = EvidenceProtectedCliExecution.CreateDeclaredStages(Profile() with { Resources = [] }, 5, null);
        Assert.Equal(new[] { EvidenceRunStage.Admission, EvidenceRunStage.Admission,
            EvidenceRunStage.Producer, EvidenceRunStage.Producer }, stages.Select(static item => item.Stage));
        Assert.False(CreateBudget(new FrozenTimeProvider(), stages, 97, out _));
        Assert.True(CreateBudget(new FrozenTimeProvider(), stages, 98, out _));
    }

    [Fact]
    public void ScheduleCopiesDeclarationValuesAndCannotBeMutatedAfterAdmission()
    {
        EvidenceResourceDeclaration[] resources = [new("http", "aspire_health", 11, [])];
        EvidenceProducerDeclaration[] producers = [Producer("coverage", 17)];
        var stages = EvidenceProtectedCliExecution.CreateDeclaredStages(Profile() with
        { Resources = resources, Producers = producers }, 5, 7);
        resources[0] = resources[0] with { DeadlineSeconds = 300 };
        producers[0] = producers[0] with { TimeoutSeconds = 1800 };
        Assert.Equal(TimeSpan.FromSeconds(11), stages[3].Duration);
        Assert.Equal(TimeSpan.FromSeconds(17), stages[4].Duration);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceRunStageDeadline>)stages).Clear());
    }

    [Theory]
    [InlineData(-1, 7, 11, 17)]
    [InlineData(0, 7, 11, 17)]
    [InlineData(31, 7, 11, 17)]
    [InlineData(5, -1, 11, 17)]
    [InlineData(5, 0, 11, 17)]
    [InlineData(5, 121, 11, 17)]
    [InlineData(5, 7, 0, 17)]
    [InlineData(5, 7, int.MaxValue, 17)]
    [InlineData(5, 7, 11, 0)]
    [InlineData(5, 7, 11, int.MaxValue)]
    public void InvalidOrUnaffordableDeadlineRejectsTheWholeScheduleBeforeAnyCallback(int admission, int start,
        int resource, int producer)
    {
        var profile = Profile() with { Resources = [new("http", "aspire_health", resource, [])],
            Producers = [Producer("coverage", producer)] };
        var stages = EvidenceProtectedCliExecution.CreateDeclaredStages(profile, admission, start);
        Assert.False(CreateBudget(new FrozenTimeProvider(), stages, 10000, out var budget));
        Assert.Null(budget);
    }

    [Fact]
    public void CancelledScheduleCannotBeginEvenWithAllReservesAvailable()
    {
        var stages = EvidenceProtectedCliExecution.CreateDeclaredStages(Profile(), 5, 7);
        Assert.True(CreateBudget(new FrozenTimeProvider(), stages, 129, out var budget));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(budget!.TryBeginNextStage(cancellation.Token, out var stage));
        Assert.Null(stage);
    }

    private static bool CreateBudget(TimeProvider clock, IReadOnlyList<EvidenceRunStageDeadline> stages,
        int seconds, out EvidenceRunTimeBudget? budget) => EvidenceRunTimeBudget.TryCreateFromAllowance(clock,
            TimeSpan.FromSeconds(seconds), stages, TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(5), out budget);

    private static EvidenceProfile Profile() => new("application", EvidenceProfileScope.Targeted,
        [new("http", "aspire_health", 11, []), new("worker", "completion", 13, [])],
        [Producer("coverage", 17), Producer("other", 19)], []);

    private static EvidenceProducerDeclaration Producer(string id, int seconds) =>
        new(id, "coverage", "1.0.0", [], [], [], seconds);

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }
}
