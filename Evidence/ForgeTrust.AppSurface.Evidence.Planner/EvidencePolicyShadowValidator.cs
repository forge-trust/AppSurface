using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>
/// Labels a base-owned path case by its expected repository role. The label is supplied by the
/// fixture owner; it is not inferred from candidate policy rules.
/// </summary>
public enum EvidencePolicyShadowFixtureKind
{
    /// <summary>Narrative documentation explicitly intended to be documentation-only.</summary>
    Documentation,

    /// <summary>Ordinary executable product or test code.</summary>
    Code,

    /// <summary>A path whose evidence profile requires a disposable or otherwise declared resource.</summary>
    Resource,

    /// <summary>Policy, workflow, runner, gate, or other control-plane material.</summary>
    ControlPlane,

    /// <summary>A path intentionally absent from the policy's recognized path classes.</summary>
    Unknown,
}

/// <summary>
/// Identifies whether a finding or selection row is associated with trusted base input or candidate-controlled input.
/// </summary>
public enum EvidencePolicyShadowFixtureSource
{
    /// <summary>The case came from the trusted base-owned fixture set.</summary>
    Base,

    /// <summary>The case was added or changed in candidate fixture data.</summary>
    Candidate,
}

/// <summary>
/// Describes one expected path selection used by the policy-shadow comparison.
/// </summary>
/// <param name="Id">Stable fixture identifier, unique within its fixture set.</param>
/// <param name="Kind">Base-owned classification independent of candidate policy rules.</param>
/// <param name="ChangedPath">Repository-relative path input, including a previous path for a rename.</param>
public sealed record EvidencePolicyShadowFixture(
    string Id,
    EvidencePolicyShadowFixtureKind Kind,
    NormalizedDiffPath ChangedPath);

/// <summary>
/// Reports the selected profile IDs for one fixture under both policy versions.
/// </summary>
/// <param name="FixtureSource">Whether the fixture is trusted base input or supplemental candidate input.</param>
/// <param name="FixtureId">Stable fixture identifier.</param>
/// <param name="Kind">Fixture classification.</param>
/// <param name="ChangedPath">The path input resolved under both policies.</param>
/// <param name="BaseProfileId">Profile selected by the trusted base policy.</param>
/// <param name="CandidateProfileId">Profile selected by the proposed candidate policy.</param>
public sealed record EvidencePolicyShadowSelection(
    EvidencePolicyShadowFixtureSource FixtureSource,
    string FixtureId,
    EvidencePolicyShadowFixtureKind Kind,
    NormalizedDiffPath ChangedPath,
    string BaseProfileId,
    string CandidateProfileId);

/// <summary>
/// Describes one bounded, non-claiming policy-shadow finding.
/// </summary>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="FixtureSource">Base or candidate side associated with the finding; weakened selections are attributed to the candidate.</param>
/// <param name="FixtureId">Stable fixture identifier, if the finding concerns a fixture.</param>
/// <param name="Kind">Fixture classification, if the finding concerns a fixture.</param>
/// <param name="Path">Bounded repository-relative path, if the finding concerns a path.</param>
/// <param name="Message">Stable explanatory text without policy claims or exception details.</param>
public sealed record EvidencePolicyShadowDiagnostic(
    string Code,
    EvidencePolicyShadowFixtureSource FixtureSource,
    string? FixtureId,
    EvidencePolicyShadowFixtureKind? Kind,
    string? Path,
    string Message);

/// <summary>
/// Contains a deterministic comparison of base-owned and candidate evidence-policy selections.
/// This result is a shadow-analysis outcome only; it is not a gate verdict or evidence claim.
/// </summary>
/// <param name="IsCompatible">Whether the bounded comparison found no policy or fixture incompatibility.</param>
/// <param name="Selections">Resolved profile pairs, ordered deterministically and bounded by fixture limits.</param>
/// <param name="Diagnostics">Stable findings, ordered deterministically and capped at the validator limit.</param>
/// <param name="DiagnosticsTruncated">Whether additional findings were omitted after reaching the diagnostic cap.</param>
public sealed record EvidencePolicyShadowResult(
    bool IsCompatible,
    IReadOnlyList<EvidencePolicyShadowSelection> Selections,
    IReadOnlyList<EvidencePolicyShadowDiagnostic> Diagnostics,
    bool DiagnosticsTruncated);

/// <summary>
/// Compares a trusted base gate policy with an untrusted candidate without issuing an EvidenceHost claim.
/// </summary>
/// <remarks>
/// The caller must load the base policy and fixture set from the protected base revision. The validator cannot
/// authenticate input provenance, select a trusted revision, run a workflow, or establish T7 authority. Until
/// a base-owned workflow invokes this API with protected-base inputs and independently enforces its result, the
/// output must not be treated as gate authority. Candidate fixture data is audit-only: an edited or removed
/// candidate case never replaces a base-owned case, while candidate
/// additions and edited variants are checked as supplemental inputs. Both policies must independently pass
/// <see cref="EvidencePlanner.ValidateGatePolicy(EvidencePolicy)"/>. Each case is then resolved under both
/// policies and the candidate selection is compared with the base selection, including resource, producer,
/// assertion, artifact-slot, and obligation requirements.
/// </remarks>
public static class EvidencePolicyShadowValidator
{
    /// <summary>Maximum number of base or candidate fixture cases accepted in one input set.</summary>
    public const int MaximumFixtures = 64;

    /// <summary>Maximum number of findings returned by one comparison.</summary>
    public const int MaximumDiagnostics = 32;

    private const int MaximumProfiles = 64;
    private const int MaximumRules = 256;
    private const int MaximumFixtureIdentifierLength = 128;
    private const int MaximumPathLength = 512;
    private const int MaximumChangeKindLength = 32;

    /// <summary>
    /// Validates old and candidate gate policies against the trusted base fixture set and candidate fixture data.
    /// </summary>
    /// <param name="trustedBasePolicy">Gate policy loaded from the protected base revision.</param>
    /// <param name="candidatePolicy">Proposed policy, treated as untrusted data.</param>
    /// <param name="trustedBaseFixtures">Authoritative expected cases loaded from the protected base revision.</param>
    /// <param name="candidateFixtures">Candidate case data. It may add checks, but cannot replace base cases.</param>
    /// <returns>A bounded, deterministic shadow result with no evidence claim or gate verdict.</returns>
    /// <remarks>
    /// The fixture classifications are supplied by the base-owned case set. A control-plane case must match
    /// an explicit rule under both policies; conservative fallback alone is reported so new control-plane
    /// paths cannot silently become indistinguishable from unknown paths. Candidate fixture deletions and
    /// edits are reported, and the original base case remains in the comparison.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An input argument is <see langword="null"/>.</exception>
    public static EvidencePolicyShadowResult Validate(
        EvidencePolicy trustedBasePolicy,
        EvidencePolicy candidatePolicy,
        IReadOnlyList<EvidencePolicyShadowFixture> trustedBaseFixtures,
        IReadOnlyList<EvidencePolicyShadowFixture> candidateFixtures)
    {
        ArgumentNullException.ThrowIfNull(trustedBasePolicy);
        ArgumentNullException.ThrowIfNull(candidatePolicy);
        ArgumentNullException.ThrowIfNull(trustedBaseFixtures);
        ArgumentNullException.ThrowIfNull(candidateFixtures);

        var diagnostics = new List<EvidencePolicyShadowDiagnostic>();
        var selections = new List<EvidencePolicyShadowSelection>();

        var basePolicyValid = ValidateGatePolicy(trustedBasePolicy, EvidencePolicyShadowFixtureSource.Base, diagnostics);
        var candidatePolicyValid = ValidateGatePolicy(candidatePolicy, EvidencePolicyShadowFixtureSource.Candidate, diagnostics);
        if (!basePolicyValid || !candidatePolicyValid)
        {
            return CreateResult(selections, diagnostics);
        }

        if (trustedBaseFixtures.Count == 0)
        {
            diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                "ASEPS003",
                EvidencePolicyShadowFixtureSource.Base,
                null,
                null,
                null,
                "The trusted base fixture set is empty; no policy-shadow comparison was performed."));
            return CreateResult(selections, diagnostics);
        }

        if (!TryIndexFixtures(trustedBaseFixtures, EvidencePolicyShadowFixtureSource.Base, diagnostics, out var baseById))
        {
            return CreateResult(selections, diagnostics);
        }

        var candidateById = new Dictionary<string, EvidencePolicyShadowFixture>(StringComparer.Ordinal);
        var candidateFixturesValid = false;
        if (candidateFixtures.Count > MaximumFixtures)
        {
            diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                "ASEPS003",
                EvidencePolicyShadowFixtureSource.Candidate,
                null,
                null,
                null,
                $"Candidate fixture input exceeds the {MaximumFixtures}-case limit; authoritative base cases remain active."));
            candidateById = new Dictionary<string, EvidencePolicyShadowFixture>(StringComparer.Ordinal);
        }
        else
        {
            candidateFixturesValid = TryIndexFixtures(candidateFixtures, EvidencePolicyShadowFixtureSource.Candidate, diagnostics, out candidateById);
        }

        var cases = new List<(EvidencePolicyShadowFixture Fixture, EvidencePolicyShadowFixtureSource Source)>();
        foreach (var baseFixture in baseById.Values.OrderBy(static fixture => fixture.Id, StringComparer.Ordinal))
        {
            cases.Add((baseFixture, EvidencePolicyShadowFixtureSource.Base));

            if (!candidateFixturesValid)
            {
                continue;
            }

            if (!candidateById.TryGetValue(baseFixture.Id, out var candidateFixture))
            {
                diagnostics.Add(CreateFixtureDiagnostic(
                    "ASEPS004",
                    EvidencePolicyShadowFixtureSource.Candidate,
                    baseFixture,
                    "Candidate fixture data removed this base-owned case; the base case remains authoritative."));
                continue;
            }

            if (candidateFixture != baseFixture)
            {
                diagnostics.Add(CreateFixtureDiagnostic(
                    "ASEPS004",
                    EvidencePolicyShadowFixtureSource.Candidate,
                    candidateFixture,
                    "Candidate fixture data edited this base-owned case; both the original base case and candidate variant are checked."));
                cases.Add((candidateFixture, EvidencePolicyShadowFixtureSource.Candidate));
            }
        }

        if (candidateFixturesValid)
        {
            foreach (var candidateFixture in candidateById.Values
                .Where(fixture => !baseById.ContainsKey(fixture.Id))
                .OrderBy(static fixture => fixture.Id, StringComparer.Ordinal))
            {
                cases.Add((candidateFixture, EvidencePolicyShadowFixtureSource.Candidate));
            }
        }

        foreach (var (fixture, source) in cases)
        {
            var basePlan = TryResolve(trustedBasePolicy, fixture, EvidencePolicyShadowFixtureSource.Base, diagnostics);
            var candidatePlan = TryResolve(candidatePolicy, fixture, EvidencePolicyShadowFixtureSource.Candidate, diagnostics);
            if (basePlan is null || candidatePlan is null)
            {
                continue;
            }

            selections.Add(new EvidencePolicyShadowSelection(
                source,
                fixture.Id,
                fixture.Kind,
                basePlan.ChangedPaths[0],
                basePlan.Profile.Id,
                candidatePlan.Profile.Id));

            var difference = EvidencePlanner.FindProfileSupersetDifference(candidatePlan.Profile, basePlan.Profile);
            if (difference is { } missing)
            {
                diagnostics.Add(CreateFixtureDiagnostic(
                    "ASEPS007",
                    EvidencePolicyShadowFixtureSource.Candidate,
                    fixture,
                    $"Candidate selection does not preserve {missing.Requirement}: {missing.Detail}."));
            }

            if (fixture.Kind == EvidencePolicyShadowFixtureKind.ControlPlane)
            {
                AddUnrecognizedControlPlanePaths(candidatePolicy, fixture, EvidencePolicyShadowFixtureSource.Candidate, diagnostics);
            }
        }

        return CreateResult(selections, diagnostics);
    }

    private static bool ValidateGatePolicy(
        EvidencePolicy policy,
        EvidencePolicyShadowFixtureSource source,
        ICollection<EvidencePolicyShadowDiagnostic> diagnostics)
    {
        if (policy.Profiles is null || policy.Rules is null || policy.Profiles.Count > MaximumProfiles || policy.Rules.Count > MaximumRules)
        {
            diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                source == EvidencePolicyShadowFixtureSource.Base ? "ASEPS001" : "ASEPS002",
                source,
                null,
                null,
                null,
                $"The {(source == EvidencePolicyShadowFixtureSource.Base ? "base" : "candidate")} gate policy exceeds the bounded policy shape or is incomplete."));
            return false;
        }

        try
        {
            EvidencePlanner.ValidateGatePolicy(policy);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                source == EvidencePolicyShadowFixtureSource.Base ? "ASEPS001" : "ASEPS002",
                source,
                null,
                null,
                null,
                $"The {(source == EvidencePolicyShadowFixtureSource.Base ? "base" : "candidate")} gate policy is invalid; no policy-shadow comparison was performed."));
            return false;
        }
    }

    private static bool TryIndexFixtures(
        IReadOnlyList<EvidencePolicyShadowFixture> fixtures,
        EvidencePolicyShadowFixtureSource source,
        ICollection<EvidencePolicyShadowDiagnostic> diagnostics,
        out Dictionary<string, EvidencePolicyShadowFixture> fixturesById)
    {
        fixturesById = new Dictionary<string, EvidencePolicyShadowFixture>(StringComparer.Ordinal);
        if (fixtures.Count > MaximumFixtures)
        {
            diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                "ASEPS003",
                source,
                null,
                null,
                null,
                $"The {(source == EvidencePolicyShadowFixtureSource.Base ? "base" : "candidate")} fixture set exceeds the {MaximumFixtures}-case limit."));
            return false;
        }

        var valid = true;
        foreach (var fixture in fixtures)
        {
            if (fixture is null
                || string.IsNullOrWhiteSpace(fixture.Id)
                || fixture.Id.Length > MaximumFixtureIdentifierLength
                || !Enum.IsDefined(fixture.Kind)
                || fixture.ChangedPath is null
                || !HasBoundedPath(fixture.ChangedPath.Path)
                || (fixture.ChangedPath.Kind is not null && fixture.ChangedPath.Kind.Length > MaximumChangeKindLength)
                || (fixture.ChangedPath.PreviousPath is not null && !HasBoundedPath(fixture.ChangedPath.PreviousPath)))
            {
                diagnostics.Add(CreateFixtureDiagnostic(
                    "ASEPS003",
                    source,
                    fixture,
                    "Fixture data is incomplete or exceeds the bounded identifier/path limits."));
                valid = false;
                continue;
            }

            if (!fixturesById.TryAdd(fixture.Id, fixture))
            {
                diagnostics.Add(CreateFixtureDiagnostic(
                    "ASEPS003",
                    source,
                    fixture,
                    "Fixture identifiers must be unique within each input set."));
                valid = false;
            }
        }

        return valid;
    }

    private static EvidencePlan? TryResolve(
        EvidencePolicy policy,
        EvidencePolicyShadowFixture fixture,
        EvidencePolicyShadowFixtureSource policySource,
        ICollection<EvidencePolicyShadowDiagnostic> diagnostics)
    {
        try
        {
            return new EvidencePlanner().ResolveForGate(policy, [fixture.ChangedPath]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            var side = policySource == EvidencePolicyShadowFixtureSource.Base ? "base" : "candidate";
            diagnostics.Add(CreateFixtureDiagnostic(
                policySource == EvidencePolicyShadowFixtureSource.Base ? "ASEPS005" : "ASEPS006",
                policySource,
                fixture,
                $"The {side} policy could not resolve this fixture; no selection comparison was made."));
            return null;
        }
    }

    private static void AddUnrecognizedControlPlanePaths(
        EvidencePolicy policy,
        EvidencePolicyShadowFixture fixture,
        EvidencePolicyShadowFixtureSource policySource,
        ICollection<EvidencePolicyShadowDiagnostic> diagnostics)
    {
        foreach (var path in EnumeratePaths(fixture.ChangedPath))
        {
            EvidencePlan? plan;
            try
            {
                plan = new EvidencePlanner().ResolveForGate(policy, [new NormalizedDiffPath(path)]);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
            {
                continue;
            }

            if (!policy.Rules.Any(rule => PathPattern.TryMatch(rule.Pattern, path, out _)))
            {
                var side = policySource == EvidencePolicyShadowFixtureSource.Base ? "base" : "candidate";
                diagnostics.Add(new EvidencePolicyShadowDiagnostic(
                    "ASEPS008",
                    policySource,
                    Bound(fixture.Id, MaximumFixtureIdentifierLength),
                    fixture.Kind,
                    Bound(path, MaximumPathLength),
                    $"The {side} policy does not explicitly recognize this control-plane path; conservative fallback alone is not a control-plane mapping."));
            }
        }
    }

    private static IEnumerable<string> EnumeratePaths(NormalizedDiffPath changedPath)
    {
        yield return changedPath.Path;
        if (!string.IsNullOrWhiteSpace(changedPath.PreviousPath))
        {
            yield return changedPath.PreviousPath;
        }
    }

    private static EvidencePolicyShadowDiagnostic CreateFixtureDiagnostic(
        string code,
        EvidencePolicyShadowFixtureSource source,
        EvidencePolicyShadowFixture? fixture,
        string message) =>
        new(
            code,
            source,
            fixture is null ? null : Bound(fixture.Id, MaximumFixtureIdentifierLength),
            fixture is null || !Enum.IsDefined(fixture.Kind) ? null : fixture.Kind,
            fixture?.ChangedPath is null ? null : Bound(fixture.ChangedPath.Path, MaximumPathLength),
            message);

    private static bool HasBoundedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= MaximumPathLength;

    private static string Bound(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value ?? string.Empty
            : string.Concat(value.AsSpan(0, maximumLength - 1), "…");

    private static EvidencePolicyShadowResult CreateResult(
        IReadOnlyCollection<EvidencePolicyShadowSelection> selections,
        IReadOnlyCollection<EvidencePolicyShadowDiagnostic> diagnostics)
    {
        var orderedSelections = selections
            .OrderBy(static selection => selection.FixtureSource)
            .ThenBy(static selection => selection.FixtureId, StringComparer.Ordinal)
            .ThenBy(static selection => selection.ChangedPath.Path, StringComparer.Ordinal)
            .ThenBy(static selection => selection.ChangedPath.PreviousPath, StringComparer.Ordinal)
            .ToArray();
        var orderedDiagnostics = diagnostics
            .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.FixtureSource)
            .ThenBy(static diagnostic => diagnostic.FixtureId, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
        var truncated = orderedDiagnostics.Length > MaximumDiagnostics;
        var boundedDiagnostics = orderedDiagnostics.Take(MaximumDiagnostics).ToArray();
        return new EvidencePolicyShadowResult(
            IsCompatible: orderedDiagnostics.Length == 0,
            Selections: orderedSelections,
            Diagnostics: boundedDiagnostics,
            DiagnosticsTruncated: truncated);
    }
}
