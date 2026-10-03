using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace EvidenceHost.PrivateQualificationMetadata;

/// <summary>Formats bounded private qualification candidate data; issues no runtime authority.</summary>
/// <remarks>
/// The existing test assembly identity exposes only catalogue metadata APIs used here. This executable
/// never constructs admission/context/worker values, resolves a compiled entry, launches a process, or
/// opens a bundle pathname. Parent build and root inspection own exact policy/source/platform bindings.
/// </remarks>
internal static class Program
{
    private const int MaximumStdioBytes = 1024 * 1024;
    private const string ApplicationId = "issue779-qualified-native-http";
    private const string ApplicationVersion = "1.0.0";
    private const string ApplicationBuildId = "issue779-private-qualification-1";
    private const string AspireVersion = "13.4.4";
    private const string ProfileId = "qualification-http";
    private const string ChangedPath = "tests/QualificationSubjectTests.cs";
    private const string FixedError = "ASEVD404: Private qualification metadata input is invalid or unsupported.";

    /// <summary>Consumes one bounded stdin envelope and emits one bounded canonical metadata line.</summary>
    /// <returns>Zero after complete output; 65 for any unsupported metadata or I/O failure.</returns>
    private static async Task<int> Main()
    {
        try
        {
            await using var input = Console.OpenStandardInput();
            var bytes = await ReadBoundedAsync(input).ConfigureAwait(false);
            var request = ParseRequest(bytes);
            var snapshot = BuildSnapshot(request);
            var plan = new EvidencePlanner().Resolve(snapshot.Policy, [new NormalizedDiffPath(ChangedPath)]);
            if (plan.Profile.Id != ProfileId)
            {
                throw InvalidInput();
            }

            var entryBytes = EvidenceCanonicalJson.Serialize(snapshot);
            var policyBytes = EvidenceCanonicalJson.Serialize(snapshot.Policy);
            var planBytes = EvidenceCanonicalJson.Serialize(plan);
            var response = new MetadataResponse(
                Convert.ToBase64String(entryBytes),
                EvidenceClosedApplicationCatalogue.ComputeEntryDigest(snapshot),
                EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([snapshot]),
                Convert.ToBase64String(policyBytes), EvidenceDigest.Sha256(policyBytes),
                Convert.ToBase64String(planBytes), plan.PlanDigest);
            var outputBytes = EvidenceCanonicalJson.Serialize(response);
            // Include the one trailing newline in the output envelope limit.
            if (outputBytes.Length >= MaximumStdioBytes)
            {
                throw InvalidInput();
            }

            await using var output = Console.OpenStandardOutput();
            await output.WriteAsync(outputBytes).ConfigureAwait(false);
            await output.WriteAsync(new byte[] { (byte)'\n' }).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            // Do not echo input, bundle names, host paths, exceptions or nested diagnostics.
            try
            {
                await Console.Error.WriteLineAsync(FixedError).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A closed diagnostic stream cannot turn failure into metadata output.
            }

            return 65;
        }
    }

    /// <summary>Reads at most one MiB and checks one extra byte without retaining an oversized envelope.</summary>
    /// <param name="input">Caller-owned stdin; no named file source is supported.</param>
    /// <returns>The complete input bytes after EOF.</returns>
    private static async Task<byte[]> ReadBoundedAsync(Stream input)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var remaining = MaximumStdioBytes - checked((int)buffer.Length);
            var read = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1))).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (read > remaining)
            {
                throw InvalidInput();
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>Parses the exact data envelope and closed file rows; never inspects a bundle pathname.</summary>
    private static MetadataRequest ParseRequest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        RequireProperties(root, ["policy", "bundle_files"]);
        var policyValue = root.GetProperty("policy");
        if (policyValue.ValueKind != JsonValueKind.Object)
        {
            throw InvalidInput();
        }

        // Use the real bounded contracts parser for policy constructor/null/duplicate/enum checks.
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(
            Encoding.UTF8.GetBytes(policyValue.GetRawText()), MaximumStdioBytes);
        var filesValue = root.GetProperty("bundle_files");
        if (filesValue.ValueKind != JsonValueKind.Array
            || filesValue.GetArrayLength() > EvidenceClosedApplicationCatalogue.MaximumBundleFiles)
        {
            throw InvalidInput();
        }

        var files = new List<EvidenceClosedBundleFile>(filesValue.GetArrayLength());
        foreach (var row in filesValue.EnumerateArray())
        {
            RequireProperties(row, ["RelativePath", "Role", "LengthBytes", "Sha256", "Mode"]);
            var roleName = RequiredString(row.GetProperty("Role"));
            if (!Enum.TryParse<EvidenceClosedBundleRole>(roleName, ignoreCase: false, out var role)
                || !Enum.IsDefined(role) || role.ToString() != roleName
                || !row.GetProperty("LengthBytes").TryGetInt64(out var length)
                || !row.GetProperty("Mode").TryGetUInt32(out var mode))
            {
                throw InvalidInput();
            }

            files.Add(new EvidenceClosedBundleFile(RequiredString(row.GetProperty("RelativePath")), role,
                length, RequiredString(row.GetProperty("Sha256")), mode));
        }

        return new MetadataRequest(policy, Array.AsReadOnly(files.ToArray()));
    }

    /// <summary>Constructs only the fixed reviewed topology/grants, then invokes the actual catalogue snapshot audit.</summary>
    private static EvidenceClosedApplicationDefinition BuildSnapshot(MetadataRequest request)
    {
        var profile = request.Policy.Profiles.SingleOrDefault(static item => item.Id == ProfileId)
            ?? throw InvalidInput();
        var resources = profile.Resources.Select(static declaration => new EvidenceClosedResourceRegistration(
            declaration, "native-http-uds", "1.0.0", "native-http")).ToArray();
        var producers = profile.Producers.Select(static declaration => new EvidenceClosedProducerRegistration(
            declaration, "coverage", "1.0.0")).ToArray();
        var capabilities = new EvidenceClosedApplicationCapabilities(
            Array.AsReadOnly(new[] { "proof-input/declared.txt" }), 512L * 1024 * 1024,
            1024L * 1024 * 1024, 128, 1024 * 1024, 120, 5);
        var definition = new EvidenceClosedApplicationDefinition(ApplicationId, ApplicationVersion, ApplicationBuildId,
            AspireVersion, request.Policy, ProfileId, Array.AsReadOnly(resources), Array.AsReadOnly(producers),
            request.BundleFiles, capabilities);
        return EvidenceClosedApplicationCatalogue.Snapshot(definition);
    }

    /// <summary>Requires exact field names once each, rejecting unknown fields, duplicate names and case aliases.</summary>
    private static void RequireProperties(JsonElement value, IReadOnlyList<string> required)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidInput();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                throw InvalidInput();
            }
        }

        if (seen.Count != required.Count)
        {
            throw InvalidInput();
        }
    }

    /// <summary>Extracts a required string; catalogue audit subsequently enforces each field's finite grammar.</summary>
    private static string RequiredString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw InvalidInput() : throw InvalidInput();

    /// <summary>Constructs a fixed data-only error without input values.</summary>
    private static InvalidDataException InvalidInput() => new(FixedError);

    /// <summary>Parsed data only; no file handle, identity observation, worker or capability is represented.</summary>
    private sealed record MetadataRequest(EvidencePolicy Policy, IReadOnlyList<EvidenceClosedBundleFile> BundleFiles);

    /// <summary>Canonical metadata output for the parent's later source expansion and independent root binding.</summary>
    private sealed record MetadataResponse(
        [property: JsonPropertyName("entrybase64")] string EntryBase64,
        [property: JsonPropertyName("entry_digest")] string EntryDigest,
        [property: JsonPropertyName("catalogue_digest")] string CatalogueDigest,
        [property: JsonPropertyName("policybase64")] string PolicyBase64,
        [property: JsonPropertyName("policy_sha256")] string PolicySha256,
        [property: JsonPropertyName("planbase64")] string PlanBase64,
        [property: JsonPropertyName("plan_digest")] string PlanDigest);
}
