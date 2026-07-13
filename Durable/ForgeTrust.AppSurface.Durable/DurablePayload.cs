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
        var value = JsonSerializer.Deserialize(payload.Content.Span, _typeInfo);
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
    private readonly Dictionary<Type, List<IDurablePayloadCodec>> _byType = new();
    private readonly Dictionary<(string Name, string Version), IDurablePayloadCodec> _byContract = new();

    /// <summary>
    /// Initializes an empty registry for manual registration.
    /// </summary>
    public DurablePayloadCodecRegistry()
    {
    }

    /// <summary>
    /// Initializes a registry from codecs contributed through dependency injection.
    /// </summary>
    /// <param name="codecs">Registered codec instances.</param>
    public DurablePayloadCodecRegistry(IEnumerable<IDurablePayloadCodec> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        foreach (var codec in codecs)
        {
            Register(codec);
        }
    }

    /// <inheritdoc />
    public void Register(IDurablePayloadCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        lock (_gate)
        {
            var key = (codec.ContractName, codec.ContractVersion);
            if (_byContract.TryGetValue(key, out var contractExisting) && !ReferenceEquals(contractExisting, codec))
            {
                throw new InvalidOperationException($"Durable contract '{key.ContractName}' version '{key.ContractVersion}' is already registered.");
            }

            if (ReferenceEquals(contractExisting, codec))
            {
                return;
            }

            if (!_byType.TryGetValue(codec.PayloadType, out var codecs))
            {
                codecs = [];
                _byType.Add(codec.PayloadType, codecs);
            }

            codecs.Add(codec);
            _byContract[key] = codec;
        }
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        lock (_gate)
        {
            if (!_byType.TryGetValue(payloadType, out var codecs))
            {
                throw new InvalidOperationException($"No durable payload codec is registered for '{payloadType.FullName}'.");
            }

            return codecs.Count == 1
                ? codecs[0]
                : throw new InvalidOperationException(
                    $"More than one durable payload codec is registered for '{payloadType.FullName}'; select an exact contract name and version.");
        }
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        var codec = GetRequired(contractName, contractVersion);
        return codec.PayloadType == payloadType
            ? codec
            : throw new InvalidOperationException(
                $"Durable contract '{codec.ContractName}' version '{codec.ContractVersion}' is registered for '{codec.PayloadType.FullName}', not '{payloadType.FullName}'.");
    }

    /// <inheritdoc />
    public IDurablePayloadCodec GetRequired(string contractName, string contractVersion)
    {
        var name = DurableIdentifier.Require(contractName, nameof(contractName), 200);
        var version = DurableIdentifier.Require(contractVersion, nameof(contractVersion), 100);
        lock (_gate)
        {
            return _byContract.TryGetValue((name, version), out var codec)
                ? codec
                : throw new InvalidOperationException($"No durable payload codec is registered for '{name}' version '{version}'.");
        }
    }
}
