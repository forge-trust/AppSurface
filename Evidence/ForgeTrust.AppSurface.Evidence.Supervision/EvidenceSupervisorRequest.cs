using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Immutable checkpoint-one Linux launch metadata, without admission or native ownership authority.</summary>
/// <remarks>
/// Paths are canonical Linux spellings only: parsing neither opens them nor establishes ownership or absence of links.
/// The root owner must independently authenticate retained deployment, policy, revisions and the original deadline.
/// Runtime and tool roots may overlap; neither may overlap the subject. No process identities, commands, environment,
/// unit policy, provider registration or proof can be supplied. Lists are detached read-only snapshots.
/// </remarks>
internal sealed record EvidenceSupervisorRequest
{
    /// <summary>The only accepted checkpoint-one request schema.</summary>
    internal const string SchemaName = "evidence-supervisor-linux-v1";
    /// <summary>Maximum complete UTF-8 request size, including JSON escaping and whitespace.</summary>
    internal const int MaximumRequestBytes = 64 * 1024;
    /// <summary>Maximum number of declared relative diff paths.</summary>
    internal const int MaximumPaths = 4096;
    /// <summary>Maximum number of identifiers in either observation list.</summary>
    internal const int MaximumIds = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredFields =
    [
        "schema", "mode", "tool_root", "runtime_root", "runtime_host", "entry_path", "policy_file", "subject_root",
        "base_revision", "subject_revision", "workflow_identity", "paths", "observation_profile_ids",
        "observation_producer_ids", "job_deadline_utc", "admission_seconds", "start_seconds", "collection_seconds",
        "cleanup_seconds", "stopping_seconds",
    ];
    private static readonly string[] OptionalFields = ["diff_file", "diff_sha256", "solution"];

    private EvidenceSupervisorRequest() { }

    /// <summary>Gets the closed request schema.</summary>
    internal string Schema => SchemaName;
    /// <summary>Gets the checkpoint-one observation mode; no trusted mode is admitted by this parser.</summary>
    internal string Mode => "observation";
    /// <summary>Gets the lexical tool root; retained deployment auditing remains required.</summary>
    internal string ToolRoot { get; private init; } = string.Empty;
    /// <summary>Gets the lexical runtime root, required even for framework-dependent deployments.</summary>
    internal string RuntimeRoot { get; private init; } = string.Empty;
    /// <summary>Gets the runtime host path strictly beneath the runtime root.</summary>
    internal string RuntimeHost { get; private init; } = string.Empty;
    /// <summary>Gets the compiled worker entry path strictly beneath the tool root.</summary>
    internal string EntryPath { get; private init; } = string.Empty;
    /// <summary>Gets the policy path strictly beneath the tool root; parsing grants no policy authority.</summary>
    internal string PolicyFile { get; private init; } = string.Empty;
    /// <summary>Gets the lexical subject root, disjoint from tool and runtime roots.</summary>
    internal string SubjectRoot { get; private init; } = string.Empty;
    /// <summary>Gets requested 40-character lowercase base revision data, without authenticating Git.</summary>
    internal string BaseRevision { get; private init; } = string.Empty;
    /// <summary>Gets requested 40-character lowercase subject revision data, without authenticating Git.</summary>
    internal string SubjectRevision { get; private init; } = string.Empty;
    /// <summary>Gets nonempty workflow identity text of at most 256 UTF-8 bytes, without provider enrollment.</summary>
    internal string WorkflowIdentity { get; private init; } = string.Empty;
    /// <summary>Gets distinct relative path data, at most 4096 normalized items of 4095 UTF-8 bytes each.</summary>
    internal IReadOnlyList<string> Paths { get; private init; } = Array.Empty<string>();
    /// <summary>Gets distinct profile identifiers, without resolving profiles or bypassing admission.</summary>
    internal IReadOnlyList<string> ObservationProfileIds { get; private init; } = Array.Empty<string>();
    /// <summary>Gets distinct producer identifiers, without registering or starting a producer.</summary>
    internal IReadOnlyList<string> ObservationProducerIds { get; private init; } = Array.Empty<string>();
    /// <summary>Gets an explicit UTC timestamp; parsing does not check expiry or reset the owning allowance.</summary>
    internal DateTimeOffset JobDeadlineUtc { get; private init; }
    /// <summary>Gets requested admission seconds, from one through 30.</summary>
    internal int AdmissionSeconds { get; private init; }
    /// <summary>Gets requested startup seconds, from one through 120.</summary>
    internal int StartSeconds { get; private init; }
    /// <summary>Gets requested collection seconds, from one through 60.</summary>
    internal int CollectionSeconds { get; private init; }
    /// <summary>Gets requested cleanup seconds, from one through 600.</summary>
    internal int CleanupSeconds { get; private init; }
    /// <summary>Gets requested stopping seconds, from one through 30 and no greater than cleanup seconds.</summary>
    internal int StoppingSeconds { get; private init; }
    /// <summary>Gets an optional tool-contained diff path, present only with a non-null digest.</summary>
    internal string? DiffFile { get; private init; }
    /// <summary>Gets the optional 64-character lowercase diff digest; no file bytes are inspected here.</summary>
    internal string? DiffSha256 { get; private init; }
    /// <summary>Gets an optional solution path strictly beneath the subject root.</summary>
    internal string? Solution { get; private init; }

    /// <summary>Parses a closed request into copied data before any OS or runtime interaction.</summary>
    /// <param name="utf8Json">One strict UTF-8 JSON object, at most 64 KiB and four levels deep.</param>
    /// <returns>Immutable lexical metadata; the owning root must independently validate all native bindings.</returns>
    /// <exception cref="EvidenceAdmissionException">
    /// Fixed ASEVD402 rejection without input text or an inner exception.
    /// </exception>
    internal static EvidenceSupervisorRequest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumRequestBytes)
        {
            throw Rejected();
        }

        try
        {
            _ = StrictUtf8.GetCharCount(utf8Json.Span);
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var fields = Fields(document.RootElement);
            Require(Text(fields["schema"], 128) == SchemaName && Text(fields["mode"], 32) == "observation");
            var request = new EvidenceSupervisorRequest
            {
                ToolRoot = AbsolutePath(fields["tool_root"]),
                RuntimeRoot = AbsolutePath(fields["runtime_root"]),
                RuntimeHost = AbsolutePath(fields["runtime_host"]),
                EntryPath = AbsolutePath(fields["entry_path"]),
                PolicyFile = AbsolutePath(fields["policy_file"]),
                SubjectRoot = AbsolutePath(fields["subject_root"]),
                BaseRevision = Hex(fields["base_revision"], 40),
                SubjectRevision = Hex(fields["subject_revision"], 40),
                WorkflowIdentity = Text(fields["workflow_identity"], 256),
                Paths = Strings(fields["paths"], MaximumPaths, identifiers: false),
                ObservationProfileIds = Strings(fields["observation_profile_ids"], MaximumIds, identifiers: true),
                ObservationProducerIds = Strings(fields["observation_producer_ids"], MaximumIds, identifiers: true),
                JobDeadlineUtc = UtcDate(fields["job_deadline_utc"]),
                AdmissionSeconds = Seconds(fields["admission_seconds"], 30),
                StartSeconds = Seconds(fields["start_seconds"], 120),
                CollectionSeconds = Seconds(fields["collection_seconds"], 60),
                CleanupSeconds = Seconds(fields["cleanup_seconds"], 600),
                StoppingSeconds = Seconds(fields["stopping_seconds"], 30),
                DiffFile = Optional(fields, "diff_file", AbsolutePath),
                DiffSha256 = Optional(fields, "diff_sha256", value => Hex(value, 64)),
                Solution = Optional(fields, "solution", AbsolutePath),
            };
            Require(request.StoppingSeconds <= request.CleanupSeconds
                && !Overlap(request.ToolRoot, request.SubjectRoot) && !Overlap(request.RuntimeRoot, request.SubjectRoot)
                && Contained(request.RuntimeRoot, request.RuntimeHost) && Contained(request.ToolRoot, request.EntryPath)
                && Contained(request.ToolRoot, request.PolicyFile)
                && (request.DiffFile is null ? request.DiffSha256 is null
                    : request.DiffSha256 is not null && Contained(request.ToolRoot, request.DiffFile))
                && (request.Solution is null || Contained(request.SubjectRoot, request.Solution)));
            return request;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
                                     or KeyNotFoundException)
        {
            throw Rejected();
        }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            Require(fields.Count < RequiredFields.Length + OptionalFields.Length && names.Add(property.Name)
                && (RequiredFields.Contains(property.Name, StringComparer.Ordinal)
                    || OptionalFields.Contains(property.Name, StringComparer.Ordinal)));
            fields.Add(property.Name, property.Value);
        }
        Require(RequiredFields.All(fields.ContainsKey));
        return fields;
    }

    private static string Text(JsonElement value, int maximumBytes)
    {
        Require(value.ValueKind == JsonValueKind.String);
        var text = value.GetString()!;
        Require(!string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl)
            && StrictUtf8.GetByteCount(text) <= maximumBytes);
        return text;
    }

    private static string AbsolutePath(JsonElement value)
    {
        var path = Text(value, 4095);
        Require(path.Length > 1 && path[0] == '/' && !path.Contains('\\') && Segments(path[1..]));
        return path;
    }

    private static IReadOnlyList<string> Strings(JsonElement value, int maximum, bool identifiers)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum);
        var items = new string[value.GetArrayLength()];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var text = Text(item, identifiers ? 96 : 4095);
            Require(names.Add(text));
            Require(identifiers
                ? char.IsAsciiLetterOrDigit(text[0])
                    && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                : text[0] != '/' && !text.Contains('\\') && Segments(text));
            items[index++] = text;
        }
        return Array.AsReadOnly(items);
    }

    private static string Hex(JsonElement value, int length)
    {
        var text = Text(value, length);
        Require(text.Length == length && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        return text;
    }

    private static DateTimeOffset UtcDate(JsonElement value)
    {
        var text = Text(value, 64);
        Require((text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            && value.TryGetDateTimeOffset(out _));
        var date = value.GetDateTimeOffset();
        Require(date.Offset == TimeSpan.Zero);
        return date;
    }

    private static int Seconds(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        var seconds = value.GetInt32();
        Require(seconds >= 1 && seconds <= maximum);
        return seconds;
    }

    private static string? Optional(Dictionary<string, JsonElement> fields, string name,
        Func<JsonElement, string> parse) =>
        !fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : parse(value);

    private static bool Segments(string path) =>
        path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static bool Contained(string root, string path) => path.StartsWith(root + "/", StringComparison.Ordinal);

    private static bool Overlap(string left, string right) =>
        left == right || Contained(left, right) || Contained(right, left);

    private static void Require(bool condition)
    {
        if (!condition) throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD402", "The supervisor launch request is malformed or exceeds its fixed limits.");
}
