using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>
/// Resolves a checked-in policy and normalized diff into one closed evidence plan.
/// </summary>
public sealed class EvidencePlanner
{
    /// <summary>
    /// Resolves one deterministic plan without starting resources or producers.
    /// </summary>
    /// <param name="policy">Versioned consumer policy.</param>
    /// <param name="changedPaths">Explicit normalized changed paths.</param>
    /// <returns>A hash-bound evidence plan.</returns>
    public EvidencePlan Resolve(EvidencePolicy policy, IReadOnlyList<NormalizedDiffPath> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(changedPaths);

        ValidatePolicy(policy);
        return ResolveValidated(policy, changedPaths);
    }

    /// <summary>
    /// Resolves a plan for a pull-request gate after verifying that mixed targeted profiles
    /// cannot lose requirements when selection falls back to the conservative profile.
    /// </summary>
    /// <param name="policy">Versioned consumer policy used by the trusted gate.</param>
    /// <param name="changedPaths">Explicit normalized changed paths, including both sides of renames.</param>
    /// <returns>A hash-bound evidence plan whose mixed-profile fallback is validated as a superset.</returns>
    /// <remarks>
    /// This is an opt-in gate API. <see cref="Resolve(EvidencePolicy, IReadOnlyList{NormalizedDiffPath})"/>
    /// and <see cref="ValidatePolicy(EvidencePolicy)"/> retain their local v1 behavior and do not
    /// enforce cross-profile closure.
    /// </remarks>
    public EvidencePlan ResolveForGate(EvidencePolicy policy, IReadOnlyList<NormalizedDiffPath> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(changedPaths);

        ValidateGatePolicy(policy);
        return ResolveValidated(policy, changedPaths);
    }

    private static EvidencePlan ResolveValidated(EvidencePolicy policy, IReadOnlyList<NormalizedDiffPath> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            throw new EvidencePlanningException(
                "ASEVD101",
                "Evidence planning requires at least one normalized changed path.",
                "Supply explicit --path input or a unified diff containing changed files.");
        }

        var profiles = policy.Profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);
        var selected = new List<(EvidenceProfile Profile, string RuleId)>();

        foreach (var changedPath in changedPaths)
        {
            ValidatePath(changedPath.Path);
            selected.Add(ResolvePath(policy, profiles, changedPath.Path));
            if (!string.IsNullOrWhiteSpace(changedPath.PreviousPath))
            {
                ValidatePath(changedPath.PreviousPath);
                selected.Add(ResolvePath(policy, profiles, changedPath.PreviousPath));
            }
        }

        var distinctProfiles = selected
            .Select(static result => result.Profile)
            .DistinctBy(static profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
        var conservativeFallback = distinctProfiles.Length != 1;
        var profile = conservativeFallback
            ? profiles[policy.ConservativeProfileId]
            : distinctProfiles[0];
        var policyDigest = EvidenceDigest.CanonicalSha256(policy);
        var normalizedPaths = changedPaths
            .Select(NormalizePath)
            .OrderBy(static path => path.Path, StringComparer.Ordinal)
            .ThenBy(static path => path.PreviousPath, StringComparer.Ordinal)
            .ToArray();
        var diffDigest = EvidenceDigest.CanonicalSha256(normalizedPaths);
        var matchedRuleIds = selected.Select(static result => result.RuleId);
        if (conservativeFallback)
        {
            matchedRuleIds = matchedRuleIds.Append($"conservative:{policy.ConservativeProfileId}");
        }

        var draft = new EvidencePlan(
            ContractVersion: "1.0",
            PolicyId: policy.Id,
            PolicyDigest: policyDigest,
            DiffDigest: diffDigest,
            Profile: profile,
            ChangedPaths: normalizedPaths,
            MatchedRuleIds: matchedRuleIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray(),
            PlanDigest: string.Empty,
            PolicySnapshot: policy);

        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    /// <summary>
    /// Validates that a policy can yield a conservative and truthful v1 plan.
    /// </summary>
    /// <param name="policy">Policy to validate.</param>
    public static void ValidatePolicy(EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RequireIdentifier(policy.Id, "policy id");
        RequireIdentifier(policy.Version, "policy version");
        RequireIdentifier(policy.ConservativeProfileId, "conservative profile id");
        if (policy.Profiles.Count == 0)
        {
            throw new EvidencePlanningException("ASEVD102", "Evidence policy declares no profiles.", "Declare at least one closed profile.");
        }

        var duplicateProfile = policy.Profiles.GroupBy(static profile => profile.Id, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicateProfile is not null)
        {
            throw new EvidencePlanningException("ASEVD103", $"Evidence policy declares profile '{duplicateProfile.Key}' more than once.", "Use stable unique profile ids.");
        }

        var profiles = policy.Profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);
        if (!profiles.TryGetValue(policy.ConservativeProfileId, out var conservativeProfile))
        {
            throw new EvidencePlanningException("ASEVD104", $"Conservative profile '{policy.ConservativeProfileId}' is not declared.", "Declare the conservative profile in the policy.");
        }

        if (IsEmptyProfile(conservativeProfile))
        {
            throw new EvidencePlanningException("ASEVD105", "The conservative profile cannot be empty.", "Use a profile with explicit evidence requirements for unmatched paths.");
        }

        foreach (var profile in policy.Profiles)
        {
            RequireIdentifier(profile.Id, "profile id");
            ValidateProfile(profile);
        }

        foreach (var rule in policy.Rules)
        {
            RequireIdentifier(rule.Id, "rule id");
            RequireIdentifier(rule.Pattern, "rule pattern");
            if (!profiles.ContainsKey(rule.ProfileId))
            {
                throw new EvidencePlanningException("ASEVD106", $"Rule '{rule.Id}' references unknown profile '{rule.ProfileId}'.", "Declare the profile or correct the rule.");
            }
        }
    }

    /// <summary>
    /// Validates that a policy's conservative fallback preserves every requirement from each
    /// rule-selected targeted profile that may participate in a mixed pull-request diff.
    /// </summary>
    /// <param name="policy">Policy to validate before using it for a pull-request gate.</param>
    /// <remarks>
    /// The check includes resources and their readiness declarations, producers and their resource
    /// wiring, assertions, artifact slots, and obligations. Additional conservative requirements
    /// are allowed. Targeted profiles with no evidence requirements, such as an explicit docs-only
    /// profile, are valid and contribute no requirements to the superset check. Release profiles
    /// are outside this pull-request check and need their own event and eligibility validation.
    /// Existing local v1 callers may continue to use <see cref="ValidatePolicy(EvidencePolicy)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="EvidencePlanningException">The policy is invalid or its conservative profile
    /// omits or weakens a targeted profile requirement.</exception>
    public static void ValidateGatePolicy(EvidencePolicy policy)
    {
        ValidatePolicy(policy);

        var profiles = policy.Profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);
        var conservativeProfile = profiles[policy.ConservativeProfileId];
        if (conservativeProfile.Scope != EvidenceProfileScope.Targeted)
        {
            throw new EvidencePlanningException(
                "ASEVD129",
                $"Conservative profile '{conservativeProfile.Id}' is not a targeted pull-request profile.",
                "Use a targeted PR policy and validate release profiles only in the protected release context.");
        }

        var releaseRule = policy.Rules.FirstOrDefault(rule => profiles[rule.ProfileId].Scope != EvidenceProfileScope.Targeted);
        if (releaseRule is not null)
        {
            throw new EvidencePlanningException(
                "ASEVD129",
                $"Pull-request gate rule '{releaseRule.Id}' selects non-targeted profile '{releaseRule.ProfileId}'.",
                "Map pull-request changes to targeted profiles; select release profiles only from the validated protected release context.");
        }

        var targetedProfiles = policy.Rules
            .Select(rule => profiles[rule.ProfileId])
            .DistinctBy(static profile => profile.Id, StringComparer.Ordinal)
            .Where(profile => !string.Equals(profile.Id, conservativeProfile.Id, StringComparison.Ordinal))
            .OrderBy(static profile => profile.Id, StringComparer.Ordinal);

        foreach (var targetedProfile in targetedProfiles)
        {
            ValidateConservativeProfileSuperset(conservativeProfile, targetedProfile);
        }
    }

    private static void ValidateConservativeProfileSuperset(EvidenceProfile conservativeProfile, EvidenceProfile targetedProfile)
    {
        var conservativeResources = conservativeProfile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
        foreach (var requiredResource in targetedProfile.Resources.OrderBy(static resource => resource.Id, StringComparer.Ordinal))
        {
            if (!conservativeResources.TryGetValue(requiredResource.Id, out var candidateResource))
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"resource '{requiredResource.Id}'",
                    "the declaration is missing");
            }

            if (!string.Equals(candidateResource.Readiness, requiredResource.Readiness, StringComparison.Ordinal)
                || candidateResource.DeadlineSeconds > requiredResource.DeadlineSeconds)
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"resource '{requiredResource.Id}'",
                    "the readiness mode or deadline is weaker than the targeted declaration");
            }

            var missingDependency = requiredResource.Requires
                .Where(dependencyId => !candidateResource.Requires.Contains(dependencyId, StringComparer.Ordinal))
                .OrderBy(static dependencyId => dependencyId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (missingDependency is not null)
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"resource dependency '{missingDependency}' for resource '{requiredResource.Id}'",
                    "the dependency is missing");
            }
        }

        var conservativeProducers = conservativeProfile.Producers.ToDictionary(static producer => producer.Id, StringComparer.Ordinal);
        foreach (var requiredProducer in targetedProfile.Producers.OrderBy(static producer => producer.Id, StringComparer.Ordinal))
        {
            if (!conservativeProducers.TryGetValue(requiredProducer.Id, out var candidateProducer))
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"producer '{requiredProducer.Id}'",
                    "the declaration is missing");
            }

            if (!string.Equals(candidateProducer.Kind, requiredProducer.Kind, StringComparison.Ordinal)
                || !string.Equals(candidateProducer.Version, requiredProducer.Version, StringComparison.Ordinal)
                || candidateProducer.TimeoutSeconds > requiredProducer.TimeoutSeconds
                || !PreservesCoverageGate(candidateProducer.CoverageGate, requiredProducer.CoverageGate))
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"producer '{requiredProducer.Id}'",
                    "the kind, version, deadline, or coverage gate does not preserve the targeted declaration");
            }

            var missingResource = requiredProducer.RequiredResources
                .Where(resourceId => !candidateProducer.RequiredResources.Contains(resourceId, StringComparer.Ordinal))
                .OrderBy(static resourceId => resourceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (missingResource is not null)
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"resource '{missingResource}' required by producer '{requiredProducer.Id}'",
                    "the producer no longer requires it");
            }

            var missingAssertion = requiredProducer.AssertionIds
                .Where(assertionId => !candidateProducer.AssertionIds.Contains(assertionId, StringComparer.Ordinal))
                .OrderBy(static assertionId => assertionId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (missingAssertion is not null)
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"assertion '{missingAssertion}' on producer '{requiredProducer.Id}'",
                    "the assertion is missing");
            }

            var conservativeArtifactSlots = candidateProducer.ArtifactSlots.ToDictionary(static slot => slot.LogicalName, StringComparer.Ordinal);
            foreach (var requiredSlot in requiredProducer.ArtifactSlots.OrderBy(static slot => slot.LogicalName, StringComparer.Ordinal))
            {
                if (!conservativeArtifactSlots.TryGetValue(requiredSlot.LogicalName, out var candidateSlot))
                {
                    throw GatePolicyValidationFailure(
                        conservativeProfile,
                        targetedProfile,
                        $"artifact slot '{requiredProducer.Id}/{requiredSlot.LogicalName}'",
                        "the declaration is missing");
                }

                if (!string.Equals(candidateSlot.RelativeRoot, requiredSlot.RelativeRoot, StringComparison.Ordinal)
                    || !string.Equals(candidateSlot.MediaType, requiredSlot.MediaType, StringComparison.Ordinal)
                    || (requiredSlot.Required && !candidateSlot.Required)
                    || candidateSlot.MaximumBytes > requiredSlot.MaximumBytes)
                {
                    throw GatePolicyValidationFailure(
                        conservativeProfile,
                        targetedProfile,
                        $"artifact slot '{requiredProducer.Id}/{requiredSlot.LogicalName}'",
                        "its root, media type, required flag, or byte limit weakens the targeted declaration");
                }
            }
        }

        var conservativeObligations = conservativeProfile.Obligations.ToDictionary(static obligation => obligation.Id, StringComparer.Ordinal);
        foreach (var requiredObligation in targetedProfile.Obligations.OrderBy(static obligation => obligation.Id, StringComparer.Ordinal))
        {
            if (!conservativeObligations.TryGetValue(requiredObligation.Id, out var candidateObligation))
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"obligation '{requiredObligation.Id}'",
                    "the declaration is missing");
            }

            if (!string.Equals(candidateObligation.RiskClass, requiredObligation.RiskClass, StringComparison.Ordinal)
                || !string.Equals(candidateObligation.Rationale, requiredObligation.Rationale, StringComparison.Ordinal)
                || !string.Equals(candidateObligation.RequiredAssertionId, requiredObligation.RequiredAssertionId, StringComparison.Ordinal))
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"obligation '{requiredObligation.Id}'",
                    "its risk class, rationale, or required assertion does not preserve the targeted declaration");
            }

            var missingProducer = requiredObligation.RequiredProducerIds
                .Where(producerId => !candidateObligation.RequiredProducerIds.Contains(producerId, StringComparer.Ordinal))
                .OrderBy(static producerId => producerId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (missingProducer is not null)
            {
                throw GatePolicyValidationFailure(
                    conservativeProfile,
                    targetedProfile,
                    $"producer '{missingProducer}' required by obligation '{requiredObligation.Id}'",
                    "the obligation no longer requires it");
            }
        }
    }

    private static EvidencePlanningException GatePolicyValidationFailure(
        EvidenceProfile conservativeProfile,
        EvidenceProfile targetedProfile,
        string requirement,
        string detail) =>
        new(
            "ASEVD129",
            $"Conservative profile '{conservativeProfile.Id}' does not preserve {requirement} required by targeted profile '{targetedProfile.Id}': {detail}.",
            "Add the missing or equivalent requirement to the conservative profile, or use an explicit combined profile for the pull-request paths.");

    private static bool PreservesCoverageGate(
        EvidenceCoverageGateRequirements? candidate,
        EvidenceCoverageGateRequirements? required)
    {
        if (required is null)
        {
            return true;
        }

        return candidate is not null
            && candidate.MinLinePercent >= required.MinLinePercent
            && candidate.MinBranchPercent >= required.MinBranchPercent
            && OptionalThresholdIsPreserved(candidate.MinPatchLinePercent, required.MinPatchLinePercent)
            && OptionalThresholdIsPreserved(candidate.MinPatchBranchPercent, required.MinPatchBranchPercent)
            && string.Equals(candidate.PatchLineMode, required.PatchLineMode, StringComparison.OrdinalIgnoreCase)
            && candidate.TolerancePercent <= required.TolerancePercent;
    }

    private static bool OptionalThresholdIsPreserved(decimal? candidate, decimal? required) =>
        required is null || (candidate is { } candidateValue && candidateValue >= required.Value);

    private static void ValidateProfile(EvidenceProfile profile)
    {
        if (profile.Resources.Count > EvidenceProfileLimits.MaximumResources
            || profile.Producers.Count > EvidenceProfileLimits.MaximumProducers
            || profile.Obligations.Count > EvidenceProfileLimits.MaximumObligations)
        {
            throw new EvidencePlanningException("ASEVD122", $"Profile '{profile.Id}' exceeds v1 EvidenceHost declaration limits.", "Split the profile into bounded evidence obligations.");
        }

        var duplicateProducer = profile.Producers
            .GroupBy(static producer => producer.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateProducer is not null)
        {
            throw new EvidencePlanningException("ASEVD107", $"Profile '{profile.Id}' declares producer '{duplicateProducer.Key}' more than once.", "Use one declaration for each producer id.");
        }

        var producers = profile.Producers.ToDictionary(static producer => producer.Id, StringComparer.Ordinal);
        var duplicateResource = profile.Resources
            .GroupBy(static resource => resource.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateResource is not null)
        {
            throw new EvidencePlanningException("ASEVD108", $"Profile '{profile.Id}' declares resource '{duplicateResource.Key}' more than once.", "Use one declaration for each resource id.");
        }

        var resources = profile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
        foreach (var resource in profile.Resources)
        {
            RequireIdentifier(resource.Id, "resource id");
            if (!string.Equals(resource.Readiness, "aspire_health", StringComparison.Ordinal)
                && !string.Equals(resource.Readiness, "completion", StringComparison.Ordinal))
            {
                throw new EvidencePlanningException("ASEVD123", $"Resource '{resource.Id}' has unsupported readiness '{resource.Readiness}'.", "Use aspire_health or completion in the v1 policy.");
            }

            if (resource.DeadlineSeconds <= 0)
            {
                throw new EvidencePlanningException("ASEVD109", $"Resource '{resource.Id}' has a non-positive readiness deadline.", "Set deadlineSeconds to a positive value.");
            }

            foreach (var dependencyId in resource.Requires)
            {
                if (!resources.ContainsKey(dependencyId))
                {
                    throw new EvidencePlanningException("ASEVD124", $"Resource '{resource.Id}' requires undeclared resource '{dependencyId}'.", "Declare every required resource in the same profile.");
                }
            }
        }

        foreach (var producer in profile.Producers)
        {
            RequireIdentifier(producer.Id, "producer id");
            RequireIdentifier(producer.Kind, "producer kind");
            if (producer.TimeoutSeconds <= 0)
            {
                throw new EvidencePlanningException("ASEVD110", $"Producer '{producer.Id}' has a non-positive timeout.", "Set timeoutSeconds to a positive value.");
            }

            if (producer.ArtifactSlots.Count > 128
                || producer.ArtifactSlots.GroupBy(static slot => slot.LogicalName, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            {
                throw new EvidencePlanningException("ASEVD125", $"Producer '{producer.Id}' has duplicate or too many artifact slots.", "Declare at most 128 uniquely named artifact slots.");
            }

            foreach (var slot in producer.ArtifactSlots)
            {
                RequireIdentifier(slot.LogicalName, "artifact logical name");
                if (slot.MaximumBytes < 0 || string.IsNullOrWhiteSpace(slot.MediaType))
                {
                    throw new EvidencePlanningException("ASEVD126", $"Producer '{producer.Id}' has an invalid artifact slot '{slot.LogicalName}'.", "Use a media type and a non-negative maximum byte count.");
                }

                try
                {
                    EvidenceArtifactValidation.NormalizeRelativePath(slot.RelativeRoot);
                }
                catch (ArgumentException)
                {
                    throw new EvidencePlanningException("ASEVD126", $"Producer '{producer.Id}' artifact root '{slot.RelativeRoot}' is invalid.", "Use a normalized relative artifact root.");
                }
            }

            if (producer.CoverageGate is { } coverageGate)
            {
                ValidateCoverageGate(producer.Id, coverageGate);
            }

            foreach (var resourceId in producer.RequiredResources)
            {
                if (!resources.ContainsKey(resourceId))
                {
                    throw new EvidencePlanningException("ASEVD111", $"Producer '{producer.Id}' requires undeclared resource '{resourceId}'.", "Declare the resource in the same profile.");
                }
            }
        }

        var duplicateObligation = profile.Obligations
            .GroupBy(static obligation => obligation.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateObligation is not null)
        {
            throw new EvidencePlanningException("ASEVD112", $"Profile '{profile.Id}' declares obligation '{duplicateObligation.Key}' more than once.", "Use one declaration for each obligation id.");
        }

        var obligations = profile.Obligations.ToDictionary(static obligation => obligation.Id, StringComparer.Ordinal);
        foreach (var obligation in profile.Obligations)
        {
            RequireIdentifier(obligation.Id, "obligation id");
            RequireIdentifier(obligation.RequiredAssertionId, "required assertion id");
            if (obligation.RequiredProducerIds.Count == 0)
            {
                throw new EvidencePlanningException("ASEVD113", $"Obligation '{obligation.Id}' has no required producers.", "Declare at least one producer that can close the obligation.");
            }

            foreach (var producerId in obligation.RequiredProducerIds)
            {
                if (!producers.TryGetValue(producerId, out var producer))
                {
                    throw new EvidencePlanningException("ASEVD114", $"Obligation '{obligation.Id}' requires unknown producer '{producerId}'.", "Declare the producer in the same profile.");
                }

                if (!producer.AssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal))
                {
                    throw new EvidencePlanningException("ASEVD115", $"Producer '{producerId}' cannot close assertion '{obligation.RequiredAssertionId}'.", "Declare the assertion on the producer or select the correct producer.");
                }
            }
        }

        if (IsEmptyProfile(profile) && profile.Scope != EvidenceProfileScope.Targeted)
        {
            throw new EvidencePlanningException("ASEVD116", $"Release profile '{profile.Id}' cannot be empty.", "Use a targeted no-evidence profile only for explicit low-risk rules.");
        }
    }

    private static (EvidenceProfile Profile, string RuleId) ResolvePath(
        EvidencePolicy policy,
        IReadOnlyDictionary<string, EvidenceProfile> profiles,
        string path)
    {
        var candidates = policy.Rules
            .Where(rule => PathPattern.TryMatch(rule.Pattern, path, out _))
            .Select(rule => new RuleCandidate(rule, PathPattern.GetSpecificity(rule.Pattern)))
            .OrderByDescending(static candidate => candidate.Specificity)
            .ThenByDescending(static candidate => candidate.Rule.Precedence)
            .ToArray();
        if (candidates.Length == 0)
        {
            return (profiles[policy.ConservativeProfileId], $"conservative:{policy.ConservativeProfileId}");
        }

        var best = candidates[0];
        var ambiguous = candidates.Skip(1).FirstOrDefault(candidate =>
            candidate.Specificity == best.Specificity
            && candidate.Rule.Precedence == best.Rule.Precedence
            && !string.Equals(candidate.Rule.ProfileId, best.Rule.ProfileId, StringComparison.Ordinal));
        if (ambiguous is not null)
        {
            throw new EvidencePlanningException(
                "ASEVD117",
                $"Path '{path}' matches equally specific rules '{best.Rule.Id}' and '{ambiguous.Rule.Id}'.",
                "Declare different precedence values or use a single explicit combined profile.");
        }

        return (profiles[best.Rule.ProfileId], best.Rule.Id);
    }

    private static bool IsEmptyProfile(EvidenceProfile profile) =>
        profile.Resources.Count == 0 && profile.Producers.Count == 0 && profile.Obligations.Count == 0;

    private static NormalizedDiffPath NormalizePath(NormalizedDiffPath path) => path with
    {
        Path = NormalizePathValue(path.Path),
        PreviousPath = string.IsNullOrWhiteSpace(path.PreviousPath) ? null : NormalizePathValue(path.PreviousPath),
        Kind = string.IsNullOrWhiteSpace(path.Kind) ? "modified" : path.Kind.Trim().ToLowerInvariant(),
    };

    private static void ValidatePath(string path)
    {
        if (!string.Equals(path, NormalizePathValue(path), StringComparison.Ordinal))
        {
            throw new EvidencePlanningException("ASEVD118", $"Path '{path}' is not normalized.", "Use a repository-relative forward-slash path without '.' or '..' segments.");
        }
    }

    private static string NormalizePathValue(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new EvidencePlanningException("ASEVD119", "Evidence path is empty.", "Supply a repository-relative changed path.");
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized)
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.Split('/').Any(static segment => segment is "." or ".."))
        {
            throw new EvidencePlanningException("ASEVD120", $"Path '{path}' escapes the repository-relative evidence boundary.", "Use a normalized relative path.");
        }

        return normalized;
    }

    private static void RequireIdentifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new EvidencePlanningException("ASEVD121", $"Evidence {description} is empty or exceeds 128 characters.", "Supply a stable identifier up to 128 characters.");
        }
    }

    private static void ValidateCoverageGate(string producerId, EvidenceCoverageGateRequirements coverageGate)
    {
        if (!IsPercentage(coverageGate.MinLinePercent)
            || !IsPercentage(coverageGate.MinBranchPercent)
            || !IsPercentage(coverageGate.TolerancePercent)
            || (coverageGate.MinPatchLinePercent is { } patchLine && !IsPercentage(patchLine))
            || (coverageGate.MinPatchBranchPercent is { } patchBranch && !IsPercentage(patchBranch))
            || (!string.Equals(coverageGate.PatchLineMode, "measurable", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(coverageGate.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase)))
        {
            throw new EvidencePlanningException("ASEVD127", $"Coverage producer '{producerId}' has invalid coverageGate requirements.", "Use percentages from 0 through 100 and a measurable or codecov patchLineMode.");
        }
    }

    private static bool IsPercentage(decimal value) => value is >= 0 and <= 100;

    private sealed record RuleCandidate(EvidencePolicyRule Rule, int Specificity);
}

/// <summary>
/// Reads explicit changed paths from a unified diff without depending on a local Git checkout.
/// </summary>
public static class EvidenceUnifiedDiffReader
{
    /// <summary>
    /// Parses changed paths from unified diff text.
    /// </summary>
    /// <param name="diff">Unified diff text.</param>
    /// <returns>Normalized added, modified, deleted, and renamed paths.</returns>
    public static IReadOnlyList<NormalizedDiffPath> Read(string diff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diff);

        var paths = new List<NormalizedDiffPath>();
        string? oldPath = null;
        string? renamedFrom = null;
        var inHunk = false;
        var suppressHeaderPair = false;
        var sawGitHeader = false;
        var sawHunkHeader = false;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                sawGitHeader = true;
                oldPath = null;
                renamedFrom = null;
                inHunk = false;
                suppressHeaderPair = false;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                sawHunkHeader = true;
                inHunk = true;
                continue;
            }

            if (inHunk)
            {
                continue;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                renamedFrom = ReadDiffPath(line[12..]);
                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                var renamedTo = ReadDiffPath(line[10..]);
                if (renamedFrom is not null && renamedTo is not null)
                {
                    paths.Add(new NormalizedDiffPath(renamedTo, "renamed", renamedFrom));
                    suppressHeaderPair = true;
                }

                renamedFrom = null;
                continue;
            }

            if (!suppressHeaderPair && line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = ReadDiffPath(line[4..]);
                continue;
            }

            if (suppressHeaderPair || !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                continue;
            }

            var newPath = ReadDiffPath(line[4..]);
            if (oldPath is null && newPath is null)
            {
                continue;
            }

            var kind = oldPath is null ? "added" : newPath is null ? "deleted" : "modified";
            var selected = newPath ?? oldPath!;
            paths.Add(new NormalizedDiffPath(selected, kind, oldPath is not null && newPath is not null && !string.Equals(oldPath, newPath, StringComparison.Ordinal) ? oldPath : null));
            oldPath = null;
        }

        if (sawHunkHeader && !sawGitHeader)
        {
            throw new EvidencePlanningException(
                "ASEVD128",
                "Unified diff contains hunks but no 'diff --git' file headers.",
                "Supply a git-formatted unified diff, or pass explicit --path values.");
        }

        return paths
            .DistinctBy(static path => (path.Path, path.Kind, path.PreviousPath))
            .OrderBy(static path => path.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadDiffPath(string value)
    {
        var path = value.Split('\t', 2)[0].Trim();
        if (path == "/dev/null")
        {
            return null;
        }

        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path;
    }
}

/// <summary>
/// Matches exact paths and segment globs without content-sensitive policy evaluation.
/// </summary>
internal static class PathPattern
{
    public static bool TryMatch(string pattern, string path, out int specificity)
    {
        specificity = GetSpecificity(pattern);
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(patternSegments, 0, pathSegments, 0);
    }

    public static int GetSpecificity(string pattern)
    {
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var exact = segments.All(static segment => !segment.Contains('*'));
        var literalSegments = segments.Count(static segment => segment != "**" && !segment.Contains('*'));
        return (exact ? 1000 : 0) + literalSegments;
    }

    private static bool MatchSegments(string[] pattern, int patternIndex, string[] path, int pathIndex)
    {
        if (patternIndex == pattern.Length)
        {
            return pathIndex == path.Length;
        }

        if (pattern[patternIndex] == "**")
        {
            return Enumerable.Range(pathIndex, path.Length - pathIndex + 1)
                .Any(nextPathIndex => MatchSegments(pattern, patternIndex + 1, path, nextPathIndex));
        }

        return pathIndex < path.Length
            && MatchSegment(pattern[patternIndex], path[pathIndex])
            && MatchSegments(pattern, patternIndex + 1, path, pathIndex + 1);
    }

    private static bool MatchSegment(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var wildcardIndex = -1;
        var retryIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == value[valueIndex] || pattern[patternIndex] == '*'))
            {
                if (pattern[patternIndex] == '*')
                {
                    wildcardIndex = patternIndex++;
                    retryIndex = valueIndex;
                }
                else
                {
                    patternIndex++;
                    valueIndex++;
                }

                continue;
            }

            if (wildcardIndex < 0)
            {
                return false;
            }

            patternIndex = wildcardIndex + 1;
            valueIndex = ++retryIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}

/// <summary>
/// Represents a stable, user-facing evidence planning failure.
/// </summary>
public sealed class EvidencePlanningException : InvalidOperationException
{
    /// <summary>
    /// Initializes a planning exception with a stable code and concrete remediation.
    /// </summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="problem">Concise problem description.</param>
    /// <param name="fix">Concrete next action.</param>
    public EvidencePlanningException(string code, string problem, string fix)
        : base($"{code}: {problem} Fix: {fix}")
    {
        Code = code;
        Fix = fix;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the concrete remediation.</summary>
    public string Fix { get; }
}
