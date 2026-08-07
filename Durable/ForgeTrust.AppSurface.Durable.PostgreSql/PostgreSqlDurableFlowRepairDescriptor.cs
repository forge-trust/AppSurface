using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class PostgreSqlDurableFlowRepairDescriptor
{
    internal const string SchemaId = "appsurface.durable.flow.child-suspension.v1";

    internal static string CreateDigest(
        string suspendedFromState,
        string code,
        string source,
        string workState,
        Guid waitId,
        DurableWorkId workId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SchemaId);
        Append(hash, suspendedFromState);
        Append(hash, code);
        Append(hash, source);
        Append(hash, workState);
        Append(hash, waitId.ToString("D").ToLowerInvariant());
        Append(hash, workId.Value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
