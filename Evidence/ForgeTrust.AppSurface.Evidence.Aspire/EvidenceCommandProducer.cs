using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Adapts one bounded command to the typed EvidenceHost producer contract.</summary>
public sealed class EvidenceCommandProducer : IEvidenceProducer
{
    private readonly EvidenceCommandRunner _runner;
    private readonly EvidenceCommandRequest _request;
    private readonly Func<EvidenceCommandResult, EvidenceProducerContext, CancellationToken, ValueTask<IReadOnlyList<string>>> _evaluateAssertions;

    /// <summary>Creates a producer with an explicit command and domain-owned assertion evaluator.</summary>
    public EvidenceCommandProducer(
        string id,
        EvidenceCommandRunner runner,
        EvidenceCommandRequest request,
        Func<EvidenceCommandResult, EvidenceProducerContext, CancellationToken, ValueTask<IReadOnlyList<string>>> evaluateAssertions)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("A producer id is required.", nameof(id)) : id;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _evaluateAssertions = evaluateAssertions ?? throw new ArgumentNullException(nameof(evaluateAssertions));
    }

    /// <summary>Gets the stable producer identifier.</summary>
    public string Id { get; }

    /// <summary>Runs the command and evaluates assertions only after a zero exit code.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Producer.Id, Id, StringComparison.Ordinal))
        {
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Invalid, [], "The producer declaration did not match the registered producer.");
        }

        try
        {
            var result = await _runner.RunAsync(_request, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Failed, [], "The evidence command returned a nonzero exit code.", ElapsedMilliseconds: result.ElapsedMilliseconds);
            }

            if (result.OutputTruncated)
            {
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Invalid, [], "The evidence command output was truncated.", ElapsedMilliseconds: result.ElapsedMilliseconds);
            }

            var assertions = await _evaluateAssertions(result, context, cancellationToken).ConfigureAwait(false);
            if (assertions is null || assertions.Count == 0 || assertions.Any(assertion => !context.Producer.AssertionIds.Contains(assertion, StringComparer.Ordinal)))
            {
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Invalid, [], "The assertion evaluator returned an undeclared assertion.", ElapsedMilliseconds: result.ElapsedMilliseconds);
            }

            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, assertions, ElapsedMilliseconds: result.ElapsedMilliseconds);
        }
        catch (EvidenceCommandException exception) when (exception.Code == "timeout")
        {
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.TimedOut, [], "The evidence command exceeded its deadline.");
        }
        catch (EvidenceCommandException exception) when (exception.Code is "budget_exceeded" or "budget_check_failed")
        {
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Unavailable, [], "The evidence workspace budget was exhausted.");
        }
    }
}
