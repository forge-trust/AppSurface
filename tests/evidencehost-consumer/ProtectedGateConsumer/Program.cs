using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace EvidenceHost.ProtectedGateConsumer;

/// <summary>Evaluates one protected-parent input through the public Evidence gate without issuing admission.</summary>
/// <remarks>
/// Standard input is a transport, not authentication. The parent owns provenance, expected-plan selection,
/// artifact-byte verification and this executable's immutable source. Uploaded JSON can satisfy structural checks
/// and must never be treated as a protected channel merely because this process returns zero.
/// </remarks>
internal static class Program
{
    /// <summary>Supported transport schema; it carries no verifier or admission authority.</summary>
    internal const string EnvelopeSchema = "evidence-protected-gate-input-v1";

    /// <summary>Reads a single bounded envelope to EOF and emits only a fixed decision diagnostic.</summary>
    /// <param name="args">No arguments are supported; file paths cannot select expected facts.</param>
    /// <returns>Zero for allowed, one for denied, two for invalid input, or three for a nonfatal consumer failure.</returns>
    /// <remarks>
    /// Reading has a 30-second cancellation deadline; the protected parent must also supervise process termination.
    /// Runtime-fatal failures are not converted into successful or recoverable gate decisions.
    /// </remarks>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            return RejectInput();
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var input = Console.OpenStandardInput();
            var envelope = await EvidenceCanonicalJson.DeserializeAsync<GateEnvelope>(
                input, EvidenceCanonicalJson.MaximumInputBytes, deadline.Token).ConfigureAwait(false);
            if (envelope.Schema != EnvelopeSchema || envelope.Plan is null || envelope.Expected is null)
            {
                return RejectInput();
            }

            if (!EvidenceProtectedGate.Allows(envelope.Plan, envelope.Manifest, envelope.Expected))
            {
                Console.Error.WriteLine("EVIDENCE_GATE_DENIED");
                return 1;
            }

            Console.Out.WriteLine("EVIDENCE_GATE_ALLOWED");
            return 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or OperationCanceledException)
        {
            return RejectInput();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            // Exception text, contract values, paths and subject diagnostics never enter output.
            Console.Error.WriteLine("EVIDENCE_GATE_FAILED");
            return 3;
        }
    }

    /// <summary>Returns the fixed invalid-input decision without disclosing the rejected bytes.</summary>
    private static int RejectInput()
    {
        Console.Error.WriteLine("EVIDENCE_GATE_INPUT_REJECTED");
        return 2;
    }
}

/// <summary>One transport message whose provenance is established independently by the protected parent.</summary>
/// <param name="Schema">Exact transport schema, <c>evidence-protected-gate-input-v1</c>.</param>
/// <param name="Plan">Expected plan independently selected from protected policy and diff.</param>
/// <param name="Manifest">Collected manifest, explicitly null when missing or quarantined.</param>
/// <param name="Expected">Current expected facts independently supplied by the protected consumer.</param>
/// <remarks>All four properties must be present. Only <paramref name="Manifest"/> may be null.</remarks>
internal sealed record GateEnvelope(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] EvidencePlan Plan,
    [property: JsonRequired] EvidenceManifest? Manifest,
    [property: JsonRequired] EvidenceProtectedGateExpectation Expected);
