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
            var resourceResults = plan.Profile.Resources.Select(resource => new EvidenceResourceResult(resource.Id,
                EvidenceResourceOutcome.Unavailable, 0,
                "Resource-backed evidence requires an explicitly registered consumer EvidenceHost; the CLI does not provision or infer consumer resources.")).ToArray();
            var unavailable = plan.Profile.Producers.Select(producer => new EvidenceProducerResult(producer.Id,
                EvidenceProducerOutcome.Unavailable, [],
                "The selected profile requires consumer-owned resources. Run it through ForgeTrust.AppSurface.Evidence.Aspire with explicit registrations.")).ToArray();
            var manifest = EvidenceManifestBuilder.Build(plan, unavailable, observationOnly, resourceResults: resourceResults);
            var decisions = plan.Profile.Producers.Select(producer => new EvidenceReuseSummaryDecision(producer.Id,
                "Ineligible", "reuse.consumer_host_required",
                "The CLI does not prepare consumer-owned resources or custom producer registrations.", null, null, null, "not-published")).ToArray();
            return new EvidenceReuseRunResult(manifest, decisions);
        }

        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal);
        var adapters = new Dictionary<string, CoverageProducerAdapter>(StringComparer.Ordinal);
        var producerRoots = new Dictionary<string, (string Execution, string Artifacts)>(StringComparer.Ordinal);
        foreach (var producer in plan.Profile.Producers)
        {
            var roots = GetProducerRoots(outputDirectory, producer.Id,
                useInvocationArtifactRoot: plan.Profile.Producers.Count == 1);
            producerRoots.Add(producer.Id, roots);
            var setup = await CreateProducerSetupAsync(producer, repositoryRoot, solutionPath,
                roots.Execution, roots.Artifacts,
                diffSnapshot, output, error, cancellationToken).ConfigureAwait(false);
            adapters.Add(producer.Id, setup.Adapter);
            registrations.Add(producer.Id, setup.Registration);
        }

        EvidenceProducerReuseSession? session = null;
        var resourceIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        var clock = TimeProvider.System;
        try
        {
            session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations, resourceIdentities,
                new EvidenceProducerReuseOptions(true, repositoryRoot, storeRoot, waitTimeout, TimeSpan.FromSeconds(10),
                    verifyReuse, false, diffSnapshot?.Bytes, clock)
                {
                    ForbiddenRoots = [outputDirectory],
                    WaitProgress = (keyPrefix, elapsed, remaining) => WriteWaitProgress(error, keyPrefix, elapsed, remaining),
                },
                declaration => producerRoots[declaration.Id].Artifacts, cancellationToken).ConfigureAwait(false);
            foreach (var adapter in adapters.Values) adapter.SetSession(session);
            cancellationToken.ThrowIfCancellationRequested();

            var results = new List<EvidenceProducerResult>(plan.Profile.Producers.Count);
            var resources = plan.Profile.Resources.Select(resource => new EvidenceResourceResult(
                resource.Id, EvidenceResourceOutcome.Unavailable, 0,
                "Resource-backed evidence requires an explicitly registered consumer EvidenceHost; the CLI does not provision or infer consumer resources.")).ToArray();
            if (session.IsUnavailable || session.IsCancelled)
            {
                results.AddRange(plan.Profile.Producers.Select(producer => new EvidenceProducerResult(producer.Id,
                    EvidenceProducerOutcome.Unavailable, [], session.Decisions.Single(decision => decision.ProducerId == producer.Id).Diagnostic)));
            }
            else if (!await session.BeginExecutionAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.AddRange(plan.Profile.Producers.Select(producer => new EvidenceProducerResult(producer.Id,
                    EvidenceProducerOutcome.Unavailable, [], session.Decisions.Single(decision => decision.ProducerId == producer.Id).Diagnostic)));
            }
            else
            {
                results.AddRange(session.ReusedResults.Values);
                foreach (var producer in plan.Profile.Producers.Where(producer => !session.ReusedResults.ContainsKey(producer.Id)))
                {
                    Directory.CreateDirectory(producerRoots[producer.Id].Execution);
                    results.Add(await adapters[producer.Id].ProduceAsync(
                        new EvidenceProducerContext(plan, producer, clock, session.GetWriter(producer)), cancellationToken).ConfigureAwait(false));
                }
            }

            var preCleanupSettled = await session.BeforeCleanupAsync(actualProducerTasksSettled: true, cancellationToken).ConfigureAwait(false);
            var actualTasksJoined = false;
            if (!preCleanupSettled)
            {
                await session.JoinKeyMaterialAsync().ConfigureAwait(false);
                actualTasksJoined = await session.JoinActualTasksAsync().ConfigureAwait(false);
                await session.CompleteUnsuccessfulSettlementAsync(actualCleanupCompleted: actualTasksJoined).ConfigureAwait(false);
            }
            else
            {
                actualTasksJoined = await session.JoinActualTasksAsync().ConfigureAwait(false);
            }

            var manifest = EvidenceManifestBuilder.Build(plan, results, observationOnly, resourceResults: resources,
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = preCleanupSettled && actualTasksJoined });
            manifest = await session.CompleteAsync(manifest, cancellationToken).ConfigureAwait(false);
            var resolvedSdks = registrations.ToDictionary(static pair => pair.Key,
                static pair => pair.Value.EffectiveToolIdentity.GetValueOrDefault("sdk", "not-applicable"), StringComparer.Ordinal);
            return new EvidenceReuseRunResult(manifest, ToSummaryDecisions(plan, session.Decisions, solutionPath, resolvedSdks)
                .Select(decision => producerRoots.TryGetValue(decision.ProducerId, out var roots)
                    ? decision with
                    {
                        VerifiedReceiptCompared = session.HasMatchedVerifiedAudit(decision.ProducerId),
                        ExecutionRoot = Path.GetRelativePath(outputDirectory, roots.Execution).Replace(Path.DirectorySeparatorChar, '/'),
                        ArtifactRoot = Path.GetRelativePath(outputDirectory, roots.Artifacts).Replace(Path.DirectorySeparatorChar, '/'),
                    }
                    : decision).ToArray());
        }
        catch
        {
            if (session is not null)
            {
                await session.JoinKeyMaterialAsync().ConfigureAwait(false);
                var joined = await session.JoinActualTasksAsync().ConfigureAwait(false);
                await session.CompleteUnsuccessfulSettlementAsync(actualCleanupCompleted: joined).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<EvidenceReuseSummaryDecision>> ExplainAsync(
        EvidencePlan plan, EvidenceDiffSnapshot? diffSnapshot, string? solutionPath, string? storeRoot,
        TimeSpan waitTimeout, CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (plan.Profile.Resources.Count > 0)
            return plan.Profile.Producers.Select(producer => new EvidenceReuseSummaryDecision(producer.Id,
                "Ineligible", "reuse.consumer_host_required",
                "The CLI cannot prepare consumer-owned resources or registrations. Use EvidenceHostBootstrap.PrepareAsync in the consumer host.",
                null, null, null, "not-published")).ToArray();
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal);
        foreach (var producer in plan.Profile.Producers)
        {
            var setup = await CreateProducerSetupAsync(producer, repositoryRoot, solutionPath,
                string.Empty, null, diffSnapshot, TextWriter.Null, TextWriter.Null, cancellationToken).ConfigureAwait(false);
            registrations.Add(producer.Id, setup.Registration);
        }

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new EvidenceProducerReuseOptions(true, repositoryRoot, storeRoot, waitTimeout, TimeSpan.FromSeconds(10),
                VerifyReuse: false, ReadOnly: true, diffSnapshot?.Bytes, TimeProvider.System),
            static _ => throw new InvalidOperationException("Read-only explain cannot allocate producer output."), cancellationToken).ConfigureAwait(false);
        var resolvedSdks = registrations.ToDictionary(static pair => pair.Key,
            static pair => pair.Value.EffectiveToolIdentity.GetValueOrDefault("sdk", "not-applicable"), StringComparer.Ordinal);
        return ToSummaryDecisions(plan, session.Decisions, solutionPath, resolvedSdks);
    }

    /// <summary>Projects coordinator decisions without replacing an ownership failure with identity ineligibility.</summary>
    /// <param name="plan">Resolved producer declarations.</param>
    /// <param name="decisions">Coordinator decisions for this invocation.</param>
    /// <param name="solutionPath">Explicit built-in coverage solution identity.</param>
    /// <param name="sdkVersions">Resolved per-producer SDK identities.</param>
    internal static IReadOnlyList<EvidenceReuseSummaryDecision> ToSummaryDecisions(
        EvidencePlan plan, IReadOnlyList<EvidenceProducerReuseDecision> decisions, string? solutionPath,
        IReadOnlyDictionary<string, string> sdkVersions) => decisions
        .Select(decision =>
        {
            var producer = plan.Profile.Producers.Single(item => item.Id == decision.ProducerId);
            var missingSolution = string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
                && !IsSolutionIdentitySafe(FindRepositoryRoot(Directory.GetCurrentDirectory()), solutionPath);
            var unsupportedRegistration = !string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
                && decision.Status == EvidenceProducerReuseStatus.Ineligible;
            var sdkUnavailable = string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
                && decision.Status == EvidenceProducerReuseStatus.Ineligible
                && string.Equals(sdkVersions.GetValueOrDefault(producer.Id), "unavailable", StringComparison.Ordinal);
            var reason = missingSolution && decision.Status == EvidenceProducerReuseStatus.Ineligible
                ? "reuse.solution_required"
                : unsupportedRegistration ? "reuse.consumer_host_required"
                : sdkUnavailable ? "reuse.sdk_unavailable" : decision.Reason;
            var diagnostic = missingSolution && decision.Status == EvidenceProducerReuseStatus.Ineligible
                ? "Coverage identity requires --solution resolving to a file inside this Git worktree. Register custom solution resolution through the consumer EvidenceHost."
                : unsupportedRegistration
                    ? "The CLI does not register custom producers. Use EvidenceHostBootstrap.PrepareAsync in the consumer host."
                    : sdkUnavailable ? "The actual dotnet SDK version could not be resolved; execute fresh or repair the local SDK installation."
                    : decision.Diagnostic;
            var publication = decision.Publication switch
            {
                EvidenceReceiptPublication.Published => "published",
                EvidenceReceiptPublication.Suppressed => "suppressed",
                EvidenceReceiptPublication.Restored => "restored",
                _ => "not-published",
            };
            return new EvidenceReuseSummaryDecision(decision.ProducerId,
                missingSolution && decision.Status == EvidenceProducerReuseStatus.Ineligible ? "Ineligible" : decision.Status.ToString(),
                reason, diagnostic, decision.ProducerKey, decision.OriginCompletedAt,
            decision.OriginElapsedMilliseconds, publication);
        }).ToArray();

    internal static IReadOnlyDictionary<string, string> CreateToolIdentity(
        EvidenceProducerDeclaration producer, string repositoryRoot, string? solutionPath, string sdkVersion)
    {
        if (!string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new CoverageEvidenceRunSettings(producer.CoverageExecution);
        var solutionIdentity = "missing";
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var fullSolution = Path.GetFullPath(solutionPath);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            if (fullSolution.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                solutionIdentity = Path.GetRelativePath(fullRoot, fullSolution).Replace(Path.DirectorySeparatorChar, '/');
            else if (string.Equals(fullSolution, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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
            ["reportGeneratorManifest"] = GetReportGeneratorManifestDigest(),
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

    private static string GetReportGeneratorManifestDigest()
    {
        const string resourceName = "ForgeTrust.AppSurface.Evidence.Coverage.reportgenerator-runtime-manifest.json";
        var assembly = typeof(StrictReportGeneratorRuntimeLocator).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? assembly.GetManifestResourceNames().Where(static name => name.EndsWith("reportgenerator-runtime-manifest.json", StringComparison.Ordinal))
                .Select(assembly.GetManifestResourceStream).FirstOrDefault(static candidate => candidate is not null);
        return stream is null ? "unavailable" : "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool RequiresCoverageIdentity(EvidenceProducerDeclaration producer, string? solutionPath) =>
        string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
        && producer.Inputs is not null
        && producer.CoverageExecution is { CooperativeChildLifetime: true }
        && !string.IsNullOrWhiteSpace(solutionPath);

    private async Task<(CoverageProducerAdapter Adapter, EvidenceReuseProducerRegistration Registration)> CreateProducerSetupAsync(
        EvidenceProducerDeclaration producer, string repositoryRoot, string? solutionPath, string executionRoot,
        string? artifactRoot, EvidenceDiffSnapshot? diffSnapshot, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        var sdkVersion = RequiresCoverageIdentity(producer, solutionPath)
            ? await ReadDotnetSdkVersionAsync(solutionPath, cancellationToken, _sdkExecutable).ConfigureAwait(false)
            : "not-applicable";
        var adapter = new CoverageProducerAdapter(producer.Id, coverageProducer, solutionPath, executionRoot,
            finalReportRoot: artifactRoot, diffSnapshot: diffSnapshot, output: output, error: error,
            sdkVersion: sdkVersion, sdkExecutable: _sdkExecutable, repositoryRoot: repositoryRoot);
        var implementationIdentity = string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase)
            ? CoverageImplementationIdentity
            : "appsurface-cli-consumer-host-required-v1";
        var registration = new EvidenceReuseProducerRegistration(adapter, implementationIdentity,
            CreateToolIdentity(producer, repositoryRoot, solutionPath, sdkVersion));
        return (adapter, registration);
    }

    private static bool IsSolutionIdentitySafe(string repositoryRoot, string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath)) return false;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var fullSolution = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullSolution)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullSolution.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

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

    private static string FindRepositoryRoot(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, ".git")) || File.Exists(Path.Join(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        throw new EvidenceCliException("ASEVD218", "Local receipt preparation requires a Git worktree. Code: reuse.repository_required. No producer or resource was started.",
            "Run Evidence from the repository containing the declared inputs and selected solution.");
    }

    private sealed class CoverageProducerAdapter(
        string id, CoverageEvidenceProducer producer, string? solutionPath, string outputDirectory,
        string? finalReportRoot,
        EvidenceDiffSnapshot? diffSnapshot, TextWriter output, TextWriter error, string sdkVersion, string sdkExecutable,
        string repositoryRoot) : IEvidenceProducer, IEvidenceProducerKeyMaterial, IEvidenceProducerReceiptEligibility
    {
        private EvidenceProducerReuseSession? _session;
        private string? _receiptIneligibilityReason;
        public string Id { get; } = id;
        public string? ReceiptIneligibilityReason => Volatile.Read(ref _receiptIneligibilityReason);
        internal void SetSession(EvidenceProducerReuseSession session) => _session = session;

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            if (!string.Equals(context.Producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Unavailable, [],
                    "This producer requires an explicit consumer EvidenceHost registration; the CLI does not infer custom producer registrations.");
            if (string.IsNullOrWhiteSpace(solutionPath))
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Unavailable, [], "reuse.solution_required: The coverage producer requires --solution so it can reuse AppSurface coverage discovery. Register custom solution resolution through the consumer host.");
            var elapsed = Stopwatch.StartNew();
            var execution = await producer.RunWithInventoryAsync(context.Producer, solutionPath, outputDirectory, diffSnapshot,
                CoverageTextWriters.Create(output, error), cancellationToken, context.Artifacts,
                GetLifetime(), finalReportRoot).ConfigureAwait(false);
            Volatile.Write(ref _receiptIneligibilityReason, execution.InventoryIneligibilityReason);
            return execution.Result with { ElapsedMilliseconds = elapsed.ElapsedMilliseconds };
        }

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(context.Producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("reuse.consumer_host_required");
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new InvalidOperationException("reuse.solution_required");
            if (!IsSolutionIdentitySafe(repositoryRoot, solutionPath))
                throw new InvalidOperationException("reuse.solution_identity_unavailable");
            var currentSdkVersion = await ReadDotnetSdkVersionAsync(solutionPath, cancellationToken, sdkExecutable).ConfigureAwait(false);
            if (string.Equals(sdkVersion, "unavailable", StringComparison.Ordinal)
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

        internal EvidenceChildLifetimeSession? GetLifetime() => _session?.GetChildLifetime(Id);
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
