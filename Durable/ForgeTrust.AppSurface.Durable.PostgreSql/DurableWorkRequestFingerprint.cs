using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class DurableWorkRequestFingerprint
{
    internal static byte[] Compute(DurableWorkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, request.WorkName);
        Write(writer, request.WorkVersion);
        Write(writer, request.Payload.ContractName);
        Write(writer, request.Payload.ContractVersion);
        writer.Write((int)request.Payload.Classification);
        Write(writer, request.Payload.RetentionPolicyId);
        writer.Write((int)request.ProviderSafety);
        writer.Write(request.RetryPolicy.MaximumAttempts);
        writer.Write(request.RetryPolicy.MaximumElapsedTime.Ticks);
        writer.Write(request.RetryPolicy.InitialRetryDelay.Ticks);
        writer.Write(request.RetryPolicy.MaximumRetryDelay.Ticks);
        writer.Write(request.RetryPolicy.LeaseDuration.Ticks);
        writer.Write(request.RetryPolicy.RenewalCadence.Ticks);
        writer.Write(request.RetryPolicy.MaximumLeaseLifetime.Ticks);
        Write(writer, request.RetryPolicy.BackoffAlgorithm);
        writer.Write(request.DueAtUtc.HasValue);
        if (request.DueAtUtc is { } dueAt)
        {
            writer.Write(dueAt.UtcTicks);
        }

        writer.Write(request.Payload.Content.Length);
        writer.Write(request.Payload.Content.Span);
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
