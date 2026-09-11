using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Tests;

internal sealed class DurableBindingRegistryTestCodec<T> : IDurablePayloadCodec<T>
{
    private readonly Func<T, DurableEncodedPayload> _encode;
    private readonly Func<DurableEncodedPayload, T> _decode;
    private readonly ConcurrentDictionary<string, int>? _getterCounts;
    private int _encodeCalls;
    private int _decodeCalls;

    internal DurableBindingRegistryTestCodec(string name, string version = "v1",
        DurableDataClassification classification = DurableDataClassification.Operational,
        string retention = DurableEncodedPayload.DefaultRetentionPolicyId,
        Type? payloadType = null,
        Func<T, DurableEncodedPayload>? encode = null,
        Func<DurableEncodedPayload, T>? decode = null,
        ConcurrentDictionary<string, int>? getterCounts = null)
    {
        ContractNameValue = name;
        ContractVersionValue = version;
        ClassificationValue = classification;
        RetentionValue = retention;
        PayloadTypeValue = payloadType ?? typeof(T);
        _getterCounts = getterCounts;
        _encode = encode ?? (value => new DurableEncodedPayload(name, version, classification,
            Encoding.UTF8.GetBytes(value is null ? string.Empty : value.ToString()!), retention));
        _decode = decode ?? (payload => (T)(object)Encoding.UTF8.GetString(payload.Content.Span));
    }

    internal string ContractNameValue { get; set; }
    internal string ContractVersionValue { get; set; }
    internal DurableDataClassification ClassificationValue { get; set; }
    internal string RetentionValue { get; set; }
    internal Type PayloadTypeValue { get; set; }
    internal int EncodeCalls => Volatile.Read(ref _encodeCalls);
    internal int DecodeCalls => Volatile.Read(ref _decodeCalls);

    public Type PayloadType { get { Count(nameof(PayloadType)); return PayloadTypeValue; } }
    public string ContractName { get { Count(nameof(ContractName)); return ContractNameValue; } }
    public string ContractVersion { get { Count(nameof(ContractVersion)); return ContractVersionValue; } }
    public DurableDataClassification Classification { get { Count(nameof(Classification)); return ClassificationValue; } }
    public string RetentionPolicyId { get { Count(nameof(RetentionPolicyId)); return RetentionValue; } }
    public DurableEncodedPayload Encode(T value)
    {
        Interlocked.Increment(ref _encodeCalls);
        return _encode(value);
    }
    public T Decode(DurableEncodedPayload payload)
    {
        Interlocked.Increment(ref _decodeCalls);
        return _decode(payload);
    }
    public DurableEncodedPayload EncodeObject(object value) => Encode((T)value);
    public object DecodeObject(DurableEncodedPayload payload) => Decode(payload)!;

    private void Count(string name) => _getterCounts?.AddOrUpdate(name, 1, static (_, value) => value + 1);
}

internal sealed class DurableBindingRegistryTestExecutor : IDurableWorkerExecutor<string, string>
{
    internal DurableWorkerEnvelope<string>? Observed { get; private set; }
    internal int Calls { get; private set; }

    public ValueTask<string> ExecuteAsync(DurableWorkerEnvelope<string> work,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        Observed = work;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult($"result:{work.Payload}");
    }
}

internal sealed class DurableBindingRegistryTestReconciler : IDurableEffectReconciler<string, string>
{
    private readonly DurableEffectReconciliation<string> _outcome;

    public DurableBindingRegistryTestReconciler()
        : this(DurableEffectReconciliation<string>.Unknown())
    {
    }

    internal DurableBindingRegistryTestReconciler(DurableEffectReconciliation<string> outcome)
    {
        _outcome = outcome;
    }

    internal DurableWorkerEnvelope<string>? Observed { get; private set; }
    public ValueTask<DurableEffectReconciliation<string>> ReconcileAsync(
        DurableWorkerEnvelope<string> work, CancellationToken cancellationToken = default)
    {
        Observed = work;
        return ValueTask.FromResult(_outcome);
    }
}

internal sealed class DurableBindingRegistryTestExitExecutor(DurableWorkExit<string> exit) :
    IDurableWorkExitExecutor<string, string>
{
    internal DurableWorkerEnvelope<string>? Observed { get; private set; }
    public ValueTask<DurableWorkExit<string>> ExecuteAsync(
        DurableWorkerEnvelope<string> work, CancellationToken cancellationToken = default)
    {
        Observed = work;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(exit);
    }
}
