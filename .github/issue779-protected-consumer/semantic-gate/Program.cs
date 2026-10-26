using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace EvidenceHost.ProtectedParentGate;

/// <summary>Resolves protected policy and diff before comparing uploaded documents and their actual artifact bytes.</summary>
/// <remarks>
/// This process authenticates no input channel and issues no admission. Its parent must independently protect
/// this executable, policy, diff, expectations and collected bytes. A zero exit alone is not Trusted acceptance.
/// </remarks>
internal static class Program
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Consumes one bounded parent envelope and emits a fixed decision, without paths or subject text.</summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return Reject();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var input = Console.OpenStandardInput();
            var envelope = await EvidenceCanonicalJson.DeserializeAsync<ParentEnvelope>(input,
                EvidenceCanonicalJson.MaximumInputBytes, deadline.Token).ConfigureAwait(false);
            if (envelope.Schema != "issue779-protected-parent-gate-v1"
                || envelope.Expected is null || envelope.PolicyBytes is null || envelope.DiffBytes is null
                || envelope.PlanBytes is null || envelope.ManifestBytes is null || envelope.Artifacts is null
                || !Bounded(envelope.PolicyBytes) || !Bounded(envelope.DiffBytes)
                || !Bounded(envelope.PlanBytes) || !Bounded(envelope.ManifestBytes)
                || !DigestMatches(envelope.PolicyBytes, envelope.PolicySha256)
                || !DigestMatches(envelope.DiffBytes, envelope.DiffSha256)) return Reject();

            deadline.Token.ThrowIfCancellationRequested();
            var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(envelope.PolicyBytes, MaximumDocumentBytes);
            var diff = StrictUtf8.GetString(envelope.DiffBytes);
            var plan = new EvidencePlanner().Resolve(policy, EvidenceUnifiedDiffReader.Read(diff));
            var receivedPlan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(envelope.PlanBytes, MaximumDocumentBytes);
            if (!EvidenceCanonicalJson.Serialize(plan).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(receivedPlan)))
                return Deny();
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(envelope.ManifestBytes, MaximumDocumentBytes);
            if (!VerifyArtifacts(manifest, envelope.Artifacts, deadline.Token)
                || !EvidenceProtectedGate.Allows(plan, manifest, envelope.Expected)) return Deny();
            deadline.Token.ThrowIfCancellationRequested();
            Console.Out.WriteLine("EVIDENCE_PARENT_GATE_ALLOWED");
            return 0;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException
                                     or EvidencePlanningException or OperationCanceledException)
        {
            return Reject();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException
                                     and not AccessViolationException)
        {
            Console.Error.WriteLine("EVIDENCE_PARENT_GATE_FAILED");
            return 3;
        }
    }

    /// <summary>Compares all declared artifact bytes, including zero-length files, with no missing or extra body.</summary>
    /// <remarks>The protected collector owns custody; detached byte arrays cannot establish that custody themselves.</remarks>
    private static bool VerifyArtifacts(EvidenceManifest manifest, IReadOnlyList<ArtifactBody> bodies,
        CancellationToken token)
    {
        if (bodies.Count > 64 || bodies.Any(static body => body is null || body.Bytes is null
            || body.Bytes.Length > MaximumDocumentBytes || string.IsNullOrWhiteSpace(body.ProducerId)
            || string.IsNullOrWhiteSpace(body.RelativePath))) return false;
        var actual = new Dictionary<(string Producer, string Path), byte[]>();
        long total = 0;
        foreach (var body in bodies)
        {
            token.ThrowIfCancellationRequested();
            if (!actual.TryAdd((body.ProducerId, body.RelativePath), body.Bytes)) return false;
            total += body.Bytes.Length;
            if (total > EvidenceCanonicalJson.MaximumInputBytes) return false;
        }
        var selected = new HashSet<(string Producer, string Path)>();
        foreach (var result in manifest.ProducerResults)
        {
            foreach (var artifact in result.Artifacts ?? [])
            {
                token.ThrowIfCancellationRequested();
                var key = (result.ProducerId, artifact.RelativePath);
                if (!selected.Add(key) || !actual.TryGetValue(key, out var bytes)
                    || artifact.LengthBytes != bytes.LongLength || !DigestMatches(bytes, artifact.Sha256)) return false;
            }
        }
        return selected.Count == actual.Count;
    }

    private static bool Bounded(byte[] bytes) => bytes.Length is > 0 and <= MaximumDocumentBytes;
    private static bool DigestMatches(byte[] bytes, string? expected) => expected is { Length: 64 }
        && expected.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
        && Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected;
    private static int Reject() { Console.Error.WriteLine("EVIDENCE_PARENT_GATE_INPUT_REJECTED"); return 2; }
    private static int Deny() { Console.Error.WriteLine("EVIDENCE_PARENT_GATE_DENIED"); return 1; }
}

/// <summary>Parent transport; policy/diff/hash/expected facts are independently protected inputs, never upload-derived.</summary>
/// <param name="Schema">Exact issue779-protected-parent-gate-v1.</param>
/// <param name="PolicyBytes">Original protected policy, at most 1 MiB; JSON represents byte arrays as base64.</param>
/// <param name="PolicySha256">Independently expected lowercase SHA-256 of original policy bytes.</param>
/// <param name="DiffBytes">Original protected UTF-8 unified diff, at most 1 MiB.</param>
/// <param name="DiffSha256">Independently expected lowercase SHA-256 of original diff bytes.</param>
/// <param name="PlanBytes">Uploaded plan selected only after parent-run archive provenance verification.</param>
/// <param name="ManifestBytes">Uploaded manifest selected from that same verified archive.</param>
/// <param name="Expected">Independent protected current run, revision, capability, proof and root facts.</param>
/// <param name="Artifacts">Actual collected artifact bytes, at most 64 files of 1 MiB each within the envelope bound.</param>
internal sealed record ParentEnvelope(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] byte[] PolicyBytes,
    [property: JsonRequired] string PolicySha256,
    [property: JsonRequired] byte[] DiffBytes,
    [property: JsonRequired] string DiffSha256,
    [property: JsonRequired] byte[] PlanBytes,
    [property: JsonRequired] byte[] ManifestBytes,
    [property: JsonRequired] EvidenceProtectedGateExpectation Expected,
    [property: JsonRequired] IReadOnlyList<ArtifactBody> Artifacts);

/// <summary>Copied collected bytes only, not a file handle or claim of filesystem ownership.</summary>
/// <param name="ProducerId">Exact protected producer identifier.</param>
/// <param name="RelativePath">Exact manifest-relative path used only as a comparison key, never opened.</param>
/// <param name="Bytes">Actual collected bytes.</param>
internal sealed record ArtifactBody([property: JsonRequired] string ProducerId,
    [property: JsonRequired] string RelativePath, [property: JsonRequired] byte[] Bytes);
