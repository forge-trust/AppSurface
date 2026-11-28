using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Adapts the existing CLI producers to the private whole-invocation receipt coordinator.</summary>
/// <param name="coverageProducer">The existing in-process coverage execution adapter.</param>
/// <param name="sdkExecutableForTests">Optional fully qualified executable used only for SDK version queries in
/// deterministic process-lifetime tests. Production callers omit it to retain normal <c>dotnet</c> resolution.
/// It does not replace the coverage engine's process runner or add a user-facing executable override.</param>
/// <exception cref="ArgumentException">The supplied test executable path is not fully qualified.</exception>
internal sealed class EvidenceReuseExecutionResolver(CoverageEvidenceProducer coverageProducer,
    string? sdkExecutableForTests = null)
{
    private const string CoverageImplementationIdentity = "appsurface-coverage-evidence-adapter-v1";
    private const string ConsumerHostRequired = "reuse.consumer_host_required";
    private readonly string _sdkExecutable = sdkExecutableForTests is null ? "dotnet"
        : Path.IsPathFullyQualified(sdkExecutableForTests) ? sdkExecutableForTests
        : throw new ArgumentException("The SDK query test executable must have a fully qualified path.", nameof(sdkExecutableForTests));

    internal async Task<EvidenceReuseRunResult> RunAsync(
        EvidencePlan plan,
        EvidenceDiffSnapshot? diffSnapshot,
        string? solutionPath,
        string outputDirectory,
        bool verifyReuse,
        bool observationOnly,
        string? storeRoot,
        TimeSpan waitTimeout,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (plan.Profile.Resources.Count > 0)
        {
            var manifest = EvidenceCliWorkflow.BuildResourceBackedRefusal(plan, observationOnly);
            var decisions = DescribeResourceBackedRefusal(plan,
                "The CLI does not prepare consumer-owned resources or custom producer registrations.");
            return new EvidenceReuseRunResult(manifest, decisions);
        }

        var invocationDirectory = Directory.GetCurrentDirectory();
        var repositoryRoot = FindRepositoryRoot(invocationDirectory);
        EvidenceProducerReuseSession? session = null;
        try
        {
            var prepared = await PrepareAsync(plan, diffSnapshot, solutionPath, invocationDirectory, repositoryRoot,
                storeRoot, waitTimeout, new(outputDirectory, CoverageTextWriters.Create(output, error), verifyReuse),
                cancellationToken).ConfigureAwait(false);
            session = prepared.Session;
            cancellationToken.ThrowIfCancellationRequested();

            var results = new List<EvidenceProducerResult>(plan.Profile.Producers.Count);
            if (session.IsUnavailable || session.IsCancelled)
            {
                results.AddRange(GetUnavailableProducerResults(session));
            }
            else if (!await session.BeginExecutionAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.AddRange(GetUnavailableProducerResults(session));
            }
            else
            {
                results.AddRange(session.ReusedResults.Values);
                foreach (var producer in plan.Profile.Producers.Where(producer => !session.ReusedResults.ContainsKey(producer.Id)))
                {
                    var adapter = prepared.Adapters[producer.Id];
                    Directory.CreateDirectory(adapter.RunOutput.ExecutionRoot);
                    var context = new EvidenceProducerContext(plan, producer, TimeProvider.System, session.GetWriter(producer));
                    results.Add(await adapter.ProduceFreshAsync(context, session.GetChildLifetime(producer.Id), cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            var preCleanupSettled = await session.BeforeCleanupAsync(actualProducerTasksSettled: true, cancellationToken).ConfigureAwait(false);
            var actualTasksJoined = preCleanupSettled
                ? await session.JoinActualTasksAsync().ConfigureAwait(false)
                : await SettleUnsuccessfulExecutionAsync(session).ConfigureAwait(false);

            var manifest = EvidenceManifestBuilder.Build(plan, results, observationOnly,
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = preCleanupSettled && actualTasksJoined });
            manifest = await session.CompleteAsync(manifest, cancellationToken).ConfigureAwait(false);
            return new EvidenceReuseRunResult(manifest, ToSummaryDecisions(session.Decisions, prepared.PreparationDiagnostics).Select(decision =>
                {
                    if (!prepared.Adapters.TryGetValue(decision.ProducerId, out var adapter)) return decision;
                    var roots = adapter.RunOutput;
                    return decision with
                    {
                        VerifiedReceiptCompared = session.HasMatchedVerifiedAudit(decision.ProducerId),
                        ExecutionRoot = Path.GetRelativePath(outputDirectory, roots.ExecutionRoot).Replace(Path.DirectorySeparatorChar, '/'),
                        ArtifactRoot = Path.GetRelativePath(outputDirectory, roots.ArtifactRoot).Replace(Path.DirectorySeparatorChar, '/'),
                    };
                }).ToArray());
        }
        catch
        {
            if (session is not null)
                await SettleUnsuccessfulExecutionAsync(session).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IEnumerable<EvidenceProducerResult> GetUnavailableProducerResults(EvidenceProducerReuseSession session)
        => session.Decisions.Select(static decision => new EvidenceProducerResult(
            decision.ProducerId, EvidenceProducerOutcome.Unavailable, [], decision.Diagnostic));

    private static async Task<bool> SettleUnsuccessfulExecutionAsync(EvidenceProducerReuseSession session)
    {
        var joined = await session.JoinActualTasksAsync().ConfigureAwait(false);
        session.CompleteUnsuccessfulSettlement(actualCleanupCompleted: joined);
        return joined;
    }

    internal async Task<IReadOnlyList<EvidenceReuseSummaryDecision>> ExplainAsync(
        EvidencePlan plan, EvidenceDiffSnapshot? diffSnapshot, string? solutionPath, string? storeRoot,
        TimeSpan waitTimeout, CancellationToken cancellationToken)
    {
        var invocationDirectory = Directory.GetCurrentDirectory();
        var repositoryRoot = FindRepositoryRoot(invocationDirectory);
        if (plan.Profile.Resources.Count > 0)
            return DescribeResourceBackedRefusal(plan,
                "The CLI cannot prepare consumer-owned resources or registrations. Use EvidenceHostBootstrap.PrepareAsync in the consumer host.");
        var prepared = await PrepareAsync(plan, diffSnapshot, solutionPath, invocationDirectory, repositoryRoot, storeRoot,
            waitTimeout, run: null, cancellationToken).ConfigureAwait(false);
        await using var session = prepared.Session;
        return ToSummaryDecisions(session.Decisions, prepared.PreparationDiagnostics);
    }

    private static IReadOnlyList<EvidenceReuseSummaryDecision> DescribeResourceBackedRefusal(EvidencePlan plan, string diagnostic)
        => ToSummaryDecisions(plan.Profile.Producers.Select(producer =>
            new EvidenceProducerReuseDecision(producer.Id, EvidenceProducerReuseStatus.Ineligible, ConsumerHostRequired, diagnostic)
            {
                Publication = EvidenceReceiptPublication.NotPublished,
            }));

    /// <summary>Prepares the same producer identities and diagnostic prerequisites for run and explain.</summary>
    /// <remarks>Only a run has output roots, progress writers, or an audit mode. Explain can inspect receipt
    /// candidates but attempting to request its producer output fails closed. Eligible coverage producers share
    /// one initial SDK observation for the solution and recheck the live SDK in their owned key callbacks.</remarks>
    private async Task<PreparedReuse> PrepareAsync(EvidencePlan plan, EvidenceDiffSnapshot? diffSnapshot,
        string? solutionPath, string invocationDirectory, string repositoryRoot, string? storeRoot, TimeSpan waitTimeout,
        RunPreparation? run, CancellationToken cancellationToken)
    {
        var fullSolutionPath = ResolveCoverageSolutionPath(plan, solutionPath, invocationDirectory);
        var hasSolutionIdentity = IsSolutionIdentitySafe(repositoryRoot, fullSolutionPath);
        var initialSdkVersion = hasSolutionIdentity && plan.Profile.Producers.Any(RequiresCoverageIdentity)
            ? await ReadDotnetSdkVersionAsync(fullSolutionPath, cancellationToken, _sdkExecutable).ConfigureAwait(false)
            : null;
        var adapters = new Dictionary<string, CoverageProducerAdapter>(StringComparer.Ordinal);
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal);
        var preparationDiagnostics = new Dictionary<string, (string Reason, string Diagnostic)>(StringComparer.Ordinal);
        foreach (var producer in plan.Profile.Producers)
        {
            ProducerRunOutput? runOutput = null;
            if (run is not null)
            {
                var roots = GetProducerRoots(run.OutputDirectory, producer.Id,
                    useInvocationArtifactRoot: plan.Profile.Producers.Count == 1);
                runOutput = new(roots.Execution, roots.Artifacts, run.Writers, diffSnapshot);
            }
            var sdkVersion = RequiresCoverageIdentity(producer) ? initialSdkVersion : null;
            var adapter = new CoverageProducerAdapter(producer.Id, coverageProducer, fullSolutionPath, runOutput,
                sdkVersion, _sdkExecutable, repositoryRoot);
            var isCoverageProducer = producer.IsCoverageProducer();
            var implementationIdentity = isCoverageProducer
                ? CoverageImplementationIdentity
                : "appsurface-cli-consumer-host-required-v1";
            adapters.Add(producer.Id, adapter);
            registrations.Add(producer.Id, new(adapter, implementationIdentity,
                CreateToolIdentity(producer, repositoryRoot, fullSolutionPath, sdkVersion ?? "not-applicable")));
            if (GetPreparationDiagnostic(isCoverageProducer, hasSolutionIdentity, sdkVersion) is { } diagnostic)
            {
                preparationDiagnostics.Add(producer.Id, diagnostic);
            }
        }

        var options = new EvidenceProducerReuseOptions(true, repositoryRoot, storeRoot, waitTimeout,
            TimeSpan.FromSeconds(10), VerifyReuse: run?.VerifyReuse == true, ReadOnly: run is null,
            diffSnapshot?.Bytes, TimeProvider.System);
        if (run is not null)
            options = options with
            {
                ForbiddenRoots = [run.OutputDirectory],
                WaitProgress = (prefix, elapsed, remaining) => WriteWaitProgress(run.Writers.Error, prefix, elapsed, remaining),
            };

        var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(StringComparer.Ordinal), options,
            declaration => adapters[declaration.Id].RunOutput.ArtifactRoot, cancellationToken).ConfigureAwait(false);
        return new(session, adapters, preparationDiagnostics);
    }

    private static (string Reason, string Diagnostic)? GetPreparationDiagnostic(
        bool isCoverageProducer, bool hasSolutionIdentity, string? sdkVersion)
    {
        if (!isCoverageProducer)
            return (ConsumerHostRequired,
                "The CLI does not register custom producers. Use EvidenceHostBootstrap.PrepareAsync in the consumer host.");
        if (!hasSolutionIdentity)
            return ("reuse.solution_required",
                "Coverage identity requires --solution resolving to a file inside this Git worktree. Register custom solution resolution through the consumer EvidenceHost.");
        if (string.Equals(sdkVersion, "unavailable", StringComparison.Ordinal))
            return ("reuse.sdk_unavailable",
                "The actual dotnet SDK version could not be resolved; execute fresh or repair the local SDK installation.");
        return null;
    }

    /// <summary>Projects coordinator decisions using CLI diagnostics captured during preparation.</summary>
    /// <remarks>Only unkeyed ineligible decisions can use a preparation diagnostic. Keyed decisions retain the
    /// coordinator's reason because they already passed identity construction.</remarks>
    /// <param name="decisions">Coordinator decisions for this invocation.</param>
    /// <param name="preparationDiagnostics">Optional CLI diagnostic overrides for producers with unmet prerequisites.</param>
    /// <returns>Summary rows in coordinator decision order.</returns>
    internal static IReadOnlyList<EvidenceReuseSummaryDecision> ToSummaryDecisions(
        IEnumerable<EvidenceProducerReuseDecision> decisions,
        IReadOnlyDictionary<string, (string Reason, string Diagnostic)>? preparationDiagnostics = null) => decisions
        .Select(decision =>
        {
            var reason = decision.Reason;
            var diagnostic = decision.Diagnostic;
            if (decision is { Status: EvidenceProducerReuseStatus.Ineligible, ProducerKey: null }
                && preparationDiagnostics?.TryGetValue(decision.ProducerId, out var preparationDiagnostic) == true)
            {
                (reason, diagnostic) = preparationDiagnostic;
            }
            var publication = decision.Publication switch
            {
                EvidenceReceiptPublication.Published => "published",
                EvidenceReceiptPublication.Suppressed => "suppressed",
                EvidenceReceiptPublication.Restored => "restored",
                _ => "not-published",
            };
            return new EvidenceReuseSummaryDecision(decision.ProducerId, decision.Status.ToString(),
                reason, diagnostic, decision.ProducerKey, decision.OriginCompletedAt,
            decision.OriginElapsedMilliseconds, publication);
        }).ToArray();

    internal static IReadOnlyDictionary<string, string> CreateToolIdentity(
        EvidenceProducerDeclaration producer, string repositoryRoot, string? solutionPath, string sdkVersion)
    {
        if (!producer.IsCoverageProducer())
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new CoverageEvidenceRunSettings(producer.CoverageExecution);
        var solutionIdentity = "missing";
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var fullSolution = Path.GetFullPath(solutionPath);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            if (IsInsideRepository(fullRoot, fullSolution))
                solutionIdentity = Path.GetRelativePath(fullRoot, fullSolution).Replace(Path.DirectorySeparatorChar, '/');
            else if (string.Equals(fullSolution, fullRoot, PathComparison))
                solutionIdentity = Path.GetFileName(fullSolution);
            else
                solutionIdentity = "outside-repository";
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sdk"] = sdkVersion,
            ["appSurfaceVersion"] = GetCoordinatedVersion(typeof(CoverageEvidenceProducer).Assembly),
            ["coverageSupportVersion"] = GetCoordinatedVersion(typeof(StrictReportGeneratorRuntimeLocator).Assembly),
            ["reportGeneratorVersion"] = StrictReportGeneratorRuntimeLocator.Version,
            ["reportGeneratorManifest"] = StrictReportGeneratorRuntimeLocator.GetPinnedManifestDigest() ?? "unavailable",
            ["solution"] = solutionIdentity,
            ["configuration"] = settings.Configuration,
            ["parallelism"] = settings.Parallelism.ToString(CultureInfo.InvariantCulture),
            ["testResults"] = settings.TestResults.ToString(),
            ["coverageDriver"] = CoverageEvidenceRunSettings.Driver.ToString(),
            ["schedule"] = CoverageEvidenceRunSettings.Schedule.ToString(),
            ["restore"] = CoverageEvidenceRunSettings.NoRestore ? "disabled" : "enabled",
            ["build"] = CoverageEvidenceRunSettings.Build ? "enabled" : "disabled",
            ["testExclusion"] = CoverageEvidenceRunSettings.TestExclusion,
            ["cooperativeProtocol"] = string.Join("\n", EvidenceCoverageProcessRunner.EffectiveArgumentIdentity),
        };
    }

    private static string GetCoordinatedVersion(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) return "unavailable";
        var metadata = version.IndexOf('+');
        return metadata < 0 ? version : version[..metadata];
    }

    private static void WriteWaitProgress(TextWriter error, string keyPrefix, TimeSpan elapsed, TimeSpan remaining)
    {
        try
        {
            error.WriteLine($"reuse.waiting key={keyPrefix} elapsed={elapsed.ToString("c", CultureInfo.InvariantCulture)} remaining={remaining.ToString("c", CultureInfo.InvariantCulture)}");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Progress is advisory; a closed diagnostic stream must not change receipt admission or execution.
        }
    }

    private static bool RequiresCoverageIdentity(EvidenceProducerDeclaration producer) =>
        producer.IsCoverageProducer()
        && producer.Inputs is not null
        && producer.CoverageExecution is { CooperativeChildLifetime: true };

    private static string? ResolveCoverageSolutionPath(EvidencePlan plan, string? solutionPath, string invocationDirectory)
        => !string.IsNullOrWhiteSpace(solutionPath) && plan.Profile.Producers.Any(static producer => producer.IsCoverageProducer())
            ? Path.GetFullPath(solutionPath, invocationDirectory)
            : null;

    private static bool IsSolutionIdentitySafe(string repositoryRoot, string? fullSolutionPath)
        => fullSolutionPath is not null && File.Exists(fullSolutionPath)
            && IsInsideRepository(Path.TrimEndingDirectorySeparator(repositoryRoot), fullSolutionPath);

    private static bool IsInsideRepository(string fullRoot, string fullSolution)
        => fullSolution.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Separates fresh collector output from receipt custody while preserving the single-producer run-root report layout.</summary>
    /// <param name="outputDirectory">Exclusive invocation output root.</param>
    /// <param name="producerId">Opaque producer identifier, hashed before path composition.</param>
    /// <param name="useInvocationArtifactRoot">Whether the sole producer can publish directly beneath the invocation root.</param>
    /// <returns>Fresh execution and artifact custody roots; multiple producers use disjoint custody roots.</returns>
    internal static (string Execution, string Artifacts) GetProducerRoots(string outputDirectory, string producerId,
        bool useInvocationArtifactRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return (Path.Join(outputDirectory, "execution", EvidenceReuseOutput.GetProducerDirectoryName(producerId)),
            Path.Combine([outputDirectory, .. EvidenceReuseOutput.GetCustodyComponents(producerId, useInvocationArtifactRoot)]));
    }

    private static async Task<string> ReadDotnetSdkVersionAsync(string? solutionPath, CancellationToken cancellationToken,
        string sdkExecutable)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var workingDirectory = string.IsNullOrWhiteSpace(solutionPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Directory.GetCurrentDirectory();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(sdkExecutable, "--version")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        try { if (!process.Start()) return "unavailable"; }
        catch (System.ComponentModel.Win32Exception) { return "unavailable"; }
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return "unavailable";
        }
        var version = (await stdout.ConfigureAwait(false)).Trim();
        await stderr.ConfigureAwait(false);
        return process.ExitCode == 0 && version.Length is > 0 and <= 128 ? version : "unavailable";
    }

    private static string FindRepositoryRoot(string directory) =>
        GitRepositoryRootResolver.FindWorktreeRoot(directory)
        ?? throw new EvidenceCliException("ASEVD218", "Local receipt preparation requires a Git worktree. Code: reuse.repository_required. No producer or resource was started.",
            "Run Evidence from the repository containing the declared inputs and selected solution.");

    /// <summary>Carries CLI run inputs that read-only explanation does not require.</summary>
    private sealed record RunPreparation(string OutputDirectory, CoverageTextWriters Writers, bool VerifyReuse);

    /// <summary>Stores the per-producer output roots and inputs used only by fresh execution.</summary>
    private sealed record ProducerRunOutput(string ExecutionRoot, string ArtifactRoot, CoverageTextWriters Writers,
        EvidenceDiffSnapshot? DiffSnapshot);

    /// <summary>Returns the prepared session, producer adapters, and CLI diagnostics for unmet prerequisites.</summary>
    private readonly record struct PreparedReuse(EvidenceProducerReuseSession Session,
        IReadOnlyDictionary<string, CoverageProducerAdapter> Adapters,
        IReadOnlyDictionary<string, (string Reason, string Diagnostic)> PreparationDiagnostics);

    private sealed class CoverageProducerAdapter(
        string id, CoverageEvidenceProducer producer, string? solutionPath, ProducerRunOutput? runOutput,
        string? initialSdkVersion, string sdkExecutable,
        string repositoryRoot) : IEvidenceProducer, IEvidenceProducerKeyMaterial, IEvidenceProducerReceiptEligibility
    {
        private string? _receiptIneligibilityReason;
        public string Id { get; } = id;
        public string? ReceiptIneligibilityReason => Volatile.Read(ref _receiptIneligibilityReason);

        /// <summary>Gets the output for a run and rejects any attempt to allocate output during explanation.</summary>
        internal ProducerRunOutput RunOutput => runOutput
            ?? throw new InvalidOperationException("Read-only explain cannot allocate producer output.");

        ValueTask<EvidenceProducerResult> IEvidenceProducer.ProduceAsync(
            EvidenceProducerContext context, CancellationToken cancellationToken)
            => ProduceFreshAsync(context, childLifetime: null, cancellationToken);

        /// <summary>Executes fresh coverage with the per-producer child lifetime supplied after admission, when available.</summary>
        /// <param name="context">Producer declaration and invocation-owned artifact writer.</param>
        /// <param name="childLifetime">The admitted child lifetime, or null for fresh execution without receipt custody.</param>
        /// <param name="cancellationToken">Cancels the fresh producer run.</param>
        /// <returns>The fresh producer result with elapsed time.</returns>
        internal async ValueTask<EvidenceProducerResult> ProduceFreshAsync(EvidenceProducerContext context,
            EvidenceChildLifetimeSession? childLifetime, CancellationToken cancellationToken)
        {
            var output = RunOutput;
            if (!context.Producer.IsCoverageProducer())
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Unavailable, [],
                    "This producer requires an explicit consumer EvidenceHost registration; the CLI does not infer custom producer registrations.");
            if (solutionPath is null)
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Unavailable, [], "reuse.solution_required: The coverage producer requires --solution so it can reuse AppSurface coverage discovery. Register custom solution resolution through the consumer host.");
            var elapsed = Stopwatch.StartNew();
            var execution = await producer.RunWithInventoryAsync(new(
                Producer: context.Producer,
                SolutionPath: solutionPath,
                ExecutionRoot: output.ExecutionRoot,
                DiffSnapshot: output.DiffSnapshot,
                Writers: output.Writers,
                ArtifactWriter: context.Artifacts,
                ChildLifetime: childLifetime,
                FinalReportRoot: output.ArtifactRoot), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _receiptIneligibilityReason, execution.InventoryIneligibilityReason);
            return execution.Result with { ElapsedMilliseconds = elapsed.ElapsedMilliseconds };
        }

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Producer.IsCoverageProducer())
                throw new InvalidOperationException("reuse.consumer_host_required");
            if (solutionPath is null)
                throw new InvalidOperationException("reuse.solution_required");
            if (initialSdkVersion is null || !IsSolutionIdentitySafe(repositoryRoot, solutionPath))
                throw new InvalidOperationException("reuse.solution_identity_unavailable");
            var currentSdkVersion = await ReadDotnetSdkVersionAsync(solutionPath, cancellationToken, sdkExecutable).ConfigureAwait(false);
            if (string.Equals(initialSdkVersion, "unavailable", StringComparison.Ordinal)
                || string.Equals(currentSdkVersion, "unavailable", StringComparison.Ordinal))
                throw new InvalidOperationException("reuse.sdk_unavailable");
            // Local source builds can share a semantic version. Bind their actual shipped implementation bytes
            // inside the owned bounded key callback rather than treating the version alone as immutable.
            static string AssemblyDigest(Assembly assembly)
            {
                if (string.IsNullOrEmpty(assembly.Location))
                    throw new InvalidOperationException("reuse.tool_identity_unavailable");
                using var input = File.OpenRead(assembly.Location);
                return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            }
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sdk"] = currentSdkVersion,
                ["adapterBinary"] = AssemblyDigest(typeof(CoverageEvidenceProducer).Assembly),
                ["supportBinary"] = AssemblyDigest(typeof(StrictReportGeneratorRuntimeLocator).Assembly),
                ["contractsBinary"] = AssemblyDigest(typeof(EvidenceOwnedChild).Assembly),
            };
        }
    }
}

/// <summary>Returns the current canonical manifest and invocation-local receipt decisions to CLI presentation.</summary>
/// <param name="Manifest">Current obligations and outcomes, with reuse always informational.</param>
/// <param name="Decisions">Bounded safe key, origin, publication and output-root explanations.</param>
internal sealed record EvidenceReuseRunResult(EvidenceManifest Manifest, IReadOnlyList<EvidenceReuseSummaryDecision> Decisions);

/// <summary>Provides the human and JSON reuse row without importing historical duration into current execution metrics.</summary>
/// <param name="ProducerId">Opaque selected producer identifier.</param>
/// <param name="Status">Hit, miss, ineligible or unavailable preparation status.</param>
/// <param name="Reason">Stable reuse diagnostic code.</param>
/// <param name="Diagnostic">Safe cause and recovery guidance.</param>
/// <param name="ProducerKey">Content key when available.</param>
/// <param name="OriginCompletedAt">Original completion time, never renewed by a read.</param>
/// <param name="OriginElapsedMilliseconds">Historical execution duration, separate from current elapsed time.</param>
/// <param name="Publication">Whether a fresh receipt was published or suppressed.</param>
/// <param name="ExecutionRoot">Current output-relative original execution tree, absent for explain.</param>
/// <param name="ArtifactRoot">Current output-relative custody root, absent for explain.</param>
internal sealed record EvidenceReuseSummaryDecision(
    string ProducerId,
    string Status,
    string Reason,
    string Diagnostic,
    string? ProducerKey,
    DateTimeOffset? OriginCompletedAt,
    long? OriginElapsedMilliseconds,
    string Publication,
    string? ExecutionRoot = null,
    string? ArtifactRoot = null)
{
    /// <summary>Gets whether fresh outcome/assertions matched an actually verified receipt in this invocation.</summary>
    /// <remarks>False for ordinary fresh execution, read-only candidates, and an audit without a comparable receipt.
    /// A true value records only comparison; the completed manifest and publication decision remain independent.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool VerifiedReceiptCompared { get; init; }
}
