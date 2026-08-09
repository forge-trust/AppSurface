using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Builds the canonical V1 descriptor digest for evidence-backed child-effect Flow repair.</summary>
/// <remarks>
/// The digest binds the persisted suspension shape to the exact activity wait and child Work. Its field order and
/// length-prefixed UTF-8 encoding are a durable compatibility contract: changing either requires a new
/// <see cref="SchemaId"/> and a corresponding migration constraint.
/// </remarks>
internal static class PostgreSqlDurableFlowRepairDescriptor
{
    /// <summary>Gets the versioned descriptor schema persisted with a repairable Flow suspension.</summary>
    internal const string SchemaId = "appsurface.durable.flow.child-suspension.v1";

    /// <summary>Creates the canonical SHA-256 digest of one child-effect suspension descriptor.</summary>
    /// <param name="suspendedFromState">The persisted Flow state immediately before suspension.</param>
    /// <param name="code">The stable suspension code.</param>
    /// <param name="source">The stable suspension source.</param>
    /// <param name="workState">The retained child Work state that caused suspension.</param>
    /// <param name="waitId">The activity-wait identity bound to the child Work.</param>
    /// <param name="workId">The child Work identity bound to the wait.</param>
    /// <returns>The lowercase hexadecimal SHA-256 descriptor digest.</returns>
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
