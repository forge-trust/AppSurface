using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductAbiInspector;

internal static class Program
{
    private static readonly string[] Names = ["Contracts", "Planner", "Cli", "Aspire", "Coverage"];

    public static int Main(string[] args)
    {
        var budget = new Budget();
        string stage = "input";
        try
        {
            Guard.Require(args.Length == 4 && args[0] == "--input" && args[2] == "--output", "arguments");
            var inputPath = Guard.Absolute(args[1]);
            var outputPath = Guard.Absolute(args[3]);
            Guard.Require(!File.Exists(outputPath) && !Directory.Exists(outputPath), "output-collision");
            byte[] input = Files.Read(inputPath, 128 * 1024, budget, false);
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = document.RootElement;
            string? partitionName = null;
            string? manifestSha256 = null;
            Dictionary<string, PairInput> requested;
            Guard.Require(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schema", out _), "input-schema");
            string? schema = root.GetProperty("schema").GetString();
            if (schema == "issue779-product-abi-common-input-v1")
            {
                return CoverageStages.CommonOnly(root, input, outputPath, Names, budget, ref stage);
            }
            bool inventoryOnly = schema == "issue779-product-abi-inventory-input-v1";
            if (schema == "issue779-product-abi-pair-input-v1" || inventoryOnly)
            {
                Input.Exact(root, "schema", "manifest", "manifest_sha256", "pair_name");
                partitionName = Guard.Text(root.GetProperty("pair_name").GetString());
                Guard.Require(Array.IndexOf(Names, partitionName) >= 0, "input-name");
                Guard.Require(!inventoryOnly || partitionName is "Contracts" or "Coverage", "inventory-pair");
                string manifestPath = Guard.Absolute(Guard.Text(root.GetProperty("manifest").GetString()));
                manifestSha256 = Guard.Sha(root.GetProperty("manifest_sha256").GetString());
                byte[] manifest = Files.Read(manifestPath, 128 * 1024, budget, false);
                Guard.Require(Hash(manifest) == manifestSha256, "manifest-sha256");
                budget.Check();
                // The manifest hash is verified BEFORE parsing its exact five-pair
                // schema. It cannot itself select another manifest or partial input.
                using var manifestDocument = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 12 });
                requested = Input.Pairs(manifestDocument.RootElement, Names, budget);
            }
            else
            {
                requested = Input.Pairs(root, Names, budget);
            }

            // Verify ALL input pins before constructing any PE or PDB metadata reader.
            stage = "input-pins";
            var bytes = new Dictionary<string, PairBytes>(StringComparer.Ordinal);
            foreach (string name in Names)
            {
                budget.Row();
                PairInput pair = requested[name];
                bytes.Add(name, new PairBytes(Files.Verify(pair.Baseline, budget), Files.Verify(pair.Candidate, budget)));
            }
            stage = "metadata-and-comparison";
            var reports = new List<PairReport>();
            var inventories = new List<InventoryPairReportV2>();
            var analyzed = new Dictionary<string, (ImageReport Baseline, ImageReport Candidate)>(StringComparer.Ordinal);
            var indexes = new Comparisons.IndexCache();
            string[] selectedNames = partitionName is null ? Names : [partitionName];
            foreach (string name in selectedNames)
            {
                budget.Row();
                PairBytes pair = bytes[name];
                budget.Begin(name, InspectionPhase.Baseline);
                ImageReport baseline = Inspector.Read(pair.Baseline, budget);
                budget.Begin(name, InspectionPhase.Candidate);
                ImageReport candidate = SameVerifiedImage(pair.Baseline, pair.Candidate, budget)
                    ? baseline : Inspector.Read(pair.Candidate, budget);
                Guard.Require(baseline.Assembly.Name == candidate.Assembly.Name, "pair-assembly-name");
                if (inventoryOnly)
                {
                    budget.Row();
                    bool shared = ReferenceEquals(baseline, candidate);
                    inventories.Add(new InventoryPairReportV2(name, baseline, shared ? null : candidate,
                        pair.Baseline.DllSha256 == pair.Candidate.DllSha256,
                        pair.Baseline.PdbSha256 == pair.Candidate.PdbSha256, shared, null));
                    continue;
                }
                analyzed.Add(name, (baseline, candidate));
                budget.Begin(name, InspectionPhase.Common);
                reports.Add(new PairReport(name, baseline, candidate,
                    pair.Baseline.DllSha256 == pair.Candidate.DllSha256,
                    pair.Baseline.PdbSha256 == pair.Candidate.PdbSha256,
                    Comparisons.Common(baseline, candidate, budget, indexes)));
            }
            List<DependencyMatch> matches = partitionName is null
                ? Comparisons.Dependencies(analyzed, budget, indexes) : [];
            int unresolved = 0;
            foreach (DependencyMatch match in matches)
            {
                budget.Begin(match.Caller, InspectionPhase.Dependency);
                budget.Row();
                if (match.BaselineMatches != 1 || match.CandidateMatches != 1)
                {
                    unresolved++;
                }
            }
            budget.Row();
            Report? report = partitionName is null ? new Report("issue779-product-abi-metadata-v1", Hash(input), reports, matches,
                unresolved, budget.Rows, budget.InputBytes, false, false, false,
                ["metadata-only; inspected code was not loaded or invoked",
                  "exact direct-definition matches are not runtime or access-resolution proof",
                  "inheritance, forwarding, generic substitution, loader binding and runtime behavior remain unresolved",
                  "caller must reject unresolved direct-dependency matches for its binary-reuse review",
                  "portable-PDB documents/checksums are observations, not rehashed source files",
                  "sequence offsets and IL hashes do not constitute a reconstructed branch map"]) : null;
            PairMetadataReport? partitionReport = partitionName is null || inventoryOnly ? null : new PairMetadataReport(
                "issue779-product-abi-pair-metadata-v1", Hash(input), partitionName, manifestSha256!, reports,
                [], null, budget.Rows, budget.InputBytes, false, false, false,
                ["metadata-only; inspected code was not loaded or invoked",
                 "single-pair partition; direct dependency references have not been reconciled",
                 "null unresolved count is not zero and cannot authorize binary reuse",
                 "trusted caller must reconcile all five digest-bound partitions against this manifest",
                 "exact direct-definition matches are not runtime or access-resolution proof",
                 "inheritance, forwarding, generic substitution, loader binding and runtime behavior remain unresolved",
                 "portable-PDB documents/checksums are observations, not rehashed source files",
                 "sequence offsets and IL hashes do not constitute a reconstructed branch map"]);
            InventoryMetadataReportV2? inventoryReport = !inventoryOnly ? null : new InventoryMetadataReportV2(
                "issue779-product-abi-inventory-metadata-v2", Hash(input), partitionName!, manifestSha256!, inventories,
                [], null, budget.Rows, budget.InputBytes, false, false, false,
                ["metadata-only; inspected code was not loaded or invoked",
                 "inventory only; common comparison and direct dependency reconciliation were not performed",
                 "candidate_is_baseline is an exact shared-image reference, only after all pins and actual byte equality",
                 "null common and unresolved count are incomplete work, not successful compatibility",
                 "caller must bind actual successful producer exit and digest before consuming this inventory",
                 "portable-PDB documents and IL/sequence observations are not source equivalence or coverage credit"]);
            stage = "output-create";
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                using var capped = new CappedOutput(output, budget);
                stage = "output-serialize";
                if (report is not null)
                {
                    JsonSerializer.Serialize(capped, report, ReportJsonContext.Default.Report);
                }
                else if (inventoryReport is not null)
                {
                    JsonSerializer.Serialize(capped, inventoryReport, ReportJsonContext.Default.InventoryMetadataReportV2);
                }
                else
                {
                    JsonSerializer.Serialize(capped, partitionReport!, ReportJsonContext.Default.PairMetadataReport);
                }
                stage = "output-flush-close";
                capped.Flush();
                output.Flush(true);
                budget.Check();
            }
            stage = "publication";
            budget.Check(); // after receipt close, before publishing success
            Console.WriteLine("metadata-report-written; runtime-compatibility-unproven");
            return 0;
        }
        catch (InspectionException error)
        {
            Console.Error.WriteLine("inspection-failed:" + error.Category + ";stage=" + stage
                + (stage == "metadata-and-comparison" ? budget.FailureProgress() : ""));
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
                                      or JsonException or ArgumentException or InvalidOperationException or OverflowException
                                      or NotSupportedException)
        {
            Console.Error.WriteLine("inspection-failed:input-or-metadata;stage=" + stage
                + (stage == "metadata-and-comparison" ? budget.FailureProgress() : ""));
            return 1;
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool SameVerifiedImage(ImageBytes baseline, ImageBytes candidate, Budget budget)
    {
        budget.Row();
        if (!string.Equals(baseline.DllPath, candidate.DllPath, StringComparison.Ordinal)
            || !string.Equals(baseline.PdbPath, candidate.PdbPath, StringComparison.Ordinal)
            || baseline.DllSha256 != candidate.DllSha256 || baseline.PdbSha256 != candidate.PdbSha256
            || baseline.Dll.Length != candidate.Dll.Length || baseline.Pdb.Length != candidate.Pdb.Length)
        {
            return false;
        }
        return BytesEqual(baseline.Dll, candidate.Dll, budget) && BytesEqual(baseline.Pdb, candidate.Pdb, budget);
    }

    private static bool BytesEqual(byte[] baseline, byte[] candidate, Budget budget)
    {
        budget.Row();
        if (baseline.Length != candidate.Length) { return false; }
        for (int offset = 0; offset < baseline.Length; offset += 65536)
        {
            budget.Row(); // charge each bounded actual-byte comparison before doing it
            int count = Math.Min(65536, baseline.Length - offset);
            if (!baseline.AsSpan(offset, count).SequenceEqual(candidate.AsSpan(offset, count))) { return false; }
        }
        return true;
    }
}

internal sealed class InspectionException(string category) : Exception(category)
{
    public string Category { get; } = category;
}

internal static class Guard
{
    public static void Require(bool value, string category)
    {
        if (!value)
        {
            throw new InspectionException(category);
        }
    }

    public static string Text(string? value)
    {
        Require(value is not null && value.Length <= 4096 && !value.Contains('\0'), "text-bound");
        return value!;
    }

    public static string Absolute(string value)
    {
        Text(value);
        Require(Path.IsPathFullyQualified(value) && Path.GetFullPath(value) == value, "absolute-path");
        return value!;
    }

    public static string Sha(string? value)
    {
        Require(value is not null && value.Length == 64, "sha256-pin");
        foreach (char item in value!)
        {
            Require(item is >= '0' and <= '9' or >= 'a' and <= 'f', "sha256-pin");
        }
        return value!;
    }
}

internal enum InspectionPair { Contracts, Planner, Cli, Aspire, Coverage }
internal enum InspectionPhase { Baseline, Candidate, Common, Dependency }

internal sealed class Budget
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public int Rows { get; private set; }
    public long InputBytes { get; private set; }
    public int Documents { get; private set; }
    private InspectionPair? pair;
    private InspectionPhase? phase;
    private int phaseStartRows;

    public void Begin(string name, InspectionPhase nextPhase)
    {
        pair = name switch
        {
            "Contracts" => InspectionPair.Contracts,
            "Planner" => InspectionPair.Planner,
            "Cli" => InspectionPair.Cli,
            "Aspire" => InspectionPair.Aspire,
            "Coverage" => InspectionPair.Coverage,
            _ => throw new InspectionException("progress-pair"),
        };
        phase = nextPhase;
        phaseStartRows = Rows;
        Row();
    }

    public string FailureProgress()
    {
        if (pair is null || phase is null) { return ""; }
        string phaseName = phase.Value switch
        {
            InspectionPhase.Baseline => "baseline",
            InspectionPhase.Candidate => "candidate",
            InspectionPhase.Common => "common",
            InspectionPhase.Dependency => "dependency",
            _ => throw new InspectionException("progress-phase"),
        };
        return ";pair=" + pair.Value + ";phase=" + phaseName
            + ";rows=" + Rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";phase-start-rows=" + phaseStartRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Check() => Guard.Require(clock.Elapsed < TimeSpan.FromSeconds(30), "deadline");

    public void Row()
    {
        Check();
        Guard.Require(Rows < 100000, "row-bound");
        Rows++;
    }

    public void Input(long bytes)
    {
        Check();
        Guard.Require(bytes > 0 && bytes <= 128L * 1024 * 1024 - InputBytes, "input-total");
        InputBytes += bytes;
    }

    public void Document()
    {
        Check();
        Guard.Require(Documents < 4096, "document-bound");
        Documents++;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Serialization | JsonSourceGenerationMode.Metadata, WriteIndented = false)]
[JsonSerializable(typeof(Report))]
[JsonSerializable(typeof(PairMetadataReport))]
[JsonSerializable(typeof(InventoryMetadataReport))]
[JsonSerializable(typeof(CommonMetadataReport))]
[JsonSerializable(typeof(InventoryMetadataReportV2))]
internal partial class ReportJsonContext : JsonSerializerContext
{
}

internal static class Input
{
    public static Dictionary<string, PairInput> Pairs(JsonElement root, string[] names, Budget budget)
    {
        Exact(root, "schema", "pairs");
        Guard.Require(root.GetProperty("schema").GetString() == "issue779-product-abi-input-v1", "input-schema");
        JsonElement pairs = root.GetProperty("pairs");
        Guard.Require(pairs.ValueKind == JsonValueKind.Array && pairs.GetArrayLength() == 5, "input-pairs");
        var requested = new Dictionary<string, PairInput>(StringComparer.Ordinal);
        foreach (JsonElement pair in pairs.EnumerateArray())
        {
            budget.Row();
            Exact(pair, "name", "baseline", "candidate");
            string name = Guard.Text(pair.GetProperty("name").GetString());
            Guard.Require(Array.IndexOf(names, name) >= 0 && !requested.ContainsKey(name), "input-name");
            requested.Add(name, new PairInput(name, Image(pair.GetProperty("baseline"), budget),
                Image(pair.GetProperty("candidate"), budget)));
        }
        return requested;
    }

    public static void Exact(JsonElement value, params string[] names)
    {
        Guard.Require(value.ValueKind == JsonValueKind.Object, "input-object");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            Guard.Require(Array.IndexOf(names, property.Name) >= 0 && actual.Add(property.Name), "input-members");
        }
        Guard.Require(actual.Count == names.Length, "input-members");
    }

    public static ImageInput Image(JsonElement value, Budget budget)
    {
        budget.Row();
        Exact(value, "dll", "pdb", "dll_sha256", "pdb_sha256");
        return new ImageInput(Guard.Absolute(Guard.Text(value.GetProperty("dll").GetString())),
            Guard.Absolute(Guard.Text(value.GetProperty("pdb").GetString())),
            Guard.Sha(value.GetProperty("dll_sha256").GetString()), Guard.Sha(value.GetProperty("pdb_sha256").GetString()));
    }
}

/// <summary>Runs digest-bound inventory comparisons for the closed Contracts/Coverage roles.</summary>
/// <remarks>Other roles retain ordinary pair mode; report schemas and comparison accounting are unchanged.</remarks>
internal static class CoverageStages
{
    public static int CommonOnly(JsonElement root, byte[] input, string outputPath, string[] names,
        Budget budget, ref string stage)
    {
        Input.Exact(root, "schema", "inventory", "inventory_sha256", "manifest", "manifest_sha256", "pair_name");
        string partitionName = Guard.Text(root.GetProperty("pair_name").GetString());
        Guard.Require(partitionName is "Contracts" or "Coverage", "common-pair");
        string manifestPath = Guard.Absolute(Guard.Text(root.GetProperty("manifest").GetString()));
        string manifestHash = Guard.Sha(root.GetProperty("manifest_sha256").GetString());
        string inventoryPath = Guard.Absolute(Guard.Text(root.GetProperty("inventory").GetString()));
        string inventoryHash = Guard.Sha(root.GetProperty("inventory_sha256").GetString());
        stage = "input-pins";
        byte[] manifest = Files.Read(manifestPath, 128 * 1024, budget, false);
        Guard.Require(Program.Hash(manifest) == manifestHash, "manifest-sha256");
        budget.Check();
        using var manifestDocument = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 12 });
        Dictionary<string, PairInput> requested = Input.Pairs(manifestDocument.RootElement, names, budget);
        byte[] data = Files.Read(inventoryPath, 32 * 1024 * 1024, budget);
        Guard.Require(Program.Hash(data) == inventoryHash, "inventory-sha256");
        budget.Check();
        stage = "metadata-and-comparison";
        budget.Begin(partitionName, InspectionPhase.Common);
        // Charge every record/container/list item BEFORE DOM or typed inventory
        // materialization. This is a bounded token walk, not inspected-code loading.
        InventoryData.Charge(data, budget);
        using var inventoryDocument = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 12 });
        InventoryData.Validate(inventoryDocument.RootElement, "Inventory", budget);
        budget.Check();
        InventoryMetadataReportV2 inventory = JsonSerializer.Deserialize(data, ReportJsonContext.Default.InventoryMetadataReportV2)
            ?? throw new InspectionException("inventory-null");
        budget.Check();
        Guard.Require(inventory.Schema == "issue779-product-abi-inventory-metadata-v2"
            && inventory.PartitionName == partitionName && inventory.ManifestSha256 == manifestHash
            && inventory.Pairs.Count == 1 && inventory.Pairs[0].Name == partitionName
            && inventory.Pairs[0].Common is null && inventory.UnresolvedDirectDependencyRows is null
            && inventory.DirectDependencyReferenceMatches.Count == 0
            && !inventory.RuntimeCompatibilityProven && !inventory.CoverageCredit && !inventory.QualificationClaim
            && inventory.ChargedRows > 0 && inventory.ChargedRows <= 100000
            && inventory.InputBytes > 0 && inventory.InputBytes <= 128L * 1024 * 1024, "inventory-status");
        Guard.Sha(inventory.InputSha256);
        InventoryPairReportV2 pair = inventory.Pairs[0];
        PairInput selected = requested[partitionName];
        BindImage(pair.Baseline, selected.Baseline);
        budget.Row(); // charge validation of the explicit shared/unshared representation
        ImageReport candidate;
        if (pair.CandidateIsBaseline)
        {
            Guard.Require(pair.Candidate is null && pair.DllBytesEqual && pair.PdbBytesEqual
                && selected.Baseline.Dll == selected.Candidate.Dll && selected.Baseline.Pdb == selected.Candidate.Pdb
                && selected.Baseline.DllSha256 == selected.Candidate.DllSha256
                && selected.Baseline.PdbSha256 == selected.Candidate.PdbSha256, "inventory-shared-image");
            // The authenticated producer verified actual byte equality before
            // encoding this reference. Manifest path/pin equality alone cannot
            // authenticate a producer; that remains a mandatory caller binding.
            BindImage(pair.Baseline, selected.Candidate);
            candidate = pair.Baseline;
        }
        else
        {
            candidate = pair.Candidate ?? throw new InspectionException("inventory-candidate-missing");
            BindImage(candidate, selected.Candidate);
        }
        Guard.Require(pair.Baseline.Assembly.Name == candidate.Assembly.Name
            && pair.DllBytesEqual == (pair.Baseline.DllSha256 == candidate.DllSha256)
            && pair.PdbBytesEqual == (pair.Baseline.PdbSha256 == candidate.PdbSha256), "inventory-pair-pins");
        budget.Begin(partitionName, InspectionPhase.Common); // reset checkpoint after charged inventory decoding
        CommonReport common = Comparisons.Common(pair.Baseline, candidate, budget, new Comparisons.IndexCache());
        budget.Row();
        var report = new CommonMetadataReport("issue779-product-abi-common-metadata-v1", Program.Hash(input),
            inventoryHash, manifestHash, partitionName, common, budget.Rows, budget.InputBytes, false, false, false,
            ["metadata-only; inspected code was not loaded or invoked",
             "common comparison uses the complete digest-pinned retained inventory; binaries were not reparsed",
             "caller must authenticate the inventory producer, actual successful exit and all twenty original input pins",
             "direct dependency reconciliation is still required separately",
             "exact comparison observations are not runtime compatibility, source equivalence or coverage credit"]);
        stage = "output-create";
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            using var capped = new CappedOutput(output, budget);
            stage = "output-serialize";
            JsonSerializer.Serialize(capped, report, ReportJsonContext.Default.CommonMetadataReport);
            stage = "output-flush-close";
            capped.Flush();
            output.Flush(true);
            budget.Check();
        }
        stage = "publication";
        budget.Check();
        Console.WriteLine("metadata-report-written; runtime-compatibility-unproven");
        return 0;
    }

    private static void BindImage(ImageReport image, ImageInput expected)
    {
        Guard.Require(Guard.Absolute(image.DllPath) == expected.Dll && Guard.Absolute(image.PdbPath) == expected.Pdb
            && Guard.Sha(image.DllSha256) == expected.DllSha256 && Guard.Sha(image.PdbSha256) == expected.PdbSha256,
            "inventory-image-pins");
    }
}

internal static class InventoryData
{
    // Fixed schema, not reflection-derived from inspected code. Every required
    // field is retained and checked before source-generated deserialization.
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["Inventory"] = ["schema", "input_sha256", "partition_name", "manifest_sha256", "pairs", "direct_dependency_reference_matches", "unresolved_direct_dependency_rows", "charged_rows", "input_bytes", "runtime_compatibility_proven", "coverage_credit", "qualification_claim", "limitations"],
        ["Pair"] = ["name", "baseline", "candidate", "dll_bytes_equal", "pdb_bytes_equal", "candidate_is_baseline", "common"],
        ["Image"] = ["dll_path", "pdb_path", "dll_sha256", "pdb_sha256", "assembly", "mvid", "assembly_references", "type_references", "types", "members", "member_references", "internals_visible_to", "exported_types", "debug_directory", "pdb"],
        ["Assembly"] = ["name", "version", "culture", "public_key_or_token", "flags"],
        ["TypeReference"] = ["token", "name", "scope_kind", "scope_token"],
        ["Type"] = ["token", "name", "attributes", "access", "base_type", "interfaces", "generic_parameters"],
        ["Member"] = ["token", "kind", "type", "name", "signature", "attributes", "access", "is_static", "impl_attributes", "il_sha256", "il_bytes", "parameters", "generic_parameters", "accessors"],
        ["Parameter"] = ["sequence", "name", "attributes", "default_value"],
        ["Constant"] = ["type_code", "value_hex"],
        ["Generic"] = ["position", "name", "attributes", "constraints"],
        ["MemberReference"] = ["token", "kind", "type", "parent_kind", "name", "signature"],
        ["Friend"] = ["attribute_type", "name"],
        ["Forward"] = ["namespace", "name", "is_forwarder", "implementation_kind", "implementation_token"],
        ["Debug"] = ["type", "stamp", "guid", "age", "path", "algorithm", "checksum"],
        ["Pdb"] = ["id", "guid", "stamp", "code_view_count", "matching_code_view_count", "one_matching_code_view", "checksums", "documents", "sequence_points"],
        ["Checksum"] = ["algorithm", "expected", "raw_file_hash", "zeroed_id_hash", "raw_file_matches", "zeroed_id_matches"],
        ["Document"] = ["row", "name", "hash_algorithm", "hash", "language"],
        ["Sequence"] = ["method_row", "offset", "document_row", "hidden", "start_line", "start_column", "end_line", "end_column"],
    };

    public static void Charge(byte[] data, Budget budget)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 12 });
        var arrayParents = new bool[13];
        int depth = 0;
        while (true)
        {
            budget.Check();
            if (!reader.Read()) { break; }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    budget.Row(); // record or list container, before its materialization
                    Guard.Require(depth < arrayParents.Length, "inventory-depth");
                    arrayParents[depth++] = reader.TokenType == JsonTokenType.StartArray;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    Guard.Require(depth > 0, "inventory-depth");
                    depth--;
                    break;
                case JsonTokenType.PropertyName:
                    Guard.Require(reader.ValueSpan.Length <= 6 * 4096, "inventory-text");
                    Guard.Text(reader.GetString());
                    break;
                default:
                    if (depth > 0 && arrayParents[depth - 1]) { budget.Row(); }
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        // A 4096-byte metadata blob can have 8192 hex characters.
                        Guard.Require(reader.ValueSpan.Length <= 6 * 8192, "inventory-text");
                        string? text = reader.GetString();
                        Guard.Require(text is not null && text.Length <= 8192 && !text.Contains('\0'), "inventory-text");
                    }
                    break;
            }
        }
        Guard.Require(depth == 0, "inventory-depth");
        budget.Check();
    }

    public static void Validate(JsonElement value, string kind, Budget budget)
    {
        budget.Check();
        Input.Exact(value, Fields[kind]);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            budget.Check();
            JsonElement item = property.Value;
            if (item.ValueKind == JsonValueKind.Null)
            {
                Guard.Require(Nullable(kind, property.Name), "inventory-null-field");
                continue;
            }
            string nested = (kind, property.Name) switch
            {
                ("Pair", "baseline" or "candidate") => "Image",
                ("Image", "assembly") => "Assembly",
                ("Image", "pdb") => "Pdb",
                ("Parameter", "default_value") => "Constant",
                _ => "",
            };
            if (nested.Length > 0) { Validate(item, nested, budget); continue; }
            string elements = (kind, property.Name) switch
            {
                ("Inventory", "pairs") => "Pair",
                ("Inventory", "direct_dependency_reference_matches") => "Empty",
                ("Inventory", "limitations") or ("Type", "interfaces") or ("Generic", "constraints") => "Text",
                ("Image", "assembly_references") => "Assembly",
                ("Image", "type_references") => "TypeReference",
                ("Image", "types") => "Type",
                ("Image", "members") => "Member",
                ("Image", "member_references") => "MemberReference",
                ("Image", "internals_visible_to") => "Friend",
                ("Image", "exported_types") => "Forward",
                ("Image", "debug_directory") => "Debug",
                ("Type", "generic_parameters") or ("Member", "generic_parameters") => "Generic",
                ("Member", "parameters") => "Parameter",
                ("Member", "accessors") => "Integer",
                ("Pdb", "checksums") => "Checksum",
                ("Pdb", "documents") => "Document",
                ("Pdb", "sequence_points") => "Sequence",
                _ => "",
            };
            if (elements.Length > 0)
            {
                Guard.Require(item.ValueKind == JsonValueKind.Array, "inventory-array");
                if (elements == "Empty") { Guard.Require(item.GetArrayLength() == 0, "inventory-dependencies"); }
                if (kind == "Inventory" && property.Name == "pairs")
                {
                    Guard.Require(item.GetArrayLength() == 1, "inventory-pairs");
                }
                foreach (JsonElement element in item.EnumerateArray())
                {
                    budget.Check();
                    if (elements == "Text") { Guard.Require(element.ValueKind == JsonValueKind.String, "inventory-text"); }
                    else if (elements == "Integer") { Guard.Require(element.TryGetInt32(out _), "inventory-integer"); }
                    else
                    {
                        if (elements == "Document") { budget.Document(); }
                        Validate(element, elements, budget);
                    }
                }
                continue;
            }
            Guard.Require(item.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array), "inventory-field-kind");
        }
    }

    private static bool Nullable(string kind, string name) => (kind, name) switch
    {
        ("Inventory", "unresolved_direct_dependency_rows") or ("Pair", "common" or "candidate")
            or ("Type", "base_type") or ("Member", "il_sha256" or "accessors")
            or ("Parameter", "default_value") or ("Debug", "guid" or "age" or "path" or "algorithm" or "checksum")
            or ("Checksum", "expected" or "raw_file_hash" or "zeroed_id_hash") => true,
        _ => false,
    };
}

internal static class Files
{
    public static byte[] Read(string path, int limit, Budget budget, bool binary = true)
    {
        budget.Check();
        var before = new FileInfo(path);
        Guard.Require(before.Exists && before.LinkTarget is null && before.Length > 0 && before.Length <= limit
                      && (before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0,
            "file-shape-or-bound");
        long size = before.Length;
        DateTime modified = before.LastWriteTimeUtc;
        if (binary)
        {
            budget.Input(size);
        }
        byte[] bytes = new byte[checked((int)size)];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Guard.Require(stream.Length == size, "file-changed");
            int offset = 0;
            while (offset < bytes.Length)
            {
                budget.Check();
                int read = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                Guard.Require(read > 0, "file-changed");
                offset += read;
            }
            budget.Check();
            Guard.Require(stream.ReadByte() == -1 && stream.Length == size, "file-changed");
        }
        var after = new FileInfo(path);
        Guard.Require(after.Exists && after.LinkTarget is null && after.Length == size && after.LastWriteTimeUtc == modified,
            "file-changed");
        budget.Check();
        return bytes;
    }

    public static ImageBytes Verify(ImageInput input, Budget budget)
    {
        budget.Row();
        byte[] dll = Read(input.Dll, 32 * 1024 * 1024, budget);
        byte[] pdb = Read(input.Pdb, 16 * 1024 * 1024, budget);
        string dllHash = Program.Hash(dll);
        string pdbHash = Program.Hash(pdb);
        Guard.Require(dllHash == input.DllSha256 && pdbHash == input.PdbSha256, "file-sha256");
        budget.Check();
        return new ImageBytes(input.Dll, input.Pdb, dllHash, pdbHash, dll, pdb);
    }
}

internal sealed class CappedOutput(Stream inner, Budget budget) : Stream
{
    private long count;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => count;
    public override long Position { get => count; set => throw new NotSupportedException(); }
    public override void Flush() { budget.Check(); inner.Flush(); budget.Check(); }
    public override void Write(byte[] buffer, int offset, int size) => Write(buffer.AsSpan(offset, size));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        budget.Check();
        Guard.Require(buffer.Length <= 32L * 1024 * 1024 - count, "output-bound");
        inner.Write(buffer);
        count += buffer.Length;
        budget.Check();
    }
    public override int Read(byte[] buffer, int offset, int size) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class SignatureNames(MetadataReader reader, string assemblyName, Budget budget)
    : ISignatureTypeProvider<string, int>
{
    private int depth;
    private string Make(string value) { budget.Row(); return Guard.Text(value); }

    public string Definition(TypeDefinitionHandle handle)
    {
        budget.Check();
        Guard.Require(++depth <= 64, "type-depth");
        try
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = Guard.Text(reader.GetString(type.Name));
            TypeDefinitionHandle parent = type.GetDeclaringType();
            return Make(parent.IsNil ? "[" + assemblyName + "]" + Qualify(reader.GetString(type.Namespace), name)
                : Definition(parent) + "+" + name);
        }
        finally { depth--; }
    }

    public string Reference(TypeReferenceHandle handle)
    {
        budget.Check();
        Guard.Require(++depth <= 64, "type-depth");
        try
        {
            TypeReference type = reader.GetTypeReference(handle);
            string name = Guard.Text(reader.GetString(type.Name));
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return Make(Reference((TypeReferenceHandle)type.ResolutionScope) + "+" + name);
            }
            string scope = type.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? Guard.Text(reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name))
                : "unresolved-scope:" + type.ResolutionScope.Kind;
            return Make("[" + scope + "]" + Qualify(reader.GetString(type.Namespace), name));
        }
        finally { depth--; }
    }

    public string Entity(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => Reference((TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, 0, (TypeSpecificationHandle)handle, 0),
        _ => Make("unresolved-parent:" + handle.Kind),
    };

    private static string Qualify(string space, string name) => string.IsNullOrEmpty(space) ? name : space + "." + name;
    public string GetArrayType(string elementType, ArrayShape shape) => Make(elementType + "[rank=" + shape.Rank
        + ";sizes=" + Join(shape.Sizes) + ";lower=" + Join(shape.LowerBounds) + "]");
    private string Join(ImmutableArray<int> items)
    {
        var parts = new List<string>();
        foreach (int item in items) { budget.Row(); parts.Add(item.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return string.Join(",", parts);
    }
    public string GetByReferenceType(string elementType) => Make(elementType + "&");
    public string GetFunctionPointerType(MethodSignature<string> signature) => Make("fnptr:" + Method(signature));
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> arguments)
    {
        foreach (string argument in arguments) { budget.Row(); Guard.Text(argument); }
        return Make(genericType + "<" + string.Join(",", arguments) + ">");
    }
    public string GetGenericMethodParameter(int context, int index) => Make("!!" + index);
    public string GetGenericTypeParameter(int context, int index) => Make("!" + index);
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => Make((isRequired ? "modreq(" : "modopt(") + modifier + ")" + unmodifiedType);
    public string GetPinnedType(string elementType) => Make("pinned:" + elementType);
    public string GetPointerType(string elementType) => Make(elementType + "*");
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Make("primitive:" + typeCode);
    public string GetSZArrayType(string elementType) => Make(elementType + "[]");
    public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
        => Make("kind:" + rawTypeKind + ":" + Definition(handle));
    public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        => Make("kind:" + rawTypeKind + ":" + Reference(handle));
    public string GetTypeFromSpecification(MetadataReader metadata, int context, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        Guard.Require(++depth <= 64, "signature-depth");
        try { budget.Row(); return reader.GetTypeSpecification(handle).DecodeSignature(this, context); }
        finally { depth--; }
    }
    public string Method(MethodSignature<string> value)
    {
        foreach (string parameter in value.ParameterTypes) { budget.Row(); Guard.Text(parameter); }
        return Make("header:" + value.Header.RawValue + ";generic:" + value.GenericParameterCount
            + ";required:" + value.RequiredParameterCount + ";return:" + value.ReturnType
            + ";parameters:(" + string.Join(",", value.ParameterTypes) + ")");
    }
}

internal static class Inspector
{
    public static ImageReport Read(ImageBytes image, Budget budget)
    {
        budget.Check();
        using var dllStream = new MemoryStream(image.Dll, false);
        using var pe = new PEReader(dllStream);
        Guard.Require(pe.HasMetadata, "managed-pe");
        MetadataReader reader = pe.GetMetadataReader();
        Guard.Require(reader.IsAssembly, "assembly-metadata");
        AssemblyDefinition definition = reader.GetAssemblyDefinition();
        budget.Row();
        var identity = new AssemblyIdentity(Guard.Text(reader.GetString(definition.Name)), definition.Version.ToString(),
            Guard.Text(reader.GetString(definition.Culture)), BlobHex(reader, definition.PublicKey, budget),
            (int)definition.Flags);
        var names = new SignatureNames(reader, identity.Name, budget);
        var refs = new List<AssemblyIdentity>();
        foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
        {
            budget.Row();
            AssemblyReference reference = reader.GetAssemblyReference(handle);
            refs.Add(new AssemblyIdentity(Guard.Text(reader.GetString(reference.Name)), reference.Version.ToString(),
                Guard.Text(reader.GetString(reference.Culture)), BlobHex(reader, reference.PublicKeyOrToken, budget),
                (int)reference.Flags));
        }
        var typeRefs = new List<TypeReferenceRow>();
        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            budget.Row();
            TypeReference reference = reader.GetTypeReference(handle);
            typeRefs.Add(new TypeReferenceRow(MetadataTokens.GetToken(handle), names.Reference(handle),
                reference.ResolutionScope.Kind.ToString(), MetadataTokens.GetToken(reference.ResolutionScope)));
        }
        var types = new List<TypeRow>();
        var members = new List<MemberRow>();
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            budget.Row();
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string fullName = names.Definition(handle);
            var interfaces = new List<string>();
            foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
            {
                budget.Row();
                interfaces.Add(names.Entity(reader.GetInterfaceImplementation(implementation).Interface));
            }
            List<GenericParameterRow> typeParameters = GenericParameters(reader, type.GetGenericParameters(), names, budget);
            types.Add(new TypeRow(MetadataTokens.GetToken(handle), fullName, (int)type.Attributes,
                (type.Attributes & TypeAttributes.VisibilityMask).ToString(),
                type.BaseType.IsNil ? null : names.Entity(type.BaseType), interfaces, typeParameters));
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                budget.Row();
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                string signature = names.Method(method.DecodeSignature(names, 0));
                List<GenericParameterRow> methodParameters = GenericParameters(reader, method.GetGenericParameters(), names, budget);
                var parameters = new List<ParameterRow>();
                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    budget.Row();
                    Parameter parameter = reader.GetParameter(parameterHandle);
                    ConstantHandle constant = parameter.GetDefaultValue();
                    parameters.Add(new ParameterRow(parameter.SequenceNumber, Guard.Text(reader.GetString(parameter.Name)),
                        (int)parameter.Attributes, constant.IsNil ? null : Constant(reader, constant, budget)));
                }
                string? ilHash = null;
                int ilBytes = 0;
                if (method.RelativeVirtualAddress != 0)
                {
                    budget.Row();
                    ImmutableArray<byte> il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent();
                    ilBytes = il.Length;
                    ilHash = Hex(SHA256.HashData(il.AsSpan()));
                }
                members.Add(new MemberRow(MetadataTokens.GetToken(methodHandle), "method", fullName,
                    Guard.Text(reader.GetString(method.Name)), signature, (int)method.Attributes,
                    (method.Attributes & MethodAttributes.MemberAccessMask).ToString(),
                    (method.Attributes & MethodAttributes.Static) != 0, (int)method.ImplAttributes,
                    ilHash, ilBytes, parameters, methodParameters));
            }
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                budget.Row();
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                members.Add(new MemberRow(MetadataTokens.GetToken(fieldHandle), "field", fullName,
                    Guard.Text(reader.GetString(field.Name)), field.DecodeSignature(names, 0), (int)field.Attributes,
                    (field.Attributes & FieldAttributes.FieldAccessMask).ToString(),
                    (field.Attributes & FieldAttributes.Static) != 0, 0, null, 0, [], []));
            }
            foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
            {
                budget.Row();
                PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
                PropertyAccessors access = property.GetAccessors();
                var accessors = new List<int>();
                AddAccessor(access.Getter, accessors, budget);
                AddAccessor(access.Setter, accessors, budget);
                foreach (MethodDefinitionHandle other in access.Others) { AddAccessor(other, accessors, budget); }
                members.Add(new MemberRow(MetadataTokens.GetToken(propertyHandle), "property", fullName,
                    Guard.Text(reader.GetString(property.Name)), names.Method(property.DecodeSignature(names, 0)),
                    (int)property.Attributes, "accessor-methods", false, 0, null, 0, [], [], accessors));
            }
            foreach (EventDefinitionHandle eventHandle in type.GetEvents())
            {
                budget.Row();
                EventDefinition item = reader.GetEventDefinition(eventHandle);
                EventAccessors access = item.GetAccessors();
                var accessors = new List<int>();
                AddAccessor(access.Adder, accessors, budget);
                AddAccessor(access.Remover, accessors, budget);
                AddAccessor(access.Raiser, accessors, budget);
                foreach (MethodDefinitionHandle other in access.Others) { AddAccessor(other, accessors, budget); }
                members.Add(new MemberRow(MetadataTokens.GetToken(eventHandle), "event", fullName,
                    Guard.Text(reader.GetString(item.Name)), names.Entity(item.Type), (int)item.Attributes,
                    "accessor-methods", false, 0, null, 0, [], [], accessors));
            }
        }
        var memberRefs = new List<MemberReferenceRow>();
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            budget.Row();
            MemberReference member = reader.GetMemberReference(handle);
            bool method = member.GetKind() == MemberReferenceKind.Method;
            memberRefs.Add(new MemberReferenceRow(MetadataTokens.GetToken(handle), method ? "method" : "field",
                names.Entity(member.Parent), member.Parent.Kind.ToString(), Guard.Text(reader.GetString(member.Name)),
                method ? names.Method(member.DecodeMethodSignature(names, 0)) : member.DecodeFieldSignature(names, 0)));
        }
        var friends = new List<FriendRow>();
        foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
        {
            budget.Row();
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            string type = AttributeType(reader, names, attribute.Constructor);
            if (type.EndsWith("System.Runtime.CompilerServices.InternalsVisibleToAttribute", StringComparison.Ordinal))
            {
                budget.Row();
                BlobReader blob = reader.GetBlobReader(attribute.Value);
                Guard.Require(blob.ReadUInt16() == 1, "ivt-prolog");
                string friend = Guard.Text(blob.ReadSerializedString());
                Guard.Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "ivt-shape");
                friends.Add(new FriendRow(type, friend));
            }
        }
        var forwards = new List<ForwardRow>();
        foreach (ExportedTypeHandle handle in reader.ExportedTypes)
        {
            budget.Row();
            ExportedType item = reader.GetExportedType(handle);
            forwards.Add(new ForwardRow(Guard.Text(reader.GetString(item.Namespace)), Guard.Text(reader.GetString(item.Name)),
                item.IsForwarder, item.Implementation.Kind.ToString(), MetadataTokens.GetToken(item.Implementation)));
        }
        List<DebugRow> debug = Debug(pe, budget);
        PdbReport pdb = Pdb(image.Pdb, debug, budget);
        budget.Row();
        budget.Check();
        return new ImageReport(image.DllPath, image.PdbPath, image.DllSha256, image.PdbSha256,
            identity, reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString(), refs, typeRefs, types, members,
            memberRefs, friends, forwards, debug, pdb);
    }

    private static void AddAccessor(MethodDefinitionHandle handle, List<int> result, Budget budget)
    {
        if (!handle.IsNil) { budget.Row(); result.Add(MetadataTokens.GetToken(handle)); }
    }

    private static List<GenericParameterRow> GenericParameters(MetadataReader reader,
        GenericParameterHandleCollection handles, SignatureNames names, Budget budget)
    {
        var result = new List<GenericParameterRow>();
        foreach (GenericParameterHandle handle in handles)
        {
            budget.Row(); // charge before reading/materializing each generic-parameter row
            GenericParameter parameter = reader.GetGenericParameter(handle);
            var constraints = new List<string>();
            foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
            {
                budget.Row(); // charge before decoding/adding each constraint
                GenericParameterConstraint constraint = reader.GetGenericParameterConstraint(constraintHandle);
                constraints.Add(names.Entity(constraint.Type));
            }
            result.Add(new GenericParameterRow(parameter.Index, Guard.Text(reader.GetString(parameter.Name)),
                (int)parameter.Attributes, constraints));
        }
        return result;
    }

    private static ConstantRow Constant(MetadataReader reader, ConstantHandle handle, Budget budget)
    {
        budget.Row();
        System.Reflection.Metadata.Constant value = reader.GetConstant(handle);
        return new ConstantRow(value.TypeCode.ToString(), BlobHex(reader, value.Value, budget));
    }

    private static string AttributeType(MetadataReader reader, SignatureNames names, EntityHandle constructor)
        => constructor.Kind switch
        {
            HandleKind.MemberReference => names.Entity(reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => names.Definition(reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => "unresolved-attribute-constructor",
        };

    private static List<DebugRow> Debug(PEReader pe, Budget budget)
    {
        var rows = new List<DebugRow>();
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            budget.Row();
            string? guid = null, path = null, algorithm = null, checksum = null;
            int? age = null;
            if (entry.Type == DebugDirectoryEntryType.CodeView)
            {
                CodeViewDebugDirectoryData data = pe.ReadCodeViewDebugDirectoryData(entry);
                guid = data.Guid.ToString(); age = data.Age; path = Guard.Text(data.Path);
            }
            if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
            {
                PdbChecksumDebugDirectoryData data = pe.ReadPdbChecksumDebugDirectoryData(entry);
                algorithm = Guard.Text(data.AlgorithmName); checksum = Hex(data.Checksum);
            }
            rows.Add(new DebugRow(entry.Type.ToString(), entry.Stamp, guid, age, path, algorithm, checksum));
        }
        return rows;
    }

    private static PdbReport Pdb(byte[] bytes, List<DebugRow> debug, Budget budget)
    {
        using var stream = new MemoryStream(bytes, false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        MetadataReader reader = provider.GetMetadataReader();
        DebugMetadataHeader header = reader.DebugMetadataHeader ?? throw new InspectionException("portable-pdb-id");
        Guard.Require(header.Id.Length == 20, "portable-pdb-id");
        ReadOnlySpan<byte> id = header.Id.AsSpan();
        string guid = new Guid(id[..16]).ToString();
        uint stamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(id[16..20]);
        int codeViews = 0, matchingViews = 0;
        var checksums = new List<ChecksumObservation>();
        foreach (DebugRow row in debug)
        {
            budget.Row();
            if (row.Type == "CodeView")
            {
                codeViews++;
                if (row.Guid == guid && row.Stamp == stamp && row.Age == 1) { matchingViews++; }
            }
            if (row.Algorithm is not null)
            {
                budget.Row();
                string? actual = row.Algorithm switch
                {
                    "SHA256" => Hex(SHA256.HashData(bytes)),
                    "SHA1" => Hex(SHA1.HashData(bytes)),
                    _ => null,
                };
                string? zeroedId = null;
                if (row.Algorithm is "SHA256" or "SHA1")
                {
                    int offset = header.IdStartOffset;
                    Guard.Require(offset >= 0 && offset <= bytes.Length - 20, "pdb-id-offset");
                    using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(row.Algorithm));
                    Append(hash, bytes.AsSpan(0, offset), budget);
                    hash.AppendData(new byte[20]);
                    Append(hash, bytes.AsSpan(offset + 20), budget);
                    zeroedId = Hex(hash.GetHashAndReset());
                }
                checksums.Add(new ChecksumObservation(row.Algorithm, row.Checksum, actual, zeroedId,
                    actual is not null && actual == row.Checksum,
                    zeroedId is not null && zeroedId == row.Checksum));
            }
        }
        var documents = new List<DocumentRow>();
        foreach (DocumentHandle handle in reader.Documents)
        {
            budget.Row();
            budget.Document();
            Document document = reader.GetDocument(handle);
            documents.Add(new DocumentRow(MetadataTokens.GetRowNumber(handle), Guard.Text(reader.GetString(document.Name)),
                reader.GetGuid(document.HashAlgorithm).ToString(), BlobHex(reader, document.Hash, budget),
                reader.GetGuid(document.Language).ToString()));
        }
        var points = new List<SequenceRow>();
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            budget.Row();
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                budget.Row();
                DocumentHandle selected = point.Document.IsNil ? method.Document : point.Document;
                points.Add(new SequenceRow(MetadataTokens.GetRowNumber(handle), point.Offset,
                    selected.IsNil ? 0 : MetadataTokens.GetRowNumber(selected), point.IsHidden,
                    point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
            }
        }
        budget.Row();
        return new PdbReport(Hex(header.Id), guid, stamp, codeViews, matchingViews,
            codeViews == 1 && matchingViews == 1, checksums, documents, points);
    }

    private static string BlobHex(MetadataReader reader, BlobHandle handle, Budget budget)
    {
        budget.Check();
        BlobReader blob = reader.GetBlobReader(handle);
        Guard.Require(blob.Length is >= 0 and <= 4096, "metadata-blob-bound");
        byte[] bytes = blob.ReadBytes(blob.Length); // length is checked BEFORE allocation/copy
        budget.Check();
        return Hex(bytes);
    }

    internal static string Hex(byte[] bytes) => Hex(bytes.AsSpan());
    internal static string Hex(ImmutableArray<byte> bytes) => Hex(bytes.AsSpan());
    internal static string Hex(ReadOnlySpan<byte> bytes)
    {
        Guard.Require(bytes.Length <= 4096, "metadata-blob-bound");
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes, Budget budget)
    {
        while (!bytes.IsEmpty)
        {
            budget.Check();
            int length = Math.Min(65536, bytes.Length);
            hash.AppendData(bytes[..length]);
            bytes = bytes[length..];
        }
        budget.Check();
    }
}

internal static class Comparisons
{
    internal sealed class IndexCache
    {
        private readonly Dictionary<ImageReport, Dictionary<string, List<MemberRow>>> indexes
            = new(ReferenceEqualityComparer.Instance);

        public Dictionary<string, List<MemberRow>> Get(ImageReport image, Budget budget)
        {
            budget.Row(); // charge EVERY cache lookup, including hits
            if (indexes.TryGetValue(image, out Dictionary<string, List<MemberRow>>? existing)) { return existing; }
            budget.Row();
            Guard.Require(indexes.Count < 10, "index-cache-bound");
            Dictionary<string, List<MemberRow>> created = Index(image, budget);
            indexes.Add(image, created);
            return created;
        }
    }

    private static string Key(MemberRow member) => member.Kind + "|" + member.Type + "|" + member.Name + "|" + member.Signature;
    private static string Key(MemberReferenceRow member) => member.Kind + "|" + member.Type + "|" + member.Name + "|" + member.Signature;

    private static bool StringsEqual(List<string> baseline, List<string> candidate, Budget budget)
    {
        budget.Row();
        if (baseline.Count != candidate.Count) { return false; }
        bool equal = true;
        for (int index = 0; index < baseline.Count; index++)
        {
            budget.Row(); // charge each actual comparison, even after an earlier mismatch
            if (!string.Equals(baseline[index], candidate[index], StringComparison.Ordinal)) { equal = false; }
        }
        return equal;
    }

    private static (bool ShapeEqual, bool NamesEqual) GenericsEqual(List<GenericParameterRow> baseline,
        List<GenericParameterRow> candidate, Budget budget)
    {
        budget.Row();
        if (baseline.Count != candidate.Count) { return (false, false); }
        bool shapeEqual = true, namesEqual = true;
        for (int index = 0; index < baseline.Count; index++)
        {
            budget.Row();
            GenericParameterRow left = baseline[index], right = candidate[index];
            bool constraintsEqual = StringsEqual(left.Constraints, right.Constraints, budget);
            if (left.Position != right.Position || left.Attributes != right.Attributes || !constraintsEqual)
            {
                shapeEqual = false;
            }
            if (left.Position != right.Position || !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            {
                namesEqual = false;
            }
        }
        return (shapeEqual, namesEqual);
    }

    private static Dictionary<string, List<MemberRow>> Index(ImageReport image, Budget budget)
    {
        var result = new Dictionary<string, List<MemberRow>>(StringComparer.Ordinal);
        foreach (MemberRow member in image.Members)
        {
            budget.Row(); // charged BEFORE lookup/materializing any match bucket
            string key = Key(member);
            if (!result.TryGetValue(key, out List<MemberRow>? matches))
            {
                budget.Row(); matches = []; result.Add(key, matches);
            }
            budget.Row(); matches.Add(member);
        }
        return result;
    }

    public static CommonReport Common(ImageReport baseline, ImageReport candidate, Budget budget, IndexCache indexes)
    {
        var typeIndex = new Dictionary<string, TypeRow>(StringComparer.Ordinal);
        foreach (TypeRow type in baseline.Types) { budget.Row(); Guard.Require(typeIndex.TryAdd(type.Name, type), "duplicate-type"); }
        var types = new List<TypeComparison>();
        foreach (TypeRow type in candidate.Types)
        {
            budget.Row();
            if (typeIndex.TryGetValue(type.Name, out TypeRow? original))
            {
                bool interfacesEqual = StringsEqual(original.Interfaces, type.Interfaces, budget);
                var generic = GenericsEqual(original.GenericParameters, type.GenericParameters, budget);
                budget.Row();
                types.Add(new TypeComparison(type.Name, original.Attributes == type.Attributes,
                    original.BaseType == type.BaseType, interfacesEqual, generic.ShapeEqual, generic.NamesEqual,
                    original.Access, type.Access));
            }
        }
        Dictionary<string, List<MemberRow>> index = indexes.Get(baseline, budget);
        Dictionary<string, List<MemberRow>> reverse = indexes.Get(candidate, budget);
        var members = new List<MemberComparison>();
        foreach (MemberRow member in candidate.Members)
        {
            budget.Row();
            var matches = new List<MemberMatch>();
            if (index.TryGetValue(Key(member), out List<MemberRow>? originals))
            {
                foreach (MemberRow original in originals)
                {
                    var generic = GenericsEqual(original.GenericParameters, member.GenericParameters, budget);
                    budget.Row(); // charge EACH match before adding its materialized row
                    matches.Add(new MemberMatch(original.Token, original.Access, original.Attributes,
                        original.IsStatic, original.ImplAttributes, original.Attributes == member.Attributes,
                        original.IsStatic == member.IsStatic, original.ImplAttributes == member.ImplAttributes,
                        generic.ShapeEqual, generic.NamesEqual));
                }
            }
            budget.Row();
            members.Add(new MemberComparison(member.Token, member.Kind, member.Type, member.Name, member.Signature,
                member.Access, member.Attributes, member.IsStatic, member.ImplAttributes, matches));
        }
        var baselineOnly = new List<MissingMember>();
        foreach (MemberRow member in baseline.Members)
        {
            budget.Row();
            if (!reverse.ContainsKey(Key(member)))
            {
                budget.Row();
                baselineOnly.Add(new MissingMember(member.Token, member.Kind, member.Type,
                    member.Name, member.Signature, member.Access));
            }
        }
        budget.Row();
        return new CommonReport(types, members, baselineOnly);
    }

    public static List<DependencyMatch> Dependencies(
        Dictionary<string, (ImageReport Baseline, ImageReport Candidate)> images, Budget budget, IndexCache indexes)
    {
        var rows = new List<DependencyMatch>();
        foreach (string dependency in new[] { "Contracts", "Planner" })
        {
            budget.Begin(dependency, InspectionPhase.Dependency);
            ImageReport baseline = images[dependency].Baseline;
            ImageReport candidate = images[dependency].Candidate;
            Dictionary<string, List<MemberRow>> left = indexes.Get(baseline, budget);
            Dictionary<string, List<MemberRow>> right = indexes.Get(candidate, budget);
            string prefix = "[" + candidate.Assembly.Name + "]";
            foreach (string caller in new[] { "Cli", "Aspire", "Coverage" })
            {
                budget.Begin(caller, InspectionPhase.Dependency);
                foreach (string side in new[] { "baseline", "candidate" })
                {
                    ImageReport image = side == "baseline" ? images[caller].Baseline : images[caller].Candidate;
                    foreach (MemberReferenceRow reference in image.MemberReferences)
                    {
                        budget.Row();
                        bool selectedDependency = reference.Type.StartsWith(prefix, StringComparison.Ordinal)
                            || (reference.ParentKind == "TypeSpecification" && reference.Type.Contains(prefix, StringComparison.Ordinal));
                        if (!selectedDependency) { continue; }
                        int leftCount = 0, rightCount = 0;
                        var observations = new List<AccessObservation>();
                        // TypeSpecs require generic substitution; deliberately never claim a direct match.
                        if (reference.ParentKind is "TypeReference" or "TypeDefinition")
                        {
                            string key = Key(reference);
                            if (left.TryGetValue(key, out List<MemberRow>? originals))
                            {
                                foreach (MemberRow item in originals)
                                {
                                    budget.Row(); leftCount++;
                                    observations.Add(new AccessObservation("baseline", item.Token, item.Access, item.IsStatic));
                                }
                            }
                            if (right.TryGetValue(key, out List<MemberRow>? replacements))
                            {
                                foreach (MemberRow item in replacements)
                                {
                                    budget.Row(); rightCount++;
                                    observations.Add(new AccessObservation("candidate", item.Token, item.Access, item.IsStatic));
                                }
                            }
                        }
                        budget.Row();
                        rows.Add(new DependencyMatch(caller, side, dependency, reference.Token, reference.Type,
                            reference.Name, reference.Signature, reference.ParentKind, leftCount, rightCount,
                            observations, leftCount == 1 && rightCount == 1 ? "exact-direct-definitions" : "unresolved-or-ambiguous"));
                    }
                }
            }
        }
        return rows;
    }
}

internal sealed record ImageInput(string Dll, string Pdb, string DllSha256, string PdbSha256);
internal sealed record PairInput(string Name, ImageInput Baseline, ImageInput Candidate);
internal sealed record ImageBytes(string DllPath, string PdbPath, string DllSha256, string PdbSha256, byte[] Dll, byte[] Pdb);
internal sealed record PairBytes(ImageBytes Baseline, ImageBytes Candidate);
internal sealed record AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyOrToken, int Flags);
internal sealed record TypeReferenceRow(int Token, string Name, string ScopeKind, int ScopeToken);
internal sealed record GenericParameterRow(int Position, string Name, int Attributes, List<string> Constraints);
internal sealed record TypeRow(int Token, string Name, int Attributes, string Access, string? BaseType,
    List<string> Interfaces, List<GenericParameterRow> GenericParameters);
internal sealed record ConstantRow(string TypeCode, string ValueHex);
internal sealed record ParameterRow(int Sequence, string Name, int Attributes, ConstantRow? DefaultValue);
internal sealed record MemberRow(int Token, string Kind, string Type, string Name, string Signature,
    int Attributes, string Access, bool IsStatic, int ImplAttributes, string? IlSha256, int IlBytes,
    List<ParameterRow> Parameters, List<GenericParameterRow> GenericParameters, List<int>? Accessors = null);
internal sealed record MemberReferenceRow(int Token, string Kind, string Type, string ParentKind, string Name, string Signature);
internal sealed record FriendRow(string AttributeType, string Name);
internal sealed record ForwardRow(string Namespace, string Name, bool IsForwarder, string ImplementationKind, int ImplementationToken);
internal sealed record DebugRow(string Type, uint Stamp, string? Guid, int? Age, string? Path, string? Algorithm, string? Checksum);
internal sealed record ChecksumObservation(string Algorithm, string? Expected, string? RawFileHash,
    string? ZeroedIdHash, bool RawFileMatches, bool ZeroedIdMatches);
internal sealed record DocumentRow(int Row, string Name, string HashAlgorithm, string Hash, string Language);
internal sealed record SequenceRow(int MethodRow, int Offset, int DocumentRow, bool Hidden, int StartLine,
    int StartColumn, int EndLine, int EndColumn);
internal sealed record PdbReport(string Id, string Guid, uint Stamp, int CodeViewCount, int MatchingCodeViewCount,
    bool OneMatchingCodeView, List<ChecksumObservation> Checksums, List<DocumentRow> Documents, List<SequenceRow> SequencePoints);
internal sealed record ImageReport(string DllPath, string PdbPath, string DllSha256, string PdbSha256,
    AssemblyIdentity Assembly, string Mvid, List<AssemblyIdentity> AssemblyReferences, List<TypeReferenceRow> TypeReferences,
    List<TypeRow> Types, List<MemberRow> Members, List<MemberReferenceRow> MemberReferences, List<FriendRow> InternalsVisibleTo,
    List<ForwardRow> ExportedTypes, List<DebugRow> DebugDirectory, PdbReport Pdb);
internal sealed record TypeComparison(string Type, bool AttributesEqual, bool BaseTypeEqual,
    bool InterfacesEqual, bool GenericParametersEqual, bool GenericParameterNamesEqual,
    string BaselineAccess, string CandidateAccess);
internal sealed record MemberMatch(int Token, string Access, int Attributes, bool IsStatic, int ImplAttributes,
    bool AttributesEqual, bool StaticEqual, bool ImplAttributesEqual,
    bool GenericParametersEqual, bool GenericParameterNamesEqual);
internal sealed record MemberComparison(int CandidateToken, string Kind, string Type, string Name, string Signature,
    string CandidateAccess, int CandidateAttributes, bool CandidateStatic, int CandidateImplAttributes,
    List<MemberMatch> BaselineMatches);
internal sealed record MissingMember(int BaselineToken, string Kind, string Type, string Name, string Signature, string Access);
internal sealed record CommonReport(List<TypeComparison> CommonTypes, List<MemberComparison> CandidateMembers,
    List<MissingMember> BaselineOnlyMembers);
internal sealed record AccessObservation(string Side, int Token, string Access, bool IsStatic);
internal sealed record DependencyMatch(string Caller, string Side, string Dependency, int ReferenceToken, string Type,
    string Name, string Signature, string ParentKind, int BaselineMatches, int CandidateMatches,
    List<AccessObservation> AccessObservations, string Resolution);
internal sealed record PairReport(string Name, ImageReport Baseline, ImageReport Candidate,
    bool DllBytesEqual, bool PdbBytesEqual, CommonReport Common);
internal sealed record Report(string Schema, string InputSha256, List<PairReport> Pairs,
    List<DependencyMatch> DirectDependencyReferenceMatches, int UnresolvedDirectDependencyRows,
    int ChargedRows, long InputBytes, bool RuntimeCompatibilityProven, bool CoverageCredit, bool QualificationClaim,
    string[] Limitations);
internal sealed record PairMetadataReport(string Schema, string InputSha256, string PartitionName,
    string ManifestSha256, List<PairReport> Pairs, List<DependencyMatch> DirectDependencyReferenceMatches,
    int? UnresolvedDirectDependencyRows, int ChargedRows, long InputBytes, bool RuntimeCompatibilityProven,
    bool CoverageCredit, bool QualificationClaim, string[] Limitations);
internal sealed record InventoryPairReport(string Name, ImageReport Baseline, ImageReport Candidate,
    bool DllBytesEqual, bool PdbBytesEqual, CommonReport? Common);
internal sealed record InventoryMetadataReport(string Schema, string InputSha256, string PartitionName,
    string ManifestSha256, List<InventoryPairReport> Pairs, List<DependencyMatch> DirectDependencyReferenceMatches,
    int? UnresolvedDirectDependencyRows, int ChargedRows, long InputBytes, bool RuntimeCompatibilityProven,
    bool CoverageCredit, bool QualificationClaim, string[] Limitations);
internal sealed record CommonMetadataReport(string Schema, string InputSha256, string InventorySha256,
    string ManifestSha256, string PartitionName, CommonReport Common, int ChargedRows, long InputBytes,
    bool RuntimeCompatibilityProven, bool CoverageCredit, bool QualificationClaim, string[] Limitations);
internal sealed record InventoryPairReportV2(string Name, ImageReport Baseline, ImageReport? Candidate,
    bool DllBytesEqual, bool PdbBytesEqual, bool CandidateIsBaseline, CommonReport? Common);
internal sealed record InventoryMetadataReportV2(string Schema, string InputSha256, string PartitionName,
    string ManifestSha256, List<InventoryPairReportV2> Pairs, List<DependencyMatch> DirectDependencyReferenceMatches,
    int? UnresolvedDirectDependencyRows, int ChargedRows, long InputBytes, bool RuntimeCompatibilityProven,
    bool CoverageCredit, bool QualificationClaim, string[] Limitations);
