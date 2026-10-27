using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Describes the user-editable retention and warning thresholds for coverage artifacts.
/// </summary>
/// <remarks>
/// Policy values control optional history retention and growth warnings. They do not
/// change coverage collection, test execution, or gate thresholds. Hard resource limits
/// are defined by <see cref="CoverageArtifactLimits"/> and cannot be raised by this policy.
/// The JSON object requires <c>schemaVersion</c> with value <c>1</c>; its optional fields
/// are <c>historyMaxBytes</c>, <c>historyMaxAgeDays</c>, <c>repositoryWarningBytes</c>,
/// <c>domainWarningBytes</c>, and <c>warningCooldownHours</c>. Omitted optional fields
/// retain the property defaults shown below. Field names are case-sensitive. Unknown fields,
/// duplicate names, missing or unsupported schema versions, and nesting deeper than 16 levels
/// are rejected. Callers should apply only the returned complete policy; an invalid document
/// never produces a partially applied result and does not make coverage collection optional.
/// </remarks>
internal sealed record CoverageArtifactPolicy
{
    private const long MaximumPolicyByteValue = 1L << 40;

    /// <summary>
    /// Gets the maximum eligible history size in bytes. Its default is 2 GiB and its
    /// allowed range is zero through 1 TiB. Zero disables new history and allows only
    /// eligible unpinned history to expire during maintenance.
    /// </summary>
    internal long HistoryMaxBytes { get; private init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum age in days for eligible history before it may expire. Its default
    /// is 7 days and its allowed range is 1 through 365 days.
    /// </summary>
    internal int HistoryMaxAgeDays { get; private init; } = 7;

    /// <summary>
    /// Gets the repository-growth warning threshold in bytes. Its default is 1 GiB and its
    /// allowed range is 1 byte through 1 TiB.
    /// </summary>
    internal long RepositoryWarningBytes { get; private init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets the domain-growth warning threshold in bytes. Its default is 2 GiB and its
    /// allowed range is 1 byte through 1 TiB.
    /// </summary>
    internal long DomainWarningBytes { get; private init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets the minimum number of hours between repeated equivalent growth warnings. Its
    /// default is 24 hours and its allowed range is 1 through 168 hours.
    /// </summary>
    internal int WarningCooldownHours { get; private init; } = 24;

    /// <summary>
    /// Reads and validates a version 1 coverage artifact policy from a bounded UTF-8 JSON stream.
    /// Integer fields must use JSON integer syntax and satisfy their documented ranges;
    /// fractions, exponents, negative values where prohibited, unknown fields, duplicate fields,
    /// and documents deeper than 16 levels are rejected. The parser reads no more than 65,537
    /// bytes to distinguish an allowed 65,536-byte document from an oversized one.
    /// </summary>
    /// <param name="policyStream">
    /// A readable stream positioned at the start of the JSON document. The stream is not
    /// disposed by this method and need not support seeking.
    /// </param>
    /// <returns>The complete policy, with defaults applied to omitted optional fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policyStream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyStream"/> cannot be read.</exception>
    /// <exception cref="InvalidDataException">
    /// The document is malformed, exceeds the byte or nesting limit, uses an unsupported
    /// schema, contains duplicate or unknown fields, or supplies a value outside its range.
    /// </exception>
    /// <exception cref="IOException">The underlying stream fails while being read.</exception>
    /// <remarks>
    /// The stream remains open and must begin at the document's first byte. Reading is bounded
    /// even for a non-seekable or untrusted stream; the complete document is validated before a
    /// policy value is returned.
    /// </remarks>
    internal static CoverageArtifactPolicy Read(Stream policyStream)
    {
        ArgumentNullException.ThrowIfNull(policyStream);
        if (!policyStream.CanRead)
        {
            throw new ArgumentException("The coverage artifact policy stream must be readable.", nameof(policyStream));
        }

        var bytes = new byte[CoverageArtifactLimits.MaximumPolicyBytes + 1];
        var byteCount = 0;
        while (byteCount < bytes.Length)
        {
            var requestedBytes = Math.Min(8192, bytes.Length - byteCount);
            var bytesRead = policyStream.Read(bytes, byteCount, requestedBytes);
            if (bytesRead == 0)
            {
                break;
            }

            byteCount += bytesRead;
        }

        if (byteCount > CoverageArtifactLimits.MaximumPolicyBytes)
        {
            throw InvalidPolicy("exceeds the 64 KiB maximum size.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes.AsMemory(0, byteCount),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The coverage artifact policy must be one valid JSON document.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidPolicy("must be a JSON object.");
            }

            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            var schemaVersionSeen = false;
            var historyMaxBytes = 2L * 1024 * 1024 * 1024;
            var historyMaxAgeDays = 7;
            var repositoryWarningBytes = 1L * 1024 * 1024 * 1024;
            var domainWarningBytes = 2L * 1024 * 1024 * 1024;
            var warningCooldownHours = 24;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seenFields.Add(property.Name))
                {
                    throw InvalidPolicy($"contains duplicate field '{property.Name}'.");
                }

                switch (property.Name)
                {
                    case "schemaVersion":
                        var schemaVersion = ReadInteger(property.Value, property.Name);
                        if (schemaVersion != 1)
                        {
                            throw InvalidPolicy("requires schemaVersion to be exactly 1.");
                        }

                        schemaVersionSeen = true;
                        break;

                    case "historyMaxBytes":
                        historyMaxBytes = ReadInteger(
                            property.Value,
                            property.Name,
                            minimum: 0,
                            maximum: MaximumPolicyByteValue);
                        break;

                    case "historyMaxAgeDays":
                        historyMaxAgeDays = checked((int)ReadInteger(
                            property.Value,
                            property.Name,
                            minimum: 1,
                            maximum: 365));
                        break;

                    case "repositoryWarningBytes":
                        repositoryWarningBytes = ReadInteger(
                            property.Value,
                            property.Name,
                            minimum: 1,
                            maximum: MaximumPolicyByteValue);
                        break;

                    case "domainWarningBytes":
                        domainWarningBytes = ReadInteger(
                            property.Value,
                            property.Name,
                            minimum: 1,
                            maximum: MaximumPolicyByteValue);
                        break;

                    case "warningCooldownHours":
                        warningCooldownHours = checked((int)ReadInteger(
                            property.Value,
                            property.Name,
                            minimum: 1,
                            maximum: 168));
                        break;

                    default:
                        throw InvalidPolicy($"contains unknown field '{property.Name}'.");
                }
            }

            if (!schemaVersionSeen)
            {
                throw InvalidPolicy("requires schemaVersion to be exactly 1.");
            }

            return new CoverageArtifactPolicy
            {
                HistoryMaxBytes = historyMaxBytes,
                HistoryMaxAgeDays = historyMaxAgeDays,
                RepositoryWarningBytes = repositoryWarningBytes,
                DomainWarningBytes = domainWarningBytes,
                WarningCooldownHours = warningCooldownHours
            };
        }
    }

    private static long ReadInteger(JsonElement value, string fieldName, long minimum = 1, long maximum = long.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !HasIntegerSyntax(value) || !value.TryGetInt64(out var parsedValue))
        {
            throw InvalidPolicy($"field '{fieldName}' must be an integer.");
        }

        if (parsedValue < minimum || parsedValue > maximum)
        {
            throw InvalidPolicy($"field '{fieldName}' is outside its allowed range.");
        }

        return parsedValue;
    }

    private static bool HasIntegerSyntax(JsonElement value)
    {
        var text = value.GetRawText().AsSpan();
        var firstDigit = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (firstDigit == text.Length)
        {
            return false;
        }

        for (var index = firstDigit; index < text.Length; index++)
        {
            if (text[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidDataException InvalidPolicy(string reason) =>
        new($"The coverage artifact policy {reason}");
}

/// <summary>
/// Defines fixed resource caps and cooperative work budgets for coverage artifact storage.
/// </summary>
/// <remarks>
/// These limits protect optional storage management from unbounded work or metadata growth.
/// They are not user-configurable and do not constrain coverage collection or gate behavior.
/// </remarks>
internal static class CoverageArtifactLimits
{
    /// <summary>Gets the maximum encoded policy document size, 65,536 bytes.</summary>
    internal const int MaximumPolicyBytes = 64 * 1024;

    /// <summary>Gets the maximum original bytes in one automatic snapshot, 256 MiB.</summary>
    internal const long AutomaticSnapshotMaxOriginalBytes = 256L * 1024 * 1024;

    /// <summary>Gets the maximum staged bytes in one automatic snapshot, 256 MiB.</summary>
    internal const long AutomaticSnapshotMaxStagingBytes = 256L * 1024 * 1024;

    /// <summary>Gets the maximum stored staging bytes reserved across one artifact domain, 512 MiB.</summary>
    internal const long DomainStagingMaxBytes = 512L * 1024 * 1024;

    /// <summary>Gets the maximum expanded bytes for one explicit pin or restore operation, 64 GiB.</summary>
    internal const long ExplicitExpandedMaxBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>Gets the maximum files accepted in one inventory, pin, or restore operation, 10,000.</summary>
    internal const int MaximumFiles = 10_000;

    /// <summary>Gets the maximum active artifact registrations retained by the registry, 10,000.</summary>
    internal const int MaximumRegistrations = 10_000;

    /// <summary>Gets the maximum encoded inventory size for one coverage run, 2 MiB.</summary>
    internal const long RunInventoryMaxBytes = 2L * 1024 * 1024;

    /// <summary>Gets the total maximum encoded registry size, including reserved overflow observations, 16 MiB.</summary>
    internal const long RegistryMaxBytes = 16L * 1024 * 1024;

    /// <summary>Gets the registry space reserved for bounded overflow observations, 64 KiB.</summary>
    internal const long OverflowObservationReserveBytes = 64L * 1024;

    /// <summary>Gets the maximum number of recent overflow observations retained, 32.</summary>
    internal const int MaximumOverflowObservations = 32;

    /// <summary>Gets the maximum active read or write buffer size for a storage operation, 1 MiB.</summary>
    internal const int OperationBufferMaxBytes = 1 * 1024 * 1024;

    /// <summary>Gets the cumulative routine inspection budget, 1,000 milliseconds (1 second).</summary>
    internal const int InspectionBudgetMilliseconds = 1_000;

    /// <summary>Gets the maximum entries visited by a routine inspection, 10,000.</summary>
    internal const int InspectionMaxEntries = 10_000;

    /// <summary>Gets the cumulative routine maintenance budget, 5,000 milliseconds (5 seconds).</summary>
    internal const int RoutineMaintenanceBudgetMilliseconds = 5_000;

    /// <summary>Gets the maximum warning conditions retained in warning state, 1,000.</summary>
    internal const int WarningStateMaxConditions = 1_000;

    /// <summary>Gets the maximum encoded warning state size, 1 MiB.</summary>
    internal const long WarningStateMaxBytes = 1L * 1024 * 1024;

    /// <summary>Gets the maximum expiry receipts retained, 10,000.</summary>
    internal const int ExpiryReceiptsMaxEntries = 10_000;

    /// <summary>Gets the maximum encoded expiry receipt state size, 4 MiB.</summary>
    internal const long ExpiryReceiptsMaxBytes = 4L * 1024 * 1024;

    /// <summary>Gets the maximum age of an expiry receipt, 7 days.</summary>
    internal const int ExpiryReceiptMaxAgeDays = 7;
}
