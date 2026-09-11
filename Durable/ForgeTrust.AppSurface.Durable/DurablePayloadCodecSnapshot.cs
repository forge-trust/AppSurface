namespace ForgeTrust.AppSurface.Durable;

/// <summary>Captures validated codec facts separately from caller-owned executable behavior.</summary>
/// <remarks>Capture reads each metadata getter once. Views never poll or dispose the source; callers own its concurrency safety.</remarks>
internal sealed class DurablePayloadCodecSnapshot
{
    /// <summary>Complete immutable facts used for identity, indexing and payload guards.</summary>
    internal sealed record Metadata(Type PayloadType, string ContractName, string ContractVersion,
        DurableDataClassification Classification, string RetentionPolicyId);

    private readonly Func<DurablePayloadCodecSnapshot, IDurablePayloadCodec> _viewFactory;

    private DurablePayloadCodecSnapshot(IDurablePayloadCodec source, Metadata facts,
        Func<DurablePayloadCodecSnapshot, IDurablePayloadCodec> viewFactory)
    {
        Source = source;
        Facts = facts;
        _viewFactory = viewFactory;
    }

    /// <summary>Gets the exact caller-owned source; metadata equality never substitutes for this identity.</summary>
    internal IDurablePayloadCodec Source { get; }
    /// <summary>Gets the facts captured once from the source.</summary>
    internal Metadata Facts { get; }
    /// <summary>Creates an independent guarded projection without rereading metadata.</summary>
    internal IDurablePayloadCodec CreateView() => _viewFactory(this);

    /// <summary>Captures an untyped raw source or reuses a package-owned view's original snapshot.</summary>
    internal static DurablePayloadCodecSnapshot Capture(IDurablePayloadCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return GetSnapshot(codec) ?? new(codec, ReadMetadata(codec), static snapshot => new View(snapshot));
    }

    /// <summary>Captures a typed source and verifies its declared CLR type independently of its contract namespace.</summary>
    internal static DurablePayloadCodecSnapshot Capture<T>(IDurablePayloadCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var snapshot = GetSnapshot(codec) ?? new(codec, ReadMetadata(codec), static value => new TypedView<T>(value));
        if (snapshot.Facts.PayloadType != typeof(T))
        {
            throw new ArgumentException("The durable payload codec must declare the exact generic payload type.", nameof(codec));
        }

        return snapshot;
    }

    /// <summary>Retrieves provenance only from an unforgeable package implementation.</summary>
    internal static DurablePayloadCodecSnapshot? GetSnapshot(IDurablePayloadCodec codec) => (codec as View)?.Snapshot;

    /// <summary>Accepts exact references or package projections of the same source and complete captured facts.</summary>
    internal static bool AreCompatible(IDurablePayloadCodec expected, IDurablePayloadCodec actual)
    {
        if (ReferenceEquals(expected, actual))
        {
            return true;
        }

        var left = GetSnapshot(expected);
        var right = GetSnapshot(actual);
        if (left is not null && right is not null)
        {
            return ReferenceEquals(left.Source, right.Source) && left.Facts == right.Facts;
        }

        return left is not null ? ReferenceEquals(left.Source, actual)
            : right is not null && ReferenceEquals(right.Source, expected);
    }

    /// <summary>Retains captured guards when a compatible registry selects the original raw source.</summary>
    /// <remarks>Call after compatibility validation. A selected package view remains authoritative; otherwise an
    /// expected view invokes that same selected source without rereading metadata or changing registration state.</remarks>
    internal static IDurablePayloadCodec RetainGuardedView(IDurablePayloadCodec expected, IDurablePayloadCodec selected) =>
        GetSnapshot(selected) is null && GetSnapshot(expected) is not null ? expected : selected;

    private static Metadata ReadMetadata(IDurablePayloadCodec codec)
    {
        var type = codec.PayloadType;
        var name = codec.ContractName;
        var version = codec.ContractVersion;
        var classification = codec.Classification;
        var retention = codec.RetentionPolicyId;
        ArgumentNullException.ThrowIfNull(type);
        DurableIdentifier.Require(name, nameof(codec.ContractName), 200);
        DurableIdentifier.Require(version, nameof(codec.ContractVersion), 100);
        DurableIdentifier.Require(retention, nameof(codec.RetentionPolicyId), 128);
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(nameof(codec.Classification));
        }

        return new(type, name, version, classification, retention);
    }

    /// <summary>Rejects incompatible payloads before decode and after encode without changing bytes or exposing values.</summary>
    private DurableEncodedPayload RequirePayload(DurableEncodedPayload? payload)
    {
        if (payload is null || payload.ContractName != Facts.ContractName || payload.ContractVersion != Facts.ContractVersion
            || payload.Classification != Facts.Classification || payload.RetentionPolicyId != Facts.RetentionPolicyId)
        {
            throw new InvalidOperationException("The durable payload does not match the captured codec contract. Preserve the defined codec metadata.");
        }

        return payload;
    }

    /// <summary>Untyped projection used by registry-only sources and untyped provider boundaries.</summary>
    private class View(DurablePayloadCodecSnapshot snapshot) : IDurablePayloadCodec
    {
        internal DurablePayloadCodecSnapshot Snapshot { get; } = snapshot;
        public Type PayloadType => Snapshot.Facts.PayloadType;
        public string ContractName => Snapshot.Facts.ContractName;
        public string ContractVersion => Snapshot.Facts.ContractVersion;
        public DurableDataClassification Classification => Snapshot.Facts.Classification;
        public string RetentionPolicyId => Snapshot.Facts.RetentionPolicyId;
        public DurableEncodedPayload EncodeObject(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!PayloadType.IsInstanceOfType(value))
            {
                throw new ArgumentException("The value must match the captured durable payload type.", nameof(value));
            }

            return Snapshot.RequirePayload(Snapshot.Source.EncodeObject(value));
        }

        public object DecodeObject(DurableEncodedPayload payload)
        {
            var value = Snapshot.Source.DecodeObject(Snapshot.RequirePayload(payload));
            if (value is null || !PayloadType.IsInstanceOfType(value))
            {
                throw new InvalidOperationException("The durable codec returned an invalid decoded payload type.");
            }

            return value;
        }
    }

    /// <summary>Typed projection preserving the source's typed invocation boundary and payload guards.</summary>
    private sealed class TypedView<T>(DurablePayloadCodecSnapshot snapshot) : View(snapshot), IDurablePayloadCodec<T>
    {
        public DurableEncodedPayload Encode(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return Snapshot.RequirePayload(((IDurablePayloadCodec<T>)Snapshot.Source).Encode(value));
        }

        public T Decode(DurableEncodedPayload payload)
        {
            var value = ((IDurablePayloadCodec<T>)Snapshot.Source).Decode(Snapshot.RequirePayload(payload));
            return value is not null ? value
                : throw new InvalidOperationException("The durable codec returned an invalid decoded payload type.");
        }
    }
}

/// <summary>A closed codec contribution; frozen projections are required only for definition-based public paths.</summary>
internal sealed record DurableCodecContribution(DurablePayloadCodecSnapshot Snapshot, bool RequiresFrozenView);
