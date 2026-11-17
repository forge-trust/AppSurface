using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs the fixed repository package journeys without entering the EvidenceHost lifecycle.</summary>
/// <remarks>This type is internal to the validation executable and has no protected-gate or admission role.</remarks>
internal static class NativeValidationExecution
{
    /// <summary>Maximum wait for callback joins, each disposal and each final artifact verification.</summary>
    internal static readonly TimeSpan MaximumJoinAllowance = TimeSpan.FromSeconds(10);

    /// <summary>Executes every selected recipe and joins callbacks before disposal and final artifact verification.</summary>
    /// <param name="plan">Canonical selection and declarations for the fixed package journeys.</param>
    /// <param name="producers">Exactly one registered callback per selected declaration.</param>
    /// <param name="artifactsDirectory">Caller-owned root for declared artifact writers.</param>
    /// <param name="inputDigest">Lowercase SHA-256 identity of the package and recipe settings.</param>
    /// <param name="cancellationToken">Caller cancellation, sampled again when finalizing the receipt.</param>
    /// <param name="loweredJoinAllowance">Test-only lower limit for joins, disposals and artifact verification.</param>
    /// <returns>A finalized, plan-checked receipt with no EvidenceHost authority.</returns>
    /// <remarks>Only tests lower the allowance. Unjoined producer or disposal work prevents further disposal, hashing and receipt publication.</remarks>
    internal static async Task<NativeValidationReceipt> ExecuteAsync(
        EvidencePlan plan,
        IReadOnlyList<IEvidenceProducer> producers,
        string artifactsDirectory,
        string inputDigest,
        CancellationToken cancellationToken,
        TimeSpan? loweredJoinAllowance = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(producers);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        var joinAllowance = loweredJoinAllowance ?? MaximumJoinAllowance;
        if (joinAllowance <= TimeSpan.Zero || joinAllowance > MaximumJoinAllowance)
            throw new ArgumentOutOfRangeException(nameof(loweredJoinAllowance));
        if (plan.Profile.Resources.Count != 0 || plan.Profile.Producers.Count == 0 || producers.Count != plan.Profile.Producers.Count
            || plan.Profile.Producers.Any(static declaration => declaration.TimeoutSeconds <= 0))
        {
            throw new ArgumentException("Native package journeys must match the selected declarations.", nameof(producers));
        }

        var declarations = plan.Profile.Producers;
        if (declarations.GroupBy(static declaration => declaration.Id, StringComparer.Ordinal).Any(static group => group.Count() != 1)
            || producers.GroupBy(static producer => producer.Id, StringComparer.Ordinal).Any(static group => group.Count() != 1))
        {
            throw new ArgumentException("Native package journey identifiers must be unique.", nameof(producers));
        }

        var producerById = producers.ToDictionary(static producer => producer.Id, StringComparer.Ordinal);
        if (declarations.Any(declaration => !producerById.ContainsKey(declaration.Id)))
        {
            throw new ArgumentException("A selected native package journey is not registered.", nameof(producers));
        }

        var artifactRoot = Path.GetFullPath(artifactsDirectory);
        Directory.CreateDirectory(artifactRoot);
        var completed = new List<CompletedJourney>(declarations.Count);
        var cleanupCompleted = true;
        string? cleanupDiagnostic = null;
        var unjoined = false;
        var fatal = false;

        try
        {
            foreach (var declaration in declarations)
            {
                var producer = producerById[declaration.Id];
                var writer = new EvidenceArtifactWriter(declaration, Path.Join(artifactRoot, declaration.Id));
                var stopwatch = Stopwatch.StartNew();
                EvidenceProducerResult result;
                if (cancellationToken.IsCancellationRequested)
                {
                    result = ValidateResult(declaration, writer,
                        Failure(declaration.Id, EvidenceProducerOutcome.Cancelled, "The native package journey was cancelled."), 0);
                    completed.Add(new CompletedJourney(declaration, writer, result));
                    continue;
                }

                var deadline = new CancellationTokenSource();
                var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(declaration.TimeoutSeconds));
                Task<EvidenceProducerResult>? execution = null;
                try
                {
                    var context = new EvidenceProducerContext(plan, declaration, TimeProvider.System, writer);
                    execution = Task.Run(async () => await producer.ProduceAsync(context, producerCancellation.Token).ConfigureAwait(false), CancellationToken.None);
                    result = await execution.WaitAsync(producerCancellation.Token).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result = Failure(declaration.Id, EvidenceProducerOutcome.Cancelled, "The native package journey was cancelled.");
                    }
                    else if (deadline.IsCancellationRequested || stopwatch.Elapsed >= TimeSpan.FromSeconds(declaration.TimeoutSeconds))
                    {
                        result = Failure(declaration.Id, EvidenceProducerOutcome.TimedOut, "The native package journey exceeded its declared deadline.");
                    }
                }
                catch (OperationCanceledException)
                {
                    if (execution is not null)
                    {
                        try { await JoinAsync(execution, joinAllowance).ConfigureAwait(false); }
                        catch (NativeValidationUnjoinedWorkException)
                        {
                            unjoined = true;
                            throw;
                        }
                        catch (Exception exception) when (IsNonFatal(exception))
                        {
                            // Observe the joined callback fault; cancellation/deadline below owns its outcome.
                            // Do not log exception messages, which can contain private command or path data.
                            _ = exception;
                        }
                    }
                    result = cancellationToken.IsCancellationRequested
                        ? Failure(declaration.Id, EvidenceProducerOutcome.Cancelled, "The native package journey was cancelled.")
                        : deadline.IsCancellationRequested
                            ? Failure(declaration.Id, EvidenceProducerOutcome.TimedOut, "The native package journey exceeded its declared deadline.")
                            : Failure(declaration.Id, EvidenceProducerOutcome.Failed, "The native package journey stopped before returning a result.");
                }
                catch (Exception exception) when (IsNonFatal(exception) && exception is not NativeValidationUnjoinedWorkException)
                {
                    result = Failure(declaration.Id, EvidenceProducerOutcome.Failed,
                        "The native package journey failed with " + exception.GetType().Name + ".");
                }
                finally
                {
                    if (execution is { IsCompleted: false })
                    {
                        // A callback that outlives the runner still owns the token sources it can use.
                        // Transfer their cleanup to actual completion, including a later fault/cancellation.
                        _ = execution.ContinueWith(task =>
                        {
                            _ = task.Exception;
                            producerCancellation.Dispose();
                            deadline.Dispose();
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    else
                    {
                        _ = execution?.Exception;
                        producerCancellation.Dispose();
                        deadline.Dispose();
                    }
                }

                stopwatch.Stop();
                result = ValidateResult(declaration, writer, result, stopwatch.ElapsedMilliseconds);
                completed.Add(new CompletedJourney(declaration, writer, result));
            }
        }
        catch (Exception exception) when (!IsNonFatal(exception))
        {
            fatal = true;
            throw;
        }
        finally
        {
            var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var producer in unjoined || fatal ? [] : producers.Reverse())
            {
                if (!disposed.Add(producer)) continue;
                try
                {
                    if (producer is IAsyncDisposable asyncDisposable)
                    {
                        var disposal = Task.Run(async () => await asyncDisposable.DisposeAsync().ConfigureAwait(false));
                        await JoinAsync(disposal, joinAllowance).ConfigureAwait(false);
                    }
                    else if (producer is IDisposable disposable)
                    {
                        var disposal = Task.Run(disposable.Dispose);
                        await JoinAsync(disposal, joinAllowance).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsNonFatal(exception) && exception is not NativeValidationUnjoinedWorkException)
                {
                    cleanupCompleted = false;
                    cleanupDiagnostic ??= "Native package journey cleanup failed with " + exception.GetType().Name + ".";
                }
            }
        }

        var journeys = new List<NativeValidationJourneyReceipt>(completed.Count);
        foreach (var journey in completed)
        {
            var artifacts = journey.Writer.WrittenArtifacts;
            var valid = NativeValidationReceiptCodec.ValidArtifactSet(
                journey.Declaration, artifacts, requireRequiredSlots: journey.Result.Outcome == EvidenceProducerOutcome.Passed);
            try
            {
                valid &= await VerifyArtifactsAsync(journey.Writer.VerifyWrittenArtifactsAsync, joinAllowance).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                valid = false;
            }

            var result = valid
                ? journey.Result
                : Failure(journey.Declaration.Id, EvidenceProducerOutcome.Invalid,
                    "A native package journey artifact failed final verification.", artifacts, journey.Result.ElapsedMilliseconds);
            journeys.Add(new NativeValidationJourneyReceipt(
                journey.Declaration.Id,
                result.Outcome.ToString().ToLowerInvariant(),
                journey.Declaration.AssertionIds.ToArray(),
                result.SatisfiedAssertionIds.ToArray(),
                journey.Declaration.ArtifactSlots.ToArray(),
                artifacts,
                BoundDiagnostic(result.Diagnostic),
                result.ElapsedMilliseconds));
        }

        var receipt = NativeValidationReceiptCodec.Create(
            plan,
            inputDigest,
            journeys,
            cleanupCompleted,
            cleanupDiagnostic,
            cancellationToken.IsCancellationRequested);
        if (!NativeValidationReceiptCodec.Verify(plan, receipt))
        {
            throw new InvalidDataException("Native package journey receipt failed its internal verification.");
        }

        return receipt;
    }

    /// <summary>Joins producer or disposal work under the runner's existing allowance.</summary>
    /// <param name="work">The actual work whose completion and fault must be observed.</param>
    /// <param name="allowance">The positive, capped allowance already validated by the runner.</param>
    /// <param name="wait">Optional test-only wait boundary for forcing deadline/completion ordering.</param>
    /// <returns>A task that succeeds only when work joins successfully within the allowance.</returns>
    /// <remarks>
    /// A wait timeout remains unsuccessful if work settles before inspection. Observe a settled task's
    /// real fault before classifying the timeout so fatal faults cannot become nonfatal diagnostics.
    /// Unsettled work still stops cleanup and receipt publication; any late fault is observed.
    /// </remarks>
    internal static async Task JoinAsync(Task work, TimeSpan allowance, Func<Task>? wait = null)
    {
        try { await (wait?.Invoke() ?? work.WaitAsync(allowance)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (work.IsCompleted)
            {
                await work.ConfigureAwait(false);
                throw;
            }

            _ = work.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new NativeValidationUnjoinedWorkException();
        }
    }

    /// <summary>Verifies artifacts after cleanup; exposed internally for controlled I/O-stall tests.</summary>
    /// <param name="verify">The written-artifact verifier; production always supplies the real writer.</param>
    /// <param name="allowance">The positive, capped allowance already validated by the runner.</param>
    /// <param name="wait">Optional test-only wait boundary for forcing verification/deadline ordering.</param>
    /// <returns>Whether all artifacts pass final verification.</returns>
    /// <remarks>
    /// An independent deadline preserves receipt finalization after caller cancellation. The outer wait
    /// also bounds synchronous file opening. A late verifier only reads artifacts; its token source
    /// remains owned until completion, when any fault is observed. It cannot change the failed receipt.
    /// A verifier already settled when the wait fails is awaited so its fatal fault still propagates.
    /// </remarks>
    internal static async Task<bool> VerifyArtifactsAsync(Func<CancellationToken, Task<bool>> verify, TimeSpan allowance,
        Func<Task<bool>, CancellationToken, Task<bool>>? wait = null)
    {
        var deadline = new CancellationTokenSource(allowance);
        var verification = Task.Run(() => verify(deadline.Token), CancellationToken.None);
        try
        {
            var verified = await (wait?.Invoke(verification, deadline.Token) ?? verification.WaitAsync(deadline.Token)).ConfigureAwait(false);
            return verified && !deadline.IsCancellationRequested;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            if (verification.IsCompleted)
            {
                try { await verification.ConfigureAwait(false); }
                catch (Exception settledException) when (IsNonFatal(settledException))
                {
                    _ = settledException;
                }
            }

            return false;
        }
        finally
        {
            if (verification.IsCompleted)
            {
                _ = verification.Exception;
                deadline.Dispose();
            }
            else
            {
                _ = verification.ContinueWith(task =>
                {
                    _ = task.Exception;
                    deadline.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static EvidenceProducerResult ValidateResult(
        EvidenceProducerDeclaration declaration,
        EvidenceArtifactWriter writer,
        EvidenceProducerResult? result,
        long elapsedMilliseconds)
    {
        var artifacts = writer.WrittenArtifacts;
        if (result is null
            || !string.Equals(result.ProducerId, declaration.Id, StringComparison.Ordinal)
            || !Enum.IsDefined(result.Outcome)
            || result.SatisfiedAssertionIds is null
            || result.SatisfiedAssertionIds.Any(static assertion => assertion is null)
            || result.SatisfiedAssertionIds.Distinct(StringComparer.Ordinal).Count() != result.SatisfiedAssertionIds.Count
            || result.SatisfiedAssertionIds.Any(assertion => !declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal))
            || (result.Artifacts is not null && !result.Artifacts.SequenceEqual(artifacts))
            || !NativeValidationReceiptCodec.ValidArtifactSet(
                declaration, artifacts, requireRequiredSlots: result.Outcome == EvidenceProducerOutcome.Passed))
        {
            return Failure(declaration.Id, EvidenceProducerOutcome.Invalid,
                "The native package journey result did not match its declaration.", artifacts, elapsedMilliseconds);
        }

        if (result.Outcome == EvidenceProducerOutcome.Passed
            && !result.SatisfiedAssertionIds.Order(StringComparer.Ordinal)
                .SequenceEqual(declaration.AssertionIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return Failure(declaration.Id, EvidenceProducerOutcome.Invalid,
                "The native package journey did not satisfy every declared assertion.", artifacts, elapsedMilliseconds);
        }

        return result with { ElapsedMilliseconds = elapsedMilliseconds };
    }

    private static EvidenceProducerResult Failure(
        string producerId,
        EvidenceProducerOutcome outcome,
        string diagnostic,
        IReadOnlyList<EvidenceArtifactResult>? artifacts = null,
        long elapsedMilliseconds = 0) =>
        new(producerId, outcome, [], diagnostic, artifacts, elapsedMilliseconds);

    private static string? BoundDiagnostic(string? diagnostic) =>
        diagnostic is { Length: > NativeValidationReceiptCodec.MaximumDiagnosticCharacters }
            ? diagnostic[..NativeValidationReceiptCodec.MaximumDiagnosticCharacters] : diagnostic;

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException
            and not AccessViolationException and not AppDomainUnloadedException;

    private sealed record CompletedJourney(
        EvidenceProducerDeclaration Declaration,
        EvidenceArtifactWriter Writer,
        EvidenceProducerResult Result);
}

/// <summary>Stops the repository recipe without disposing live work or publishing a receipt.</summary>
internal sealed class NativeValidationUnjoinedWorkException()
    : InvalidOperationException("Native package validation work did not join within its cleanup allowance.");
