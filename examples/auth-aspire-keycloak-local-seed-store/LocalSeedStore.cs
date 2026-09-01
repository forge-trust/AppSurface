using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuthAspireKeycloakLocalSeedStore;

/// <summary>
/// Owns the bounded, consumer-local JSON state shared by the sample seed workers.
/// </summary>
/// <remarks>
/// The store contains only non-secret broker metadata, identity subject mappings, and fixture records. Updates are
/// serialized per path within each worker process and committed by replacing a temporary file, so a process interruption
/// leaves either the previous complete document or the next complete document. The AppHost's strict seed chain supplies
/// the sample's cross-process serialization; unrelated concurrent processes must add their own coordination.
/// </remarks>
public sealed class LocalSeedStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _path;

    /// <summary>
    /// Initializes a store for the supplied durable JSON path.
    /// </summary>
    /// <param name="path">The file path owned by the consumer workers.</param>
    public LocalSeedStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A store path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    /// <summary>
    /// Reads and validates the current store document.
    /// </summary>
    /// <returns>An immutable snapshot of the consumer-owned records.</returns>
    public LocalSeedStoreSnapshot ReadSnapshot()
    {
        var gate = Locks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return ReadSnapshotCore();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Upserts the single sample broker alias by its natural alias key.
    /// </summary>
    /// <param name="alias">The Keycloak identity-provider alias.</param>
    /// <param name="issuer">The non-secret issuer used by the sample provider.</param>
    /// <param name="clientId">The public client identifier used by the sample provider.</param>
    public void UpsertBrokerAlias(string alias, string issuer, string clientId)
    {
        RequireValue(alias, nameof(alias));
        RequireValue(issuer, nameof(issuer));
        RequireValue(clientId, nameof(clientId));
        Update(snapshot =>
        {
            var records = snapshot.BrokerAliases.ToList();
            var matches = records.Where(record => string.Equals(record.Alias, alias, StringComparison.Ordinal)).ToList();
            var replacement = new BrokerAliasRecord(alias, issuer, clientId);
            if (matches.Count == 0)
            {
                records.Add(replacement);
            }
            else
            {
                records[records.IndexOf(matches[0])] = replacement;
            }

            return snapshot with { BrokerAliases = records };
        });
    }

    /// <summary>
    /// Upserts an identity subject mapping by its stable local natural key.
    /// </summary>
    /// <param name="naturalKey">The local identity key.</param>
    /// <param name="subject">The non-secret external subject.</param>
    public void UpsertIdentitySubjectMap(string naturalKey, string subject)
    {
        RequireValue(naturalKey, nameof(naturalKey));
        RequireValue(subject, nameof(subject));
        Update(snapshot =>
        {
            var records = snapshot.IdentitySubjectMaps.ToList();
            var matches = records.Where(record => string.Equals(record.NaturalKey, naturalKey, StringComparison.Ordinal)).ToList();
            var replacement = new IdentitySubjectMapRecord(naturalKey, subject);
            if (matches.Count == 0)
            {
                records.Add(replacement);
            }
            else
            {
                records[records.IndexOf(matches[0])] = replacement;
            }

            return snapshot with { IdentitySubjectMaps = records };
        });
    }

    /// <summary>
    /// Upserts a candidate fixture by its stable natural key.
    /// </summary>
    /// <param name="naturalKey">The candidate fixture natural key.</param>
    /// <param name="subject">The validated identity subject associated with the fixture.</param>
    public void UpsertCandidateFixture(string naturalKey, string subject)
    {
        RequireValue(naturalKey, nameof(naturalKey));
        RequireValue(subject, nameof(subject));
        Update(snapshot =>
        {
            var records = snapshot.CandidateFixtures.ToList();
            var matches = records.Where(record => string.Equals(record.NaturalKey, naturalKey, StringComparison.Ordinal)).ToList();
            var replacement = new CandidateFixtureRecord(naturalKey, subject);
            if (matches.Count == 0)
            {
                records.Add(replacement);
            }
            else
            {
                records[records.IndexOf(matches[0])] = replacement;
            }

            return snapshot with { CandidateFixtures = records };
        });
    }

    private void Update(Func<LocalSeedStoreSnapshot, LocalSeedStoreSnapshot> update)
    {
        var gate = Locks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            WriteSnapshot(update(ReadSnapshotCore()));
        }
        finally
        {
            gate.Release();
        }
    }

    private LocalSeedStoreSnapshot ReadSnapshotCore()
    {
        if (!File.Exists(_path))
        {
            return LocalSeedStoreSnapshot.Empty;
        }

        using var stream = File.OpenRead(_path);
        using var rawDocument = JsonDocument.Parse(stream);
        StoreDocument.ValidateShape(rawDocument.RootElement);
        stream.Position = 0;
        var document = JsonSerializer.Deserialize<StoreDocument>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The store document is empty.");
        var snapshot = document.ToSnapshot();
        snapshot.Validate();
        return snapshot;
    }

    private void WriteSnapshot(LocalSeedStoreSnapshot snapshot)
    {
        snapshot.Validate();
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException("The store path has no directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, StoreDocument.FromSnapshot(snapshot), SerializerOptions);
                stream.Flush(true);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}

/// <summary>
/// Represents a validated read-only view of the local seed store.
/// </summary>
public sealed record LocalSeedStoreSnapshot(
    IReadOnlyList<BrokerAliasRecord> BrokerAliases,
    IReadOnlyList<IdentitySubjectMapRecord> IdentitySubjectMaps,
    IReadOnlyList<CandidateFixtureRecord> CandidateFixtures)
{
    /// <summary>
    /// Gets an empty store snapshot.
    /// </summary>
    public static LocalSeedStoreSnapshot Empty { get; } = new([], [], []);

    /// <summary>
    /// Validates record shape and natural-key uniqueness.
    /// </summary>
    public void Validate()
    {
        if (BrokerAliases is null || IdentitySubjectMaps is null || CandidateFixtures is null)
        {
            throw new InvalidDataException("The store document has a missing record collection.");
        }

        ValidateUnique(BrokerAliases.Select(record => record.Alias), "broker aliases");
        ValidateUnique(IdentitySubjectMaps.Select(record => record.NaturalKey), "identity subject mappings");
        ValidateUnique(CandidateFixtures.Select(record => record.NaturalKey), "candidate fixtures");

        foreach (var record in BrokerAliases)
        {
            RequireRecordValue(record.Alias, "broker alias");
            RequireRecordValue(record.Issuer, "broker issuer");
            RequireRecordValue(record.ClientId, "broker client id");
        }

        foreach (var record in IdentitySubjectMaps)
        {
            RequireRecordValue(record.NaturalKey, "identity mapping key");
            RequireRecordValue(record.Subject, "identity subject");
        }

        foreach (var record in CandidateFixtures)
        {
            RequireRecordValue(record.NaturalKey, "fixture key");
            RequireRecordValue(record.Subject, "fixture subject");
        }
    }

    private static void ValidateUnique(IEnumerable<string> values, string category)
    {
        var list = values.ToList();
        if (list.Any(string.IsNullOrWhiteSpace)
            || list.Count != list.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException($"The store contains duplicate or invalid {category}.");
        }
    }

    private static void RequireRecordValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The store contains an invalid {name}.");
        }
    }
}

/// <summary>
/// Describes one non-secret Keycloak identity-provider alias.
/// </summary>
public sealed record BrokerAliasRecord(string Alias, string Issuer, string ClientId);

/// <summary>
/// Associates a consumer-owned local identity key with an external subject.
/// </summary>
public sealed record IdentitySubjectMapRecord(string NaturalKey, string Subject);

/// <summary>
/// Describes one consumer-owned candidate fixture.
/// </summary>
public sealed record CandidateFixtureRecord(string NaturalKey, string Subject);

internal sealed class StoreDocument
{
    [JsonPropertyName("brokerAliases")]
    public List<BrokerAliasRecord> BrokerAliases { get; set; } = [];

    [JsonPropertyName("identitySubjectMaps")]
    public List<IdentitySubjectMapRecord> IdentitySubjectMaps { get; set; } = [];

    [JsonPropertyName("candidateFixtures")]
    public List<CandidateFixtureRecord> CandidateFixtures { get; set; } = [];

    internal LocalSeedStoreSnapshot ToSnapshot() =>
        new(BrokerAliases, IdentitySubjectMaps, CandidateFixtures);

    internal static StoreDocument FromSnapshot(LocalSeedStoreSnapshot snapshot) =>
        new()
        {
            BrokerAliases = snapshot.BrokerAliases.ToList(),
            IdentitySubjectMaps = snapshot.IdentitySubjectMaps.ToList(),
            CandidateFixtures = snapshot.CandidateFixtures.ToList(),
        };

    internal static void ValidateShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The store document must be a JSON object.");
        }

        EnsureProperties(root, new[] { "brokerAliases", "identitySubjectMaps", "candidateFixtures" }, "store document");
        EnsureArray(root, "brokerAliases", new[] { "alias", "issuer", "clientId" }, "broker alias");
        EnsureArray(root, "identitySubjectMaps", new[] { "naturalKey", "subject" }, "identity subject mapping");
        EnsureArray(root, "candidateFixtures", new[] { "naturalKey", "subject" }, "candidate fixture");
    }

    private static void EnsureArray(
        JsonElement root,
        string propertyName,
        IEnumerable<string> allowedRecordProperties,
        string recordName)
    {
        if (!root.TryGetProperty(propertyName, out var collection)
            || collection.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The store document has an invalid {recordName} collection.");
        }

        foreach (var record in collection.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The store contains an invalid {recordName}.");
            }

            EnsureProperties(record, allowedRecordProperties, recordName);
        }
    }

    private static void EnsureProperties(
        JsonElement value,
        IEnumerable<string> allowedProperties,
        string valueName)
    {
        var allowedPropertySet = allowedProperties.ToHashSet(StringComparer.Ordinal);
        var properties = value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Count != allowedPropertySet.Count || !allowedPropertySet.All(properties.Contains))
        {
            throw new InvalidDataException($"The store contains an incomplete {valueName}.");
        }
    }
}
