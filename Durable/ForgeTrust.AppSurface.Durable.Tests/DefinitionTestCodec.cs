using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

internal sealed class DefinitionTestCodec<T> : IDurablePayloadCodec<T>
{
    private readonly Func<T, DurableEncodedPayload> _encode;
    private readonly Func<DurableEncodedPayload, T> _decode;
    private readonly Func<string, Exception?>? _getterFailure;

    internal DefinitionTestCodec(
        string contractName = "tests.typed.payload",
        string contractVersion = "v1",
        DurableDataClassification classification = DurableDataClassification.Operational,
        string retentionPolicyId = DurableEncodedPayload.DefaultRetentionPolicyId,
        Type? payloadType = null,
        bool returnNullPayloadType = false,
        Func<T, DurableEncodedPayload>? encode = null,
        Func<DurableEncodedPayload, T>? decode = null,
        ConcurrentDictionary<string, int>? getterCounts = null,
        Func<string, Exception?>? getterFailure = null)
    {
        ContractNameValue = contractName;
        ContractVersionValue = contractVersion;
        ClassificationValue = classification;
        RetentionPolicyIdValue = retentionPolicyId;
        PayloadTypeValue = returnNullPayloadType ? null! : payloadType ?? typeof(T);
        _encode = encode ?? (value => new DurableEncodedPayload(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value is null ? string.Empty : value.ToString()!),
            RetentionPolicyId));
        _decode = decode ?? (payload => (T)(object)Encoding.UTF8.GetString(payload.Content.Span));
        GetterCounts = getterCounts;
        _getterFailure = getterFailure;
    }

    internal string ContractNameValue { get; set; }
    internal string ContractVersionValue { get; set; }
    internal DurableDataClassification ClassificationValue { get; set; }
    internal string RetentionPolicyIdValue { get; set; }
    internal Type PayloadTypeValue { get; set; }
    internal ConcurrentDictionary<string, int>? GetterCounts { get; }
    internal int EncodeCalls;
    internal int DecodeCalls;

    public Type PayloadType
    {
        get { Count(nameof(PayloadType)); ThrowIfConfigured(nameof(PayloadType)); return PayloadTypeValue; }
    }

    public string ContractName
    {
        get { Count(nameof(ContractName)); ThrowIfConfigured(nameof(ContractName)); return ContractNameValue; }
    }

    public string ContractVersion
    {
        get { Count(nameof(ContractVersion)); ThrowIfConfigured(nameof(ContractVersion)); return ContractVersionValue; }
    }

    public DurableDataClassification Classification
    {
        get { Count(nameof(Classification)); ThrowIfConfigured(nameof(Classification)); return ClassificationValue; }
    }

    public string RetentionPolicyId
    {
        get { Count(nameof(RetentionPolicyId)); ThrowIfConfigured(nameof(RetentionPolicyId)); return RetentionPolicyIdValue; }
    }

    public DurableEncodedPayload Encode(T value)
    {
        Interlocked.Increment(ref EncodeCalls);
        return _encode(value);
    }

    public T Decode(DurableEncodedPayload payload)
    {
        Interlocked.Increment(ref DecodeCalls);
        return _decode(payload);
    }

    public DurableEncodedPayload EncodeObject(object value) => Encode((T)value);

    public object DecodeObject(DurableEncodedPayload payload) => Decode(payload)!;

    private void Count(string property) => GetterCounts?.AddOrUpdate(property, 1, static (_, count) => count + 1);

    private void ThrowIfConfigured(string property)
    {
        var failure = _getterFailure?.Invoke(property);
        if (failure is not null)
        {
            throw failure;
        }
    }
}
