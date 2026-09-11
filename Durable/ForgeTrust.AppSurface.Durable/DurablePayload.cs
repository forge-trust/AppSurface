using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Classifies data that has been explicitly approved for durable persistence.
/// </summary>
public enum DurableDataClassification
{
    /// <summary>
    /// Opaque identifiers, safe codes, timestamps, counts, and other operational metadata.
    /// </summary>
    Operational = 0,

    /// <summary>
    /// Application data that a registered codec and policy explicitly approve for durable storage.
    /// </summary>
    ApprovedApplication = 1,
}

/// <summary>
/// Carries an immutable, versioned, allowlisted payload ready for durable persistence.
/// </summary>
public sealed record DurableEncodedPayload
{
    /// <summary>Gets the default application-owned retention policy identifier.</summary>
    public const string DefaultRetentionPolicyId = "application-default";

    /// <summary>
    /// Maximum encoded payload size supported by the first durable protocol version.
    /// </summary>
    public const int ProtocolMaximumBytes = 262_144;

    private readonly byte[] _content;

    /// <summary>
    /// Initializes a new encoded payload.
    /// </summary>
    /// <param name="contractName">Stable registered contract name.</param>
    /// <param name="contractVersion">Stable registered contract version.</param>
    /// <param name="classification">Approved durable-data classification.</param>
    /// <param name="content">Canonical encoded bytes.</param>
    /// <param name="retentionPolicyId">Stable application-owned retention policy snapshotted with the payload.</param>
    public DurableEncodedPayload(
        string contractName,
        string contractVersion,
        DurableDataClassification classification,
        ReadOnlyMemory<byte> content,
        string retentionPolicyId = DefaultRetentionPolicyId)
    {
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(nameof(classification));
        }

        if (content.Length > ProtocolMaximumBytes)
        {
            throw new ArgumentException(
                $"Durable payloads must not exceed {ProtocolMaximumBytes} encoded bytes.",
                nameof(content));
        }

        ContractName = DurableIdentifier.Require(contractName, nameof(contractName), 200);
        ContractVersion = DurableIdentifier.Require(contractVersion, nameof(contractVersion), 100);
        Classification = classification;
        RetentionPolicyId = DurableIdentifier.Require(retentionPolicyId, nameof(retentionPolicyId), 128);
        _content = content.ToArray();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_content));
    }

    /// <summary>Compares payload metadata and canonical content bytes by value.</summary>
    /// <param name="other">Payload to compare.</param>
    /// <returns><see langword="true"/> when metadata and content bytes are equal.</returns>
    public bool Equals(DurableEncodedPayload? other) =>
        other is not null
        && string.Equals(ContractName, other.ContractName, StringComparison.Ordinal)
        && string.Equals(ContractVersion, other.ContractVersion, StringComparison.Ordinal)
        && Classification == other.Classification
        && string.Equals(RetentionPolicyId, other.RetentionPolicyId, StringComparison.Ordinal)
        && _content.AsSpan().SequenceEqual(other._content);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContractName, StringComparer.Ordinal);
        hash.Add(ContractVersion, StringComparer.Ordinal);
        hash.Add(Classification);
        hash.Add(RetentionPolicyId, StringComparer.Ordinal);
        hash.Add(Sha256, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    /// <summary>
    /// Gets the stable registered contract name.
    /// </summary>
    public string ContractName { get; }

    /// <summary>
    /// Gets the stable registered contract version.
    /// </summary>
    public string ContractVersion { get; }

    /// <summary>
    /// Gets the approved durable-data classification.
    /// </summary>
    public DurableDataClassification Classification { get; }

    /// <summary>Gets the stable retention policy identity snapshotted by the registered codec.</summary>
    public string RetentionPolicyId { get; }

    /// <summary>
    /// Gets a copy-safe view of the canonical encoded bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Content => _content.ToArray();

    /// <summary>
    /// Gets the lowercase SHA-256 hash of the exact encoded bytes.
    /// </summary>
    public string Sha256 { get; }
}

/// <summary>
/// Encodes and decodes one registered durable payload contract without runtime type-name serialization.
/// </summary>
public interface IDurablePayloadCodec
{
    /// <summary>
    /// Gets the supported CLR payload type.
    /// </summary>
    Type PayloadType { get; }

    /// <summary>
    /// Gets the stable contract name.
    /// </summary>
    string ContractName { get; }

    /// <summary>
    /// Gets the stable contract version.
    /// </summary>
    string ContractVersion { get; }

    /// <summary>Gets the exact classification this codec accepts and emits.</summary>
    DurableDataClassification Classification { get; }

    /// <summary>Gets the stable application-owned retention policy identity this codec accepts and emits.</summary>
    string RetentionPolicyId { get; }

    /// <summary>
    /// Encodes a value after applying its registration-time data policy.
    /// </summary>
    DurableEncodedPayload EncodeObject(object value);

    /// <summary>
    /// Decodes bytes only when their contract identity exactly matches this codec.
    /// </summary>
    object DecodeObject(DurableEncodedPayload payload);
}

/// <summary>
/// Strongly typed durable payload codec.
/// </summary>
/// <typeparam name="T">Registered payload type.</typeparam>
public interface IDurablePayloadCodec<T> : IDurablePayloadCodec
{
    /// <summary>
    /// Encodes a typed payload.
    /// </summary>
    DurableEncodedPayload Encode(T value);

    /// <summary>
    /// Decodes a typed payload.
    /// </summary>
    T Decode(DurableEncodedPayload payload);
}

/// <summary>
/// Source-generation-friendly JSON codec for an explicitly registered payload type.
/// </summary>
/// <typeparam name="T">Registered payload type.</typeparam>
public sealed class SystemTextJsonDurablePayloadCodec<T> : IDurablePayloadCodec<T>
{
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T, bool> _isApproved;
    private readonly int _maximumBytes;

    /// <summary>
    /// Initializes a new JSON durable payload codec.
    /// </summary>
    /// <param name="contractName">Stable contract name.</param>
    /// <param name="contractVersion">Stable contract version.</param>
    /// <param name="classification">Approved data classification.</param>
    /// <param name="typeInfo">Source-generated JSON metadata.</param>
    /// <param name="isApproved">Policy that rejects values unsafe for durable storage.</param>
    /// <param name="maximumBytes">Contract-specific encoded byte limit.</param>
    /// <param name="retentionPolicyId">Stable application-owned retention policy identity.</param>
    public SystemTextJsonDurablePayloadCodec(
        string contractName,
        string contractVersion,
        DurableDataClassification classification,
        JsonTypeInfo<T> typeInfo,
        Func<T, bool> isApproved,
        int maximumBytes = DurableEncodedPayload.ProtocolMaximumBytes,
        string retentionPolicyId = DurableEncodedPayload.DefaultRetentionPolicyId)
    {
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(nameof(classification));
        }

        if (maximumBytes is < 1 or > DurableEncodedPayload.ProtocolMaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        ContractName = DurableIdentifier.Require(contractName, nameof(contractName), 200);
        ContractVersion = DurableIdentifier.Require(contractVersion, nameof(contractVersion), 100);
        Classification = classification;
        RetentionPolicyId = DurableIdentifier.Require(retentionPolicyId, nameof(retentionPolicyId), 128);
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
        _isApproved = isApproved ?? throw new ArgumentNullException(nameof(isApproved));
        _maximumBytes = maximumBytes;
    }

    /// <inheritdoc />
    public Type PayloadType => typeof(T);

    /// <inheritdoc />
    public string ContractName { get; }

    /// <inheritdoc />
    public string ContractVersion { get; }

    /// <summary>
    /// Gets the approved data classification.
    /// </summary>
    public DurableDataClassification Classification { get; }

    /// <inheritdoc />
    public string RetentionPolicyId { get; }

    /// <inheritdoc />
    public DurableEncodedPayload Encode(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_isApproved(value))
        {
            throw new ArgumentException("The payload policy rejected this value for durable persistence.", nameof(value));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _typeInfo);
        if (bytes.Length > _maximumBytes)
        {
            throw new ArgumentException($"Encoded payload exceeds the registered {_maximumBytes}-byte limit.", nameof(value));
        }

        return new DurableEncodedPayload(ContractName, ContractVersion, Classification, bytes, RetentionPolicyId);
    }

    /// <inheritdoc />
    public T Decode(DurableEncodedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireMatchingContract(payload);
        var content = payload.Content;
        if (content.Length > _maximumBytes)
        {
            throw new JsonException($"Encoded durable payload exceeds the registered {_maximumBytes}-byte limit.");
        }

        var value = JsonSerializer.Deserialize(content.Span, _typeInfo);
        bool isApproved;
        try
        {
            isApproved = value is not null && _isApproved(value);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            throw new JsonException(
                "The decoded durable payload could not be evaluated by its registered data policy.",
                exception);
        }

        if (!isApproved)
        {
            throw new JsonException("The decoded durable payload is null or no longer satisfies its registered policy.");
        }

        return value!;
    }

    /// <inheritdoc />
    public DurableEncodedPayload EncodeObject(object value)
    {
        if (value is not T typed)
        {
            throw new ArgumentException($"Expected payload type '{typeof(T).FullName}'.", nameof(value));
        }

        return Encode(typed);
    }

    /// <inheritdoc />
    public object DecodeObject(DurableEncodedPayload payload) => Decode(payload)!;

    private void RequireMatchingContract(DurableEncodedPayload payload)
    {
        if (!string.Equals(payload.ContractName, ContractName, StringComparison.Ordinal)
            || !string.Equals(payload.ContractVersion, ContractVersion, StringComparison.Ordinal)
            || payload.Classification != Classification
            || !string.Equals(payload.RetentionPolicyId, RetentionPolicyId, StringComparison.Ordinal))
        {
            throw new JsonException(
                "The encoded payload contract, classification, or retention policy does not match the registered codec.");
        }
    }
}

/// <summary>
/// Resolves only explicitly registered durable payload codecs.
/// </summary>
public interface IDurablePayloadCodecRegistry
{
    /// <summary>
    /// Registers one codec by CLR type and durable contract identity.
    /// </summary>
    void Register(IDurablePayloadCodec codec);

    /// <summary>
    /// Gets the required codec for a CLR type.
    /// </summary>
    IDurablePayloadCodec GetRequired(Type payloadType);

    /// <summary>
    /// Gets a required codec for an exact CLR type and persisted contract identity.
    /// </summary>
    IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion);

    /// <summary>
    /// Gets the required codec for persisted contract identity.
    /// </summary>
    IDurablePayloadCodec GetRequired(string contractName, string contractVersion);
}

/// <summary>
/// Thread-safe in-memory registry for explicitly allowlisted durable payload codecs.
/// </summary>
public sealed class DurablePayloadCodecRegistry : IDurablePayloadCodecRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Entry>> _byType = new();
    private readonly Dictionary<(string Name, string Version), Entry> _byContract = new();
    private readonly Dictionary<IDurablePayloadCodec, Entry> _bySource = new(ReferenceEqualityComparer.Instance);

    /// <summary>One entry is shared by all indexes; an upgrade publishes its canonical view atomically under the registry lock.</summary>
    private sealed class Entry(DurablePayloadCodecSnapshot snapshot, bool frozen)
    {
        internal DurablePayloadCodecSnapshot Snapshot { get; } = snapshot;
        internal bool Frozen { get; private set; } = frozen;
        internal IDurablePayloadCodec Codec { get; private set; } = frozen ? snapshot.CreateView() : snapshot.Source;

        /// <summary>Creates before publishing so a failed upgrade leaves every index unchanged.</summary>
        internal void Upgrade(DurablePayloadCodecSnapshot incoming)
        {
            if (!Frozen)
            {
                var view = incoming.CreateView();
                Codec = view;
                Frozen = true;
            }
        }
    }

    /// <summary>Initializes an empty registry for manual registration.</summary>
    public DurablePayloadCodecRegistry()
    {
    }

    /// <summary>Aggregates explicitly contributed codecs before exposing any lookup result.</summary>
    /// <param name="codecs">Raw codecs or package-owned snapshot views.</param>
    /// <remarks>Raw-only entries preserve source references. Frozen entries use a registry-owned guarded view.</remarks>
    public DurablePayloadCodecRegistry(IEnumerable<IDurablePayloadCodec> codecs) : this([], codecs)
    {
    }

    /// <summary>Validates all same-source snapshots before deterministic contract-collision checks and index publication.</summary>
    internal DurablePayloadCodecRegistry(IEnumerable<DurableCodecContribution> contributions, IEnumerable<IDurablePayloadCodec> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        var sources = new Dictionary<IDurablePayloadCodec, (DurablePayloadCodecSnapshot Snapshot, bool Frozen)>(ReferenceEqualityComparer.Instance);
        var sourceConflict = false;
        void Include(DurablePayloadCodecSnapshot snapshot, bool frozen)
        {
            if (sources.TryGetValue(snapshot.Source, out var existing))
            {
                sourceConflict |= existing.Snapshot.Facts != snapshot.Facts;
                sources[snapshot.Source] = (!existing.Frozen && frozen ? snapshot : existing.Snapshot, existing.Frozen || frozen);
            }
            else
            {
                sources.Add(snapshot.Source, (snapshot, frozen));
            }
        }

        foreach (var contribution in contributions)
        {
            Include(contribution.Snapshot, contribution.RequiresFrozenView);
        }

        // Discover every supplied snapshot before considering raw descriptors, regardless of enumeration order.
        var publicCodecs = codecs.ToArray();
        foreach (var codec in publicCodecs)
        {
            ArgumentNullException.ThrowIfNull(codec);
            if (DurablePayloadCodecSnapshot.GetSnapshot(codec) is { } view)
            {
                Include(view, true);
            }
        }

        foreach (var codec in publicCodecs)
        {
            if (DurablePayloadCodecSnapshot.GetSnapshot(codec) is null && !sources.ContainsKey(codec))
            {
                Include(DurablePayloadCodecSnapshot.Capture(codec), false);
            }
        }

        if (sourceConflict)
        {
            throw SourceConflict();
        }

        // Validate contract collisions in a linear pass. Select the ordinal-first conflict so diagnostics
        // are stable across contribution order without sorting successful registry construction.
        var contracts = new HashSet<(string Name, string Version)>();
        (string Name, string Version)? conflict = null;
        foreach (var item in sources.Values)
        {
            var key = (Name: item.Snapshot.Facts.ContractName, Version: item.Snapshot.Facts.ContractVersion);
            if (!contracts.Add(key) && (conflict is null ||
                StringComparer.Ordinal.Compare(key.Name, conflict.Value.Name) < 0 ||
                (key.Name == conflict.Value.Name && StringComparer.Ordinal.Compare(key.Version, conflict.Value.Version) < 0)))
            {
                conflict = key;
            }
        }

        if (conflict is { } duplicate)
        {
            throw ContractConflict(duplicate.Name, duplicate.Version);
        }

        foreach (var item in sources.Values)
        {
            AddEntry(new Entry(item.Snapshot, item.Frozen));
        }
    }

    /// <inheritdoc />
    /// <remarks>Equivalent raw/view registrations are idempotent. A frozen view upgrades a raw entry without downgrades;
    /// references returned before a manual upgrade remain caller-owned references.</remarks>
    public void Register(IDurablePayloadCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        lock (_gate)
        {
            var view = DurablePayloadCodecSnapshot.GetSnapshot(codec);
            var source = view?.Source ?? codec;
            if (_bySource.TryGetValue(source, out var existing))
            {
                if (view is not null)
                {
                    if (existing.Snapshot.Facts != view.Facts)
                    {
                        throw SourceConflict();
                    }

                    existing.Upgrade(view);
                }

                return;
            }

            var snapshot = view ?? DurablePayloadCodecSnapshot.Capture(codec);
            var facts = snapshot.Facts;
            if (_byContract.ContainsKey((facts.ContractName, facts.ContractVersion)))
            {
                throw ContractConflict(facts.ContractName, facts.ContractVersion);
            }

            AddEntry(new Entry(snapshot, view is not null));
        }
    }

    /// <summary>Populates both indexes with one validated entry; caller holds the lock or owns an unpublished registry.</summary>
    private void AddEntry(Entry entry)
    {
        var facts = entry.Snapshot.Facts;
        if (!_byType.TryGetValue(facts.PayloadType, out var entries))
        {
            entries = [];
            _byType.Add(facts.PayloadType, entries);
        }

        entries.Add(entry);
        _bySource.Add(entry.Snapshot.Source, entry);
        _byContract.Add((facts.ContractName, facts.ContractVersion), entry);
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        lock (_gate)
        {
            if (!_byType.TryGetValue(payloadType, out var entries))
            {
                throw new InvalidOperationException($"No durable payload codec is registered for '{payloadType.FullName}'.");
            }

            return entries.Count == 1 ? entries[0].Codec
                : throw new InvalidOperationException(
                    $"More than one durable payload codec is registered for '{payloadType.FullName}'; select an exact contract name and version.");
        }
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        lock (_gate)
        {
            var entry = GetEntry(contractName, contractVersion);
            var facts = entry.Snapshot.Facts;
            return facts.PayloadType == payloadType ? entry.Codec
                : throw new InvalidOperationException(
                    $"Durable contract '{facts.ContractName}' version '{facts.ContractVersion}' is registered for '{facts.PayloadType.FullName}', not '{payloadType.FullName}'.");
        }
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(string contractName, string contractVersion)
    {
        lock (_gate)
        {
            return GetEntry(contractName, contractVersion).Codec;
        }
    }

    /// <summary>Resolves by validated identifiers using captured metadata; caller holds the registry lock.</summary>
    private Entry GetEntry(string contractName, string contractVersion)
    {
        var name = DurableIdentifier.Require(contractName, nameof(contractName), 200);
        var version = DurableIdentifier.Require(contractVersion, nameof(contractVersion), 100);
        return _byContract.TryGetValue((name, version), out var entry) ? entry
            : throw new InvalidOperationException($"No durable payload codec is registered for '{name}' version '{version}'.");
    }

    private static InvalidOperationException SourceConflict() =>
        new("A durable payload codec source was contributed with conflicting contract metadata.");

    private static InvalidOperationException ContractConflict(string name, string version) =>
        new($"Durable contract '{name}' version '{version}' is already registered.");
}
