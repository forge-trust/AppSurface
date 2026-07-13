using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class DurableProviderKey
{
    private const string DomainSeparator = "ForgeTrust.AppSurface.Durable/provider-key/v1";

    internal static string Create(DurableScopeId scopeId, string activityId)
    {
        var hash = ComputeHash(scopeId, activityId);
        return $"asdur-v1-{Convert.ToHexStringLower(hash)}";
    }

    internal static long CreateJitterSeed(DurableScopeId scopeId, string activityId) =>
        BinaryPrimitives.ReadInt64BigEndian(ComputeHash(scopeId, activityId));

    private static byte[] ComputeHash(DurableScopeId scopeId, string activityId)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, DomainSeparator);
        Write(writer, scopeId.Value);
        Write(writer, activityId);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
