using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class DurableFlowRequestFingerprint
{
    internal static byte[] Compute(DurableFlowStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "start-v1",
            request.ScopeId.Value,
            request.InstanceId.Value,
            request.FlowId,
            request.FlowVersion,
            PayloadIdentity(request.Context));
    }

    internal static byte[] Compute(DurableFlowStartRequest request, DurableFlowRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        return Compute(
            "start-manifest-v1",
            request.ScopeId.Value,
            request.InstanceId.Value,
            request.FlowId,
            request.FlowVersion,
            registration.AuthoringModel,
            registration.DefinitionFingerprint,
            PayloadIdentity(request.Context));
    }

    internal static byte[] Compute(DurableFlowEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "event-v1",
            request.ScopeId.Value,
            request.InstanceId.Value,
            request.EventName,
            request.ExpectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "",
            request.Payload is null ? "" : PayloadIdentity(request.Payload));
    }

    internal static byte[] Compute(DurableFlowCancelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "cancel-v1",
            request.ScopeId.Value,
            request.InstanceId.Value,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
    }

    internal static byte[] Compute(DurableFlowReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "release-v1",
            request.ScopeId.Value,
            request.InstanceId.Value,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
    }

    internal static string CreateActivityIdentity(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long transitionRevision,
        string callsiteId)
    {
        var hash = Compute(
            "flow-activity-v1",
            scopeId.Value,
            instanceId.Value,
            transitionRevision.ToString(CultureInfo.InvariantCulture),
            callsiteId);
        return $"flow-activity-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    private static string PayloadIdentity(DurableEncodedPayload payload) =>
        string.Join(
            "|",
            payload.ContractName,
            payload.ContractVersion,
            ((int)payload.Classification).ToString(CultureInfo.InvariantCulture),
            payload.RetentionPolicyId,
            payload.Sha256);

    private static byte[] Compute(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return hash.GetHashAndReset();
    }
}
