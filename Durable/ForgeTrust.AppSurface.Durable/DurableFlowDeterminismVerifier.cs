using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Reports whether two evaluations of the same persisted Flow input produced byte-for-byte equivalent durable
/// decisions.
/// </summary>
public sealed record DurableFlowDeterminismReport
{
    internal DurableFlowDeterminismReport(string firstEvaluationSha256, string secondEvaluationSha256)
    {
        FirstEvaluationSha256 = firstEvaluationSha256;
        SecondEvaluationSha256 = secondEvaluationSha256;
    }

    /// <summary>Gets the canonical SHA-256 fingerprint of the first evaluation.</summary>
    public string FirstEvaluationSha256 { get; }

    /// <summary>Gets the canonical SHA-256 fingerprint of the second evaluation.</summary>
    public string SecondEvaluationSha256 { get; }

    /// <summary>Gets whether both evaluations produced the same durable decision.</summary>
    public bool IsDeterministic => string.Equals(
        FirstEvaluationSha256,
        SecondEvaluationSha256,
        StringComparison.Ordinal);
}

/// <summary>
/// Test harness that evaluates one durable Flow input twice and compares canonical transition bytes.
/// </summary>
/// <remarks>
/// Use this in definition tests and deployment validation. It deliberately executes node code twice, so do not place
/// external effects in nodes and do not use this helper as the production transition commit path. A passing sample
/// proves only that the supplied input was stable; source-generator determinism warnings and explicit persisted inputs
/// remain necessary for other branches.
/// </remarks>
public static class DurableFlowDeterminismVerifier
{
    /// <summary>
    /// Evaluates the same registered input twice and returns canonical decision fingerprints.
    /// </summary>
    /// <param name="registration">Exact durable Flow registration to evaluate.</param>
    /// <param name="input">Persisted node input to replay twice.</param>
    /// <param name="payloadCodecs">Exact allowlisted payload codec registry.</param>
    /// <param name="cancellationToken">Token that cancels either evaluation.</param>
    /// <returns>A report whose <see cref="DurableFlowDeterminismReport.IsDeterministic"/> value compares both runs.</returns>
    public static async ValueTask<DurableFlowDeterminismReport> VerifyAsync(
        DurableFlowRegistration registration,
        DurableFlowEvaluationInput input,
        IDurablePayloadCodecRegistry payloadCodecs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(payloadCodecs);

        var first = await registration.EvaluateAsync(input, payloadCodecs, cancellationToken).ConfigureAwait(false);
        var second = await registration.EvaluateAsync(input, payloadCodecs, cancellationToken).ConfigureAwait(false);
        return new DurableFlowDeterminismReport(Fingerprint(first), Fingerprint(second));
    }

    /// <summary>
    /// Evaluates the same registered input twice and throws when the canonical decisions differ.
    /// </summary>
    /// <param name="registration">Exact durable Flow registration to evaluate.</param>
    /// <param name="input">Persisted node input to replay twice.</param>
    /// <param name="payloadCodecs">Exact allowlisted payload codec registry.</param>
    /// <param name="cancellationToken">Token that cancels either evaluation.</param>
    /// <returns>The deterministic comparison report.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the two canonical decisions differ.</exception>
    public static async ValueTask<DurableFlowDeterminismReport> VerifyAndThrowAsync(
        DurableFlowRegistration registration,
        DurableFlowEvaluationInput input,
        IDurablePayloadCodecRegistry payloadCodecs,
        CancellationToken cancellationToken = default)
    {
        var report = await VerifyAsync(registration, input, payloadCodecs, cancellationToken).ConfigureAwait(false);
        if (!report.IsDeterministic)
        {
            throw new InvalidOperationException(
                $"Durable Flow '{registration.FlowId}' version '{registration.FlowVersion}' produced different decisions for the same persisted input ({report.FirstEvaluationSha256} != {report.SecondEvaluationSha256}).");
        }

        return report;
    }

    private static string Fingerprint(DurableFlowEvaluationResult transition)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "durable-flow-evaluation-v1");
        Append(hash, ((int)transition.Kind).ToString(CultureInfo.InvariantCulture));
        Append(hash, transition.NodeId);
        AppendPayload(hash, transition.Context);
        AppendOptional(hash, transition.NextNodeId);
        AppendOptional(hash, transition.EventName);
        AppendOptional(
            hash,
            transition.Timeout?.Duration.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendOptional(hash, transition.Fault?.Code);
        AppendOptional(hash, transition.Fault?.Message);

        if (transition.Activity is { } activity)
        {
            Append(hash, "activity-present");
            Append(hash, activity.CallsiteId);
            Append(hash, activity.ResultContractVersion.ToString(CultureInfo.InvariantCulture));
            Append(hash, activity.WorkName);
            Append(hash, activity.WorkVersion);
            Append(hash, ((int)activity.ProviderSafety).ToString(CultureInfo.InvariantCulture));
            AppendPayload(hash, activity.Work);
        }
        else
        {
            Append(hash, "activity-absent");
        }

        if (transition.EventContract is { } eventContract)
        {
            Append(hash, "event-contract-present");
            Append(hash, eventContract.PayloadRequired ? "required" : "no-payload");
            AppendOptional(hash, eventContract.ContractName);
            AppendOptional(hash, eventContract.ContractVersion);
            AppendOptional(
                hash,
                eventContract.Classification is { } classification
                    ? ((int)classification).ToString(CultureInfo.InvariantCulture)
                    : null);
            AppendOptional(hash, eventContract.RetentionPolicyId);
        }
        else
        {
            Append(hash, "event-contract-absent");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendPayload(IncrementalHash hash, DurableEncodedPayload? payload)
    {
        if (payload is null)
        {
            Append(hash, "payload-absent");
            return;
        }

        Append(hash, "payload-present");
        Append(hash, payload.ContractName);
        Append(hash, payload.ContractVersion);
        Append(hash, ((int)payload.Classification).ToString(CultureInfo.InvariantCulture));
        Append(hash, payload.RetentionPolicyId);
        Append(hash, payload.Content.Span);
    }

    private static void AppendOptional(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, "optional-absent");
            return;
        }

        Append(hash, "optional-present");
        Append(hash, value);
    }

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
