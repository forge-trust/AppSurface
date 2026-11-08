using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Records the result of this repository's package-validation recipe, without EvidenceHost authority.</summary>
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
internal static class NativeValidationReceiptCodec
{
    internal const int CurrentSchemaVersion = 1;
    internal const string ReceiptTypeName = "appsurface-native-validation-receipt";
    // Retains an already bounded 1,024-character fan-in diagnostic plus its fixed context prefix.
    internal const int MaximumDiagnosticCharacters = 2048;
    private const int MaximumReceiptBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReceiptOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

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
