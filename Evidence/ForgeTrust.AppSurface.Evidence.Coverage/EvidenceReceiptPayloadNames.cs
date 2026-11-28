namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Defines the persisted receipt payload names and the narrower generated-name rule used for recovery.</summary>
internal static class EvidenceReceiptPayloadNames
{
    private const string PayloadPrefix = "payload-";
    private const string StagePrefix = PayloadPrefix + "stage-";
    private const int ReservationIdLength = 32;

    /// <summary>Gets the staging directory component for an already validated reservation ID.</summary>
    internal static string Stage(string reservationId) => StagePrefix + reservationId;

    /// <summary>Gets the final directory component for an already validated reservation ID.</summary>
    internal static string Final(string reservationId) => PayloadPrefix + reservationId;

    /// <summary>Checks the exact binding of a persisted reservation, including legacy hexadecimal case.</summary>
    internal static bool MatchesReservation(string? reservationId, string stageName, string payloadName)
        => reservationId is { Length: ReservationIdLength } id && id.All(Uri.IsHexDigit)
            && stageName == Stage(id) && payloadName == Final(id);

    /// <summary>Checks the case-sensitive prefix used by receipt payload names.</summary>
    internal static bool HasPayloadPrefix(string name) => name.StartsWith(PayloadPrefix, StringComparison.Ordinal);

    /// <summary>Validates a final component in the index, allowing legacy names and excluding staging names.</summary>
    internal static bool IsFinalDirectoryName(string name)
    {
        try
        {
            CoverageRunOutputLease.ValidateArtifactComponent(name);
            return HasPayloadPrefix(name) && !name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Infers a recovery stage only for final names generated with a lowercase hexadecimal ID.</summary>
    internal static string? InferRecoveryStage(string payloadName)
    {
        if (!HasPayloadPrefix(payloadName) || payloadName.Length != PayloadPrefix.Length + ReservationIdLength)
            return null;
        var id = payloadName[PayloadPrefix.Length..];
        return id.All(char.IsAsciiHexDigitLower) ? Stage(id) : null;
    }
}
