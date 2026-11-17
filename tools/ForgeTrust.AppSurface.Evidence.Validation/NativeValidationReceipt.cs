using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Records the result of this repository's package-validation recipe, without EvidenceHost authority.</summary>
/// <param name="SchemaVersion">The closed receipt schema version.</param>
/// <param name="ReceiptType">The discriminator identifying this as a native package-validation receipt.</param>
/// <param name="RunOutcome">The aggregate outcome: <c>passed</c>, <c>failed</c>, or <c>cancelled</c>.</param>
/// <param name="ProfileId">The nonblank selected package-validation profile identifier.</param>
/// <param name="PlanDigest">The digest of the plan against which the journeys were run.</param>
/// <param name="InputDigest">The caller-supplied lowercase SHA-256 digest identifying recipe inputs; verification checks its shape, not its origin.</param>
/// <param name="Journeys">The ordered result for each producer selected by the plan.</param>
/// <param name="CleanupCompleted">Whether recipe cleanup completed successfully.</param>
/// <param name="CleanupDiagnostic">A bounded diagnostic when cleanup did not complete.</param>
/// <param name="CallerCancellationRequested">Whether the runner observed caller cancellation when it created this receipt.</param>
/// <param name="EvidenceHostClaim">Always <c>none</c>; this receipt carries no EvidenceHost admission claim.</param>
/// <param name="EvidenceHostEligibility">Always <c>none</c>; this receipt carries no EvidenceHost eligibility decision.</param>
/// <param name="EvidenceHostProofProduced">Always <see langword="false"/>; this receipt is not EvidenceHost proof.</param>
/// <param name="ReceiptDigest">The canonical SHA-256 digest over the receipt with this field empty.</param>
internal sealed record NativeValidationReceipt(
    int SchemaVersion,
    string ReceiptType,
    string RunOutcome,
    string ProfileId,
    string PlanDigest,
    string InputDigest,
    IReadOnlyList<NativeValidationJourneyReceipt> Journeys,
    bool CleanupCompleted,
    string? CleanupDiagnostic,
    bool CallerCancellationRequested,
    string EvidenceHostClaim,
    string EvidenceHostEligibility,
    bool EvidenceHostProofProduced,
    string ReceiptDigest);

/// <summary>Describes one selected repository-only package journey and its independently checked outputs.</summary>
/// <param name="ProducerId">The producer identifier declared by the selected plan.</param>
/// <param name="Outcome">The producer's known execution outcome.</param>
/// <param name="DeclaredAssertionIds">The assertion identifiers declared by the producer.</param>
/// <param name="SatisfiedAssertionIds">The assertion identifiers reported as satisfied.</param>
/// <param name="DeclaredArtifactSlots">The artifact slots declared by the producer.</param>
/// <param name="Artifacts">The independently validated results written to declared artifact slots.</param>
/// <param name="Diagnostic">An optional bounded producer diagnostic.</param>
/// <param name="ElapsedMilliseconds">The non-negative producer execution duration in milliseconds.</param>
internal sealed record NativeValidationJourneyReceipt(
    string ProducerId,
    string Outcome,
    IReadOnlyList<string> DeclaredAssertionIds,
    IReadOnlyList<string> SatisfiedAssertionIds,
    IReadOnlyList<EvidenceArtifactSlot> DeclaredArtifactSlots,
    IReadOnlyList<EvidenceArtifactResult> Artifacts,
    string? Diagnostic,
    long ElapsedMilliseconds);

/// <summary>Serializes and verifies the closed, versioned receipt emitted by the native package recipe.</summary>
/// <remarks>
/// A valid receipt is a package-validation result only. Its digest checks internal consistency, not
/// authenticated origin, and grants no EvidenceHost admission authority.
/// </remarks>
internal static class NativeValidationReceiptCodec
{
    /// <summary>Gets the only receipt schema version accepted by this codec.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>Gets the discriminator identifying native package-validation receipts.</summary>
    internal const string ReceiptTypeName = "appsurface-native-validation-receipt";

    /// <summary>Gets the maximum number of characters allowed in a journey diagnostic.</summary>
    /// <remarks>The value retains an already bounded 1,024-character fan-in diagnostic plus its fixed context prefix.</remarks>
    internal const int MaximumDiagnosticCharacters = 2048;
    private const int MaximumReceiptBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReceiptOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Creates and seals the aggregate receipt for one completed package-validation run.</summary>
    /// <param name="plan">The selected plan whose profile and digest are recorded.</param>
    /// <param name="inputDigest">The caller-computed lowercase SHA-256 digest identifying recipe inputs.</param>
    /// <param name="journeys">The ordered producer results collected by the runner.</param>
    /// <param name="cleanupCompleted">Whether cleanup completed successfully.</param>
    /// <param name="cleanupDiagnostic">An optional bounded cleanup diagnostic.</param>
    /// <param name="callerCancellationRequested">The runner's final sampled caller-cancellation state.</param>
    /// <returns>A receipt with a canonical digest and an aggregate outcome derived from the supplied results.</returns>
    /// <remarks>This records the supplied input digest but does not independently authenticate or recompute it.</remarks>
    internal static NativeValidationReceipt Create(
        EvidencePlan plan,
        string inputDigest,
        IReadOnlyList<NativeValidationJourneyReceipt> journeys,
        bool cleanupCompleted,
        string? cleanupDiagnostic,
        bool callerCancellationRequested)
    {
        var allJourneysPassed = journeys.Count == plan.Profile.Producers.Count
            && journeys.All(static journey => string.Equals(journey.Outcome, "passed", StringComparison.Ordinal));
        var outcome = callerCancellationRequested
            ? "cancelled"
            : cleanupCompleted && allJourneysPassed ? "passed" : "failed";
        var draft = new NativeValidationReceipt(
            CurrentSchemaVersion,
            ReceiptTypeName,
            outcome,
            plan.Profile.Id,
            plan.PlanDigest,
            inputDigest,
            journeys,
            cleanupCompleted,
            cleanupDiagnostic,
            callerCancellationRequested,
            "none",
            "none",
            EvidenceHostProofProduced: false,
            ReceiptDigest: string.Empty);
        return draft with { ReceiptDigest = ComputeDigest(draft) };
    }

    /// <summary>Serializes a receipt as canonical JSON after recomputing its digest.</summary>
    /// <param name="receipt">The receipt to serialize.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    /// <remarks>
    /// Serialization reseals the supplied in-memory value. It therefore does not detect whether that value
    /// was modified after an earlier seal and does not authenticate its origin. Use <see cref="Verify"/>
    /// with the original plan to check plan binding and receipt semantics.
    /// </remarks>
    internal static byte[] Serialize(NativeValidationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var sealedReceipt = receipt with { ReceiptDigest = ComputeDigest(receipt with { ReceiptDigest = string.Empty }) };
        if (!HasValidDigest(sealedReceipt))
        {
            throw new InvalidDataException("Native validation receipt is invalid.");
        }

        return EvidenceCanonicalJson.Serialize(sealedReceipt);
    }

    /// <summary>Reads a bounded, closed-schema receipt and validates its embedded digest.</summary>
    /// <param name="utf8Json">The UTF-8 JSON bytes to parse.</param>
    /// <returns>The structurally valid receipt.</returns>
    /// <remarks>
    /// Every constructor member must be present, with exact PascalCase names and no duplicate properties.
    /// Null values are permitted only for nullable constructor parameters.
    /// This method validates receipt structure and its digest, but it does not bind the receipt to a plan.
    /// Call <see cref="Verify"/> with the original plan before relying on its reported results.
    /// </remarks>
    internal static NativeValidationReceipt Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumReceiptBytes)
        {
            throw new InvalidDataException("Native validation receipt exceeds its size limit.");
        }

        var receipt = JsonSerializer.Deserialize<NativeValidationReceipt>(utf8Json, ReceiptOptions)
            ?? throw new JsonException("Native validation receipt must be an object.");
        if (receipt.SchemaVersion != CurrentSchemaVersion || receipt.ReceiptType != ReceiptTypeName)
        {
            throw new JsonException("Native validation receipt schema is unsupported.");
        }

        if (!HasValidDigest(receipt))
        {
            throw new JsonException("Native validation receipt is invalid.");
        }

        return receipt;
    }

    /// <summary>Checks receipt integrity, plan binding, producer results, and aggregate outcome consistency.</summary>
    /// <param name="plan">The original plan against which the receipt must be checked.</param>
    /// <param name="receipt">The parsed or otherwise supplied receipt.</param>
    /// <returns><see langword="true"/> when the receipt is internally consistent with the plan; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Verification checks that <see cref="NativeValidationReceipt.InputDigest"/> has the expected SHA-256 shape,
    /// but the digest is caller supplied and is not independently authenticated or compared with external inputs.
    /// A successful result establishes package-recipe consistency only, not EvidenceHost admission authority.
    /// </remarks>
    internal static bool Verify(EvidencePlan plan, NativeValidationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!HasValidDigest(receipt)
            || plan.Profile.Producers.Count == 0
            || plan.Profile.Producers.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != plan.Profile.Producers.Count
            || !string.Equals(receipt.ProfileId, plan.Profile.Id, StringComparison.Ordinal)
            || !string.Equals(receipt.PlanDigest, plan.PlanDigest, StringComparison.Ordinal)
            || !PlanHasValidDigest(plan)
            || receipt.Journeys.Count != plan.Profile.Producers.Count)
        {
            return false;
        }

        for (var index = 0; index < plan.Profile.Producers.Count; index++)
        {
            var declaration = plan.Profile.Producers[index];
            var journey = receipt.Journeys[index];
            if (!string.Equals(journey.ProducerId, declaration.Id, StringComparison.Ordinal)
                || !journey.DeclaredAssertionIds.SequenceEqual(declaration.AssertionIds, StringComparer.Ordinal)
                || !journey.DeclaredArtifactSlots.SequenceEqual(declaration.ArtifactSlots)
                || !IsKnownOutcome(journey.Outcome)
                || journey.ElapsedMilliseconds < 0
                || HasDuplicates(declaration.AssertionIds)
                || HasDuplicates(journey.DeclaredAssertionIds)
                || HasDuplicates(journey.SatisfiedAssertionIds)
                || journey.SatisfiedAssertionIds.Any(assertion => !declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal))
                || !ValidArtifactSet(declaration, journey.Artifacts, requireRequiredSlots: journey.Outcome == "passed"))
            {
                return false;
            }

            if (journey.Outcome == "passed"
                && !journey.SatisfiedAssertionIds.Order(StringComparer.Ordinal)
                    .SequenceEqual(declaration.AssertionIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                return false;
            }
        }

        var allPassed = receipt.Journeys.All(static journey => journey.Outcome == "passed");
        return receipt.RunOutcome switch
        {
            "passed" => !receipt.CallerCancellationRequested && receipt.CleanupCompleted && allPassed,
            "failed" => !receipt.CallerCancellationRequested && (!receipt.CleanupCompleted || !allPassed),
            "cancelled" => receipt.CallerCancellationRequested,
            _ => false,
        };
    }

    private static bool HasValidDigest(NativeValidationReceipt receipt) =>
        receipt.SchemaVersion == CurrentSchemaVersion
        && string.Equals(receipt.ReceiptType, ReceiptTypeName, StringComparison.Ordinal)
        && receipt.EvidenceHostClaim == "none"
        && receipt.EvidenceHostEligibility == "none"
        && !receipt.EvidenceHostProofProduced
        && receipt.RunOutcome is "passed" or "failed" or "cancelled"
        && !string.IsNullOrWhiteSpace(receipt.ProfileId)
        && receipt.Journeys is { Count: > 0 }
        && receipt.Journeys.All(static journey => journey is not null
            && !string.IsNullOrWhiteSpace(journey.ProducerId)
            && IsKnownOutcome(journey.Outcome)
            && journey.DeclaredAssertionIds is not null
            && journey.SatisfiedAssertionIds is not null
            && journey.DeclaredArtifactSlots is not null
            && journey.DeclaredArtifactSlots.All(static slot => slot is not null)
            && journey.Artifacts is not null
            && journey.Artifacts.All(static artifact => artifact is not null)
            && (journey.Diagnostic is null || journey.Diagnostic.Length <= MaximumDiagnosticCharacters)
            && journey.ElapsedMilliseconds >= 0)
        && receipt.Journeys.Select(static journey => journey.ProducerId).Distinct(StringComparer.Ordinal).Count() == receipt.Journeys.Count
        && IsSha256(receipt.PlanDigest)
        && IsSha256(receipt.InputDigest)
        && IsSha256(receipt.ReceiptDigest)
        && string.Equals(receipt.ReceiptDigest, ComputeDigest(receipt with { ReceiptDigest = string.Empty }), StringComparison.Ordinal)
        && (receipt.CleanupDiagnostic is null || receipt.CleanupDiagnostic.Length <= 1024)
        && (!receipt.CleanupCompleted || receipt.CleanupDiagnostic is null)
        && (receipt.RunOutcome != "passed" || receipt.CleanupCompleted && !receipt.CallerCancellationRequested)
        && (receipt.RunOutcome != "cancelled" || receipt.CallerCancellationRequested);

    private static bool PlanHasValidDigest(EvidencePlan plan) =>
        plan.PolicySnapshot is not null
        && string.Equals(plan.PolicyDigest, EvidenceDigest.CanonicalSha256(plan.PolicySnapshot), StringComparison.Ordinal)
        && string.Equals(plan.DiffDigest, EvidenceDigest.CanonicalSha256(plan.ChangedPaths), StringComparison.Ordinal)
        && string.Equals(plan.PlanDigest,
            EvidenceDigest.CanonicalSha256(plan with { PlanDigest = string.Empty }), StringComparison.Ordinal);

    /// <summary>Checks that artifact results match a producer's declared slots.</summary>
    /// <param name="declaration">The producer declaration containing the slot definitions.</param>
    /// <param name="artifacts">The artifact results to validate.</param>
    /// <param name="requireRequiredSlots">When <see langword="true"/>, every required slot must have a valid result; otherwise required slots are treated as optional while undeclared or invalid results remain rejected.</param>
    /// <returns><see langword="true"/> when the results satisfy the slot contract.</returns>
    internal static bool ValidArtifactSet(
        EvidenceProducerDeclaration declaration,
        IReadOnlyList<EvidenceArtifactResult> artifacts,
        bool requireRequiredSlots)
    {
        var selected = requireRequiredSlots ? declaration : declaration with
        {
            ArtifactSlots = declaration.ArtifactSlots.Select(static slot => slot with { Required = false }).ToArray(),
        };
        return EvidenceArtifactValidation.AreValid(selected, artifacts);
    }

    private static bool IsKnownOutcome(string outcome) => outcome is
        "passed" or "failed" or "flaky" or "timedout" or "unavailable" or "cancelled" or "invalid" or "skippednotrequired";

    private static bool HasDuplicates(IReadOnlyList<string> values) =>
        values is null || values.Any(static value => value is null)
        || values.Distinct(StringComparer.Ordinal).Count() != values.Count;

    private static bool IsSha256(string value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeDigest(NativeValidationReceipt receipt) =>
        EvidenceDigest.Sha256(EvidenceCanonicalJson.Serialize(receipt with { ReceiptDigest = string.Empty }));
}
