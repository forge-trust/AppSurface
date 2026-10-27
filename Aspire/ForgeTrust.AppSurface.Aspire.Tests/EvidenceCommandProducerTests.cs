using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceCommandProducerTests
{
    [Fact]
    public void Constructor_RejectsMissingDependenciesAndId()
    {
        var request = Request("true", "[Console]::Write('ok')");
        var runner = new EvidenceCommandRunner();
        var evaluator = static (EvidenceCommandResult _, EvidenceProducerContext _, CancellationToken _) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["assertion@1"]);

        Assert.Throws<ArgumentException>(() => new EvidenceCommandProducer(" ", runner, request, evaluator));
        Assert.Throws<ArgumentNullException>(() => new EvidenceCommandProducer("id", null!, request, evaluator));
        Assert.Throws<ArgumentNullException>(() => new EvidenceCommandProducer("id", runner, null!, evaluator));
        Assert.Throws<ArgumentNullException>(() => new EvidenceCommandProducer("id", runner, request, null!));
    }

    [Fact]
    public async Task ProduceAsync_RejectsNullAndMismatchedContexts()
    {
        var producer = Producer(Request("true", "[Console]::Write('ok')"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => producer.ProduceAsync(null!, CancellationToken.None).AsTask());

        var declaration = Declaration("other", "assertion@1");
        var result = await producer.ProduceAsync(Context(declaration), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task ProduceAsync_MapsNonzeroExitAndTruncatedOutputToInvalidResults()
    {
        var failed = await Producer(Request("exit 7", "[Environment]::Exit(7)"))
            .ProduceAsync(Context(Declaration("native", "assertion@1")), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Failed, failed.Outcome);
        Assert.Empty(failed.SatisfiedAssertionIds);

        var truncatedRequest = Request("printf 123456", "[Console]::Write('123456')") with { MaximumOutputBytes = 2 };
        var truncated = await Producer(truncatedRequest)
            .ProduceAsync(Context(Declaration("native", "assertion@1")), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Invalid, truncated.Outcome);
        Assert.Contains("truncated", truncated.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProduceAsync_RejectsEmptyNullAndUndeclaredAssertions()
    {
        var context = Context(Declaration("native", "declared@1"));
        foreach (var assertions in new IReadOnlyList<string>?[]
        {
            [],
            null,
            ["undeclared@1"],
        })
        {
            var producer = new EvidenceCommandProducer("native", new EvidenceCommandRunner(), Request("true", ""),
                (_, _, _) => ValueTask.FromResult(assertions!));
            var result = await producer.ProduceAsync(context, CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
        }
    }

    [Fact]
    public async Task ProduceAsync_MapsTimeoutAndBothBudgetFailures()
    {
        var timeoutRequest = Request("sleep 30", "Start-Sleep -Seconds 30") with { Timeout = TimeSpan.FromMilliseconds(100) };
        var timedOut = await Producer(timeoutRequest)
            .ProduceAsync(Context(Declaration("native", "assertion@1")), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.TimedOut, timedOut.Outcome);

        foreach (var exception in new Exception[]
        {
            new EvidenceWorkspaceBudgetException("budget", 2, 1, 100, 0),
            new InvalidOperationException("budget check failed"),
        })
        {
            var request = Request("true", "[Console]::Write('unused')") with
            {
                CheckBudget = _ => ValueTask.FromException(exception),
            };
            var unavailable = await Producer(request)
                .ProduceAsync(Context(Declaration("native", "assertion@1")), CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Unavailable, unavailable.Outcome);
        }
    }

    private static EvidenceCommandProducer Producer(EvidenceCommandRequest request) =>
        new("native", new EvidenceCommandRunner(), request,
            static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["assertion@1"]));

    private static EvidenceCommandRequest Request(string command, string windowsCommand) =>
        OperatingSystem.IsWindows()
            ? new EvidenceCommandRequest("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", windowsCommand], Environment.CurrentDirectory, TimeSpan.FromSeconds(10))
            : new EvidenceCommandRequest("sh", ["-c", command], Environment.CurrentDirectory, TimeSpan.FromSeconds(10));

    private static EvidenceProducerDeclaration Declaration(string id, string assertion) =>
        new(id, "command", "1.0", [], [assertion], [], 10);

    private static EvidenceProducerContext Context(EvidenceProducerDeclaration declaration) =>
        new(CreatePlan(declaration), declaration, TimeProvider.System);

    private static EvidencePlan CreatePlan(EvidenceProducerDeclaration producer) =>
        new("1", "test", "policy", "diff", new EvidenceProfile("test", EvidenceProfileScope.Targeted, [], [producer], []), [], [], "plan");
}
