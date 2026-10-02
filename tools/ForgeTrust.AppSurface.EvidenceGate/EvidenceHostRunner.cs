using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>
/// Runs the base-owned AppSurface EvidenceHost registration set against one verified v2 plan.
/// </summary>
/// <remarks>
/// The initial registration set supports an empty targeted profile explicitly selected by the trusted policy.
/// It never starts commands or executes subject files. Other profiles receive an incomplete manifest
/// until every selected producer and resource has a safe first-party implementation registered here.
/// Revision and policy verification uses the trusted policy and Git object store supplied by the
/// controller; this host run does not issue a CI check verdict. A separate trusted verifier must
/// still validate job provenance and current event identity.
/// </remarks>
internal static class EvidenceHostRunner
{
    private const int MaximumInputBytes = 4 * 1024 * 1024;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private const int MaximumSummaryBytes = 128 * 1024;
    public static async Task<int> ExecuteAsync(
        string planPath,
        string policyPath,
        string repositoryPath,
        string outputDirectory,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        EvidencePlan plan;
        byte[] canonicalPlanBytes;
        EvidencePolicy? verifiedPolicy = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputRoot = Path.GetFullPath(outputDirectory);
            EnsureOutputTargetsAreNew(outputRoot);

            var planBytes = await ReadBoundedAsync(planPath, MaximumInputBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);
            }
            catch (JsonException)
            {
                await standardError.WriteLineAsync("ASEGH103: The plan is malformed; no trustworthy plan identity exists, so no manifest was emitted.").ConfigureAwait(false);
                return 2;
            }

            if (!HasBoundedManifestShape(plan))
            {
                await standardError.WriteLineAsync("ASEGH103: The plan has no bounded manifest identity; no manifest was emitted.").ConfigureAwait(false);
                return 2;
            }

            canonicalPlanBytes = EvidenceCanonicalJson.Serialize(plan);
            var planIsCanonical = canonicalPlanBytes.AsSpan().SequenceEqual(planBytes);
            string? invalidDiagnostic = planIsCanonical ? null : "ASEGH103";
            if (invalidDiagnostic is null)
            {
                try
                {
                    var policyBytes = await ReadBoundedAsync(policyPath, MaximumInputBytes, cancellationToken).ConfigureAwait(false);
                    // The policy is read from the trusted base checkout, whose checked-in JSON is
                    // formatted for review. Its parsed value is independently closed against Git.
                    verifiedPolicy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policyBytes);
                    EvidencePlanner.ValidateGatePolicy(verifiedPolicy);
                    await EvidenceRevisionPlanBuilder.VerifyAsync(
                        new EvidencePlanner(),
                        verifiedPolicy,
                        repositoryPath,
                        plan,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (EvidenceGateInputException exception)
                {
                    invalidDiagnostic = exception.Code;
                }
                catch (EvidencePlanningException exception)
                {
                    invalidDiagnostic = string.Equals(exception.Code, "ASEVD139", StringComparison.Ordinal)
                        ? "ASEGH104"
                        : exception.Code;
                }
                catch (JsonException)
                {
                    invalidDiagnostic = "ASEGH103";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsNonFatal(exception))
                {
                    invalidDiagnostic = "ASEGH105";
                }
            }

            var registrations = CreateFirstPartyRegistrations(repositoryPath);
            EvidenceManifest manifest;
            if (invalidDiagnostic is not null)
            {
                manifest = BuildInvalidManifest(plan);
            }
            else if (verifiedPolicy is not null && IsSupportedEmptyTargetedProfile(plan, verifiedPolicy))
            {
                await using var host = EvidenceHostBootstrap.Create(
                    plan,
                    registration => ConfigureFirstPartyRegistrations(registration, repositoryPath),
                    new EvidenceHostOptions(ArtifactDirectory: Path.Join(outputRoot, "artifacts")));
                manifest = await host.RunAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                manifest = BuildIncompleteManifest(plan);
            }

            if (invalidDiagnostic is null && !EvidenceManifestBuilder.Verify(plan, manifest))
            {
                manifest = BuildInvalidManifest(plan);
                invalidDiagnostic = "ASEGH105";
            }

            var hostCompleted = invalidDiagnostic is null
                && manifest.ExecutionVerdict == EvidenceExecutionVerdict.Passed
                && manifest.ClaimKind == EvidenceClaimKind.NoEvidenceRequired
                && manifest.Eligibility == EvidenceClaimEligibility.PullRequestGate
                && verifiedPolicy is not null
                && IsSupportedEmptyTargetedProfile(plan, verifiedPolicy);
            var diagnostic = invalidDiagnostic is not null
                ? invalidDiagnostic + ": The plan failed trusted host-input validation; no successful evidence claim was issued."
                : hostCompleted ? null : GetIncompleteDiagnostic(plan, registrations);
            await WriteOutputsAsync(outputRoot, canonicalPlanBytes, manifest, plan, diagnostic, hostCompleted, CancellationToken.None).ConfigureAwait(false);

            var status = hostCompleted ? "completed" : manifest.ExecutionVerdict.ToString().ToLowerInvariant();
            await standardOutput.WriteLineAsync($"host={status}; claim={manifest.ClaimKind}; profile={SafeToken(plan.Profile.Id)}; plan={SafeDigest(plan.PlanDigest)}").ConfigureAwait(false);
            if (diagnostic is not null)
            {
                await standardError.WriteLineAsync(diagnostic).ConfigureAwait(false);
            }

            return hostCompleted ? 0 : 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("ASEGH130: EvidenceHost execution was cancelled; no complete host result was issued.").ConfigureAwait(false);
            return 130;
        }
        catch (EvidenceGateInputException exception)
        {
            var detail = exception.Code == "ASEGH107"
                ? "Evidence input exceeded the 4 MiB limit; no trustworthy plan identity exists, so no manifest was emitted."
                : exception.Message;
            await standardError.WriteLineAsync($"{exception.Code}: {detail}").ConfigureAwait(false);
            return 2;
        }
        catch (JsonException)
        {
            await standardError.WriteLineAsync("ASEGH103: An Evidence input is malformed; no trustworthy plan identity exists, so no manifest was emitted.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            await standardError.WriteLineAsync("ASEGH102: Evidence input or output could not be processed safely.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException)
        {
            await standardError.WriteLineAsync("ASEGH199: Evidence execution failed; no successful claim was issued.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static EvidenceHostRegistration CreateFirstPartyRegistrations(string repositoryRoot)
    {
        var registrations = new EvidenceHostRegistration();
        ConfigureFirstPartyRegistrations(registrations, repositoryRoot);
        return registrations;
    }

    private static void ConfigureFirstPartyRegistrations(EvidenceHostRegistration registrations, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        registrations.AddProducer(new ProtectedReleaseEvidenceProducer(
            repositoryRoot,
            new UnavailableProtectedReleaseInvocationProvider()));
    }

    private static bool IsSupportedEmptyProfile(EvidencePlan plan) =>
        plan.Profile.Scope == EvidenceProfileScope.Targeted
            && plan.Profile.Resources.Count == 0
            && plan.Profile.Producers.Count == 0
            && plan.Profile.Obligations.Count == 0;

    private static bool IsSupportedEmptyTargetedProfile(EvidencePlan plan, EvidencePolicy trustedPolicy)
    {
        if (!IsSupportedEmptyProfile(plan))
        {
            return false;
        }

        var policyProfile = trustedPolicy.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, plan.Profile.Id, StringComparison.Ordinal));
        var selectedByExplicitRule = plan.MatchedRuleIds.Any(ruleId => trustedPolicy.Rules.Any(rule =>
            string.Equals(rule.Id, ruleId, StringComparison.Ordinal)
                && string.Equals(rule.ProfileId, plan.Profile.Id, StringComparison.Ordinal)));
        return policyProfile is not null
            && policyProfile.Scope == EvidenceProfileScope.Targeted
            && policyProfile.Resources.Count == 0
            && policyProfile.Producers.Count == 0
            && policyProfile.Obligations.Count == 0
            && selectedByExplicitRule;
    }

    private static bool HasBoundedManifestShape(EvidencePlan plan) =>
        !string.IsNullOrWhiteSpace(plan.ContractVersion)
            && plan.ContractVersion.Length <= 16
            && !string.IsNullOrWhiteSpace(plan.PlanDigest)
            && plan.PlanDigest.Length <= 128
            && plan.Profile is not null
            && !string.IsNullOrWhiteSpace(plan.Profile.Id)
            && plan.Profile.Id.Length <= 128
            && plan.Profile.Resources is not null
            && plan.Profile.Producers is not null
            && plan.Profile.Obligations is not null
            && plan.ChangedPaths is not null
            && plan.MatchedRuleIds is not null
            && plan.Profile.Resources.Count <= EvidenceProfileLimits.MaximumResources
            && plan.Profile.Producers.Count <= EvidenceProfileLimits.MaximumProducers
            && plan.Profile.Obligations.Count <= EvidenceProfileLimits.MaximumObligations
            && plan.ChangedPaths.Count <= 10_000
            && plan.Profile.Resources.All(static resource => resource is not null && !string.IsNullOrWhiteSpace(resource.Id) && resource.Id.Length <= 128)
            && plan.Profile.Producers.All(static producer => producer is not null && !string.IsNullOrWhiteSpace(producer.Id) && producer.Id.Length <= 128)
            && plan.Profile.Obligations.All(static obligation => obligation is not null && !string.IsNullOrWhiteSpace(obligation.Id) && obligation.Id.Length <= 128);

    private static EvidenceManifest BuildIncompleteManifest(EvidencePlan plan)
    {
        var producerResults = plan.Profile.Producers.Select(producer => new EvidenceProducerResult(
            producer.Id,
            EvidenceProducerOutcome.Unavailable,
            [],
            "No first-party EvidenceHost registration is available for this AppSurface producer.")).ToArray();
        var resourceResults = plan.Profile.Resources.Select(resource => new EvidenceResourceResult(
            resource.Id,
            EvidenceResourceOutcome.Unavailable,
            0,
            "No first-party EvidenceHost registration is available for this AppSurface resource.")).ToArray();

        return BuildTerminalManifest(
            plan,
            EvidenceExecutionVerdict.Incomplete,
            plan.Profile.Scope == EvidenceProfileScope.Release ? EvidenceEnvelopeStatus.Unavailable : EvidenceEnvelopeStatus.NotRequired,
            resourceResults,
            producerResults);
    }

    private static EvidenceManifest BuildInvalidManifest(EvidencePlan plan)
    {
        var producerResults = plan.Profile.Producers.Select(producer => new EvidenceProducerResult(
            producer.Id,
            EvidenceProducerOutcome.Invalid,
            [],
            "The supplied plan failed trusted validation; this producer was not run.")).ToArray();
        var resourceResults = plan.Profile.Resources.Select(resource => new EvidenceResourceResult(
            resource.Id,
            EvidenceResourceOutcome.Invalid,
            0,
            "The supplied plan failed trusted validation; this resource was not provisioned.")).ToArray();
        return BuildTerminalManifest(
            plan,
            EvidenceExecutionVerdict.Invalid,
            plan.Profile.Scope == EvidenceProfileScope.Release ? EvidenceEnvelopeStatus.Invalid : EvidenceEnvelopeStatus.NotRequired,
            resourceResults,
            producerResults);
    }

    private static EvidenceManifest BuildTerminalManifest(
        EvidencePlan plan,
        EvidenceExecutionVerdict verdict,
        EvidenceEnvelopeStatus envelopeStatus,
        IReadOnlyList<EvidenceResourceResult> resourceResults,
        IReadOnlyList<EvidenceProducerResult> producerResults)
    {
        var draft = new EvidenceManifest(
            plan.ContractVersion,
            plan.PlanDigest,
            verdict,
            EvidenceClaimKind.None,
            EvidenceClaimEligibility.None,
            envelopeStatus,
            resourceResults.OrderBy(static result => result.ResourceId, StringComparer.Ordinal).ToArray(),
            plan.Profile.Obligations.Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            [],
            plan.Profile.Obligations.Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            producerResults.OrderBy(static result => result.ProducerId, StringComparer.Ordinal).ToArray(),
            new EvidenceExecutionMetrics(CleanupCompleted: true),
            string.Empty,
            plan.BaseRevision,
            plan.HeadRevision,
            plan.SourceDiffDigest,
            plan.NameStatusDigest,
            plan.PullRequestRunIdentity);
        return draft with { ManifestDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    private static string GetIncompleteDiagnostic(EvidencePlan plan, EvidenceHostRegistration registrations)
    {
        var hasMissingProducer = plan.Profile.Producers.Any(producer => !registrations.Producers.ContainsKey(producer.Id));
        var hasMissingResource = plan.Profile.Resources.Any(resource => !registrations.Resources.ContainsKey(resource.Id));
        if (hasMissingProducer || hasMissingResource)
        {
            return "ASEGH201: A selected typed producer or resource lacks an explicit first-party registration; the manifest is incomplete.";
        }

        return "ASEGH202: This runner supports only an empty targeted profile explicitly selected by the validated trusted policy; the manifest is not gate eligible.";
    }

    private static async Task WriteOutputsAsync(
        string outputRoot,
        byte[] planBytes,
        EvidenceManifest manifest,
        EvidencePlan plan,
        string? diagnostic,
        bool hostCompleted,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        var summaryBytes = EvidenceCanonicalJson.Serialize(new
        {
            claim = manifest.ClaimKind.ToString(),
            diagnostic,
            eligibility = manifest.Eligibility.ToString(),
            executionVerdict = manifest.ExecutionVerdict.ToString(),
            manifestDigest = manifest.ManifestDigest,
            planDigest = plan.PlanDigest,
            profile = SafeToken(plan.Profile.Id),
            hostStatus = hostCompleted ? "completed" : manifest.ExecutionVerdict.ToString(),
            contractVersion = plan.ContractVersion,
        });
        if (planBytes.Length > MaximumOutputBytes || manifestBytes.Length > MaximumOutputBytes || summaryBytes.Length > MaximumSummaryBytes)
        {
            throw new EvidenceGateInputException("ASEGH106", "Evidence output exceeded the configured size limit.");
        }

        await WriteNewAsync(Path.Join(outputRoot, "evidence-plan.json"), planBytes, cancellationToken).ConfigureAwait(false);
        await WriteNewAsync(Path.Join(outputRoot, "evidence-manifest.json"), manifestBytes, cancellationToken).ConfigureAwait(false);
        await WriteNewAsync(Path.Join(outputRoot, "evidence-summary.json"), summaryBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new EvidenceGateInputException("ASEGH107", "Evidence input exceeded the configured size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            throw new EvidenceGateInputException("ASEGH102", "A required Evidence input is unavailable.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new EvidenceGateInputException("ASEGH102", "A required Evidence input is unavailable.");
        }

        return output.ToArray();
    }

    private static void EnsureOutputTargetsAreNew(string outputRoot)
    {
        foreach (var fileName in new[] { "evidence-plan.json", "evidence-manifest.json", "evidence-summary.json" })
        {
            if (File.Exists(Path.Join(outputRoot, fileName)) || Directory.Exists(Path.Join(outputRoot, fileName)))
            {
                throw new EvidenceGateInputException("ASEGH108", "The output directory already contains an Evidence result file.");
            }
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SafeToken(string value) => value.Length <= 128
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : "[invalid-token]";

    private static string SafeDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit)
        ? value
        : "[invalid-digest]";

    private static bool IsNonFatal(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException
        and not AppDomainUnloadedException;

    private sealed class EvidenceGateInputException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
