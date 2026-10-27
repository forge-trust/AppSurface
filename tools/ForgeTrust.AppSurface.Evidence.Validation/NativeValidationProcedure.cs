using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs ordinary validation procedures without creating Evidence admission or a claim.</summary>
internal static class NativeValidationProcedure
{
    internal const string Schema = "appsurface-native-validation-procedure-v1";
    private static readonly IReadOnlyDictionary<string, int> ProducerDeadlines = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["packed-storage"] = 600,
        ["installed-consumer"] = 1800,
        ["native-fan-in"] = 300,
    };

    /// <summary>Runs the declared component checks, joins them, disposes their owners, then verifies artifacts.</summary>
    /// <remarks>This local procedure report is not an Evidence manifest, admission, native acceptance, or gate input.
    /// A validated call owns every supplied producer, including ones not started due to cancellation.
    /// Invalid arguments leave producer ownership with the caller. Procedures execute in plan order;
    /// disposal runs in reverse producer construction order. Deadlines signal cooperative cancellation;
    /// the original task and cleanup must join before any report can be returned.</remarks>
    internal static async Task<NativeValidationProcedureReport> RunAsync(
        EvidencePlan plan,
        IReadOnlyList<IEvidenceProducer> producers,
        string freshOutput,
        CancellationToken cancellationToken) => await RunAsync(plan, producers, freshOutput, cancellationToken, TimeProvider.System).ConfigureAwait(false);

    internal static async Task<NativeValidationProcedureReport> RunAsync(
        EvidencePlan plan,
        IReadOnlyList<IEvidenceProducer> producers,
        string freshOutput,
        CancellationToken cancellationToken,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(producers);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(freshOutput);
        if (plan.Profile is null || plan.Profile.Producers is null || plan.Profile.Producers.Count == 0
            || plan.Profile.Resources is null || plan.Profile.Resources.Count != 0)
            throw new ArgumentException("Validation requires a nonempty producer-only plan.", nameof(plan));
        var declarations = plan.Profile.Producers.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var producerMap = producers.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        if (declarations.Count != producers.Count || declarations.Keys.Any(id => !producerMap.ContainsKey(id))
            || declarations.Any(pair => !ProducerDeadlines.TryGetValue(pair.Key, out var timeout) || pair.Value.TimeoutSeconds != timeout
                || pair.Value.AssertionIds is null || pair.Value.ArtifactSlots is null
                || pair.Value.ArtifactSlots.Any(static slot => slot is null)
                || pair.Value.ArtifactSlots.GroupBy(static slot => slot.LogicalName, StringComparer.Ordinal).Any(static group => group.Count() != 1)))
            throw new ArgumentException("Validation producers must match the plan declarations exactly.", nameof(producers));

        var output = Path.GetFullPath(freshOutput);
        if (Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Validation output must be fresh.", nameof(freshOutput));

        var reports = new Dictionary<string, EvidenceProducerResult>(StringComparer.Ordinal);
        var writers = new Dictionary<string, EvidenceArtifactWriter>(StringComparer.Ordinal);
        var acquired = producers.ToArray();
        var cleanupCompleted = true;
        var procedurePassed = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(output);
            foreach (var declaration in plan.Profile.Producers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var producer = producerMap[declaration.Id];
                var writer = new EvidenceArtifactWriter(declaration, Path.Join(output, "artifacts", declaration.Id));
                writers.Add(declaration.Id, writer);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(declaration.TimeoutSeconds), clock);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var started = clock.GetTimestamp();
                EvidenceProducerResult? result = null;
                string? failure = null;
                try
                {
                    result = await producer.ProduceAsync(
                        new EvidenceProducerContext(plan, declaration, clock, writer), deadline.Token)
                        .AsTask().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    failure = "Producer deadline expired.";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    failure = "Procedure failed.";
                }

                var elapsedDuration = clock.GetElapsedTime(started);
                var elapsed = Math.Max(0, (long)elapsedDuration.TotalMilliseconds);
                var artifacts = writer.WrittenArtifacts;
                var withinDeadline = !deadline.IsCancellationRequested && elapsedDuration < TimeSpan.FromSeconds(declaration.TimeoutSeconds);
                if (!withinDeadline && failure is null) failure = "Producer deadline expired.";
                var valid = withinDeadline && result is not null
                    && string.Equals(result.ProducerId, declaration.Id, StringComparison.Ordinal)
                    && result.Outcome == EvidenceProducerOutcome.Passed
                    && result.SatisfiedAssertionIds is not null
                    && result.SatisfiedAssertionIds.Distinct(StringComparer.Ordinal).Count() == result.SatisfiedAssertionIds.Count
                    && result.SatisfiedAssertionIds.All(assertion => declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal))
                    && ValidArtifacts(declaration, artifacts)
                    && (result.Artifacts is null || (ValidArtifacts(declaration, result.Artifacts)
                        && SameArtifacts(artifacts, result.Artifacts)))
                    && declaration.AssertionIds.All(result.SatisfiedAssertionIds.Contains);
                if (!valid && failure is null) failure = result is { Outcome: EvidenceProducerOutcome.Failed }
                    ? BoundDiagnostic(result.Diagnostic) : "Producer result or declared artifacts did not satisfy the procedure contract.";
                procedurePassed &= valid;
                reports[declaration.Id] = new EvidenceProducerResult(
                    declaration.Id,
                    valid ? EvidenceProducerOutcome.Passed : EvidenceProducerOutcome.Failed,
                    valid ? result!.SatisfiedAssertionIds!.Where(declaration.AssertionIds.Contains).Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray() : [],
                    failure,
                    artifacts.OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal).ToArray(),
                    elapsed);
            }
        }
        finally
        {
            foreach (var producer in acquired.Reverse())
            {
                try
                {
                    if (producer is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (producer is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    cleanupCompleted = false;
                    procedurePassed = false;
                    if (reports.TryGetValue(producer.Id, out var report))
                        reports[producer.Id] = report with { Outcome = EvidenceProducerOutcome.Failed, SatisfiedAssertionIds = [], Diagnostic = "Producer cleanup failed." };
                }
            }
        }

        foreach (var declaration in plan.Profile.Producers)
        {
            if (!reports.TryGetValue(declaration.Id, out var report)) continue;
            var writer = writers[declaration.Id];
            if (!await writer.VerifyWrittenArtifactsAsync(cancellationToken).ConfigureAwait(false))
            {
                procedurePassed = false;
                reports[declaration.Id] = report with { Outcome = EvidenceProducerOutcome.Failed, SatisfiedAssertionIds = [], Diagnostic = "Artifact verification after producer cleanup failed." };
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new NativeValidationProcedureReport(
            Schema,
            plan.PlanDigest,
            procedurePassed && cleanupCompleted,
            cleanupCompleted,
            reports.Values.OrderBy(static report => report.ProducerId, StringComparer.Ordinal).ToArray());
    }

    private static string BoundDiagnostic(string? diagnostic) => string.IsNullOrEmpty(diagnostic)
        ? "Producer procedure failed."
        : diagnostic.Length <= 1024 ? diagnostic : diagnostic[..1024];

    private static bool SameArtifacts(IReadOnlyList<EvidenceArtifactResult> left, IReadOnlyList<EvidenceArtifactResult> right) =>
        left.OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal));

    private static bool ValidArtifacts(EvidenceProducerDeclaration declaration, IReadOnlyList<EvidenceArtifactResult> artifacts) =>
        !artifacts.Any(static artifact => artifact is null || artifact.LogicalName is null || artifact.RelativePath is null || artifact.MediaType is null || artifact.Sha256 is null)
        && EvidenceArtifactValidation.AreValid(declaration, artifacts);
}

/// <summary>Local procedure result with explicit non-claim status.</summary>
internal sealed record NativeValidationProcedureReport(
    string Schema,
    string PlanDigest,
    bool ProcedurePassed,
    bool CleanupCompleted,
    IReadOnlyList<EvidenceProducerResult> ProducerResults)
{
    public bool ProtectedAdmission => false;
    public bool NativeAcceptance => false;
    public bool GateEligible => false;
}
