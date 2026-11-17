using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Fixed v3 Git substrate: isolated child context, bounded object work and exact retained diff bytes.</summary>
/// <remarks>No command fetches or writes repository state. A matching clean checkout and complete local history are
/// prerequisites even for historical replay. Semantic parsing by the independent verifier remains separate.</remarks>
internal sealed class CoverageFanInDiffContext(CoverageFanInEngineRequest request, CoverageFanInBudget budget)
{
    internal const string PolicyId = "appsurface-git-patch-v1";
    internal const int StreamLimit = 1024 * 1024;
    internal const int PatchLimit = 20 * 1024 * 1024;
    internal const long BlobLimit = 20 * 1024 * 1024;
    internal const long BlobTotalLimit = 256 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Neutralized = new(StringComparer.OrdinalIgnoreCase)
    {
        "diff.renames", "diff.renamelimit", "diff.orderfile", "diff.mnemonicprefix", "diff.noprefix",
        "diff.suppressblankempty", "diff.context", "diff.interhunkcontext", "diff.algorithm", "diff.indentheuristic"
    };
    private readonly IReadOnlyDictionary<string, string?> _environment = ChildEnvironment(request.Expected.Source.GetProperty("commit").GetString()!);
    private readonly string _root = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(request.RepositoryRoot));
    private string? _configurationSnapshot;
    internal string? GitVersion { get; private set; }
    internal IReadOnlySet<string> TrackedPaths { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Builds a copied child-only environment, clearing inherited Git overrides without touching shared caches.</summary>
    internal static IReadOnlyDictionary<string, string?> ChildEnvironment(string head)
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name && name.StartsWith("GIT_", OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) environment[name] = null;
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = nullDevice,
            ["GIT_CONFIG_SYSTEM"] = nullDevice,
            ["GIT_ATTR_NOSYSTEM"] = "1",
            ["GIT_ATTR_SOURCE"] = head,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["LC_ALL"] = "C",
            ["LANG"] = "C"
        }) environment[name] = value;
        return environment;
    }

    /// <summary>Checks the supported Git version and repository metadata before the first object command.</summary>
    internal async Task InitializeAsync(CancellationToken token)
    {
        var version = (await RunAsync(["--version"], token).ConfigureAwait(false)).Trim();
        RequireSupportedVersion(version);
        GitVersion = version["git version ".Length..].Split(' ')[0];
        await VerifyMetadataAsync(token).ConfigureAwait(false);
    }

    /// <summary>Requires2.46 or later, accepting numeric upstream versions and recognized Apple/Windows vendor suffixes.</summary>
    internal static void RequireSupportedVersion(string text)
    {
        var match = Regex.Match(text, @"^git version (?<major>[0-9]+)\.(?<minor>[0-9]+)(?:\.[0-9]+){0,2}(?:\.windows\.[0-9]+| \(Apple Git-[0-9]+\))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor) || major < 2 || major == 2 && minor < 46)
            throw Fail("unsupported-git-version", "Install a supported Git2.46 or later, then regenerate evidence in a fresh matching checkout.");
    }

    /// <summary>Refuses config/attribute/history changes and inputs whose effective semantics are outside this fixed policy.</summary>
    internal async Task VerifyMetadataAsync(CancellationToken token)
    {
        var configuration = await RunAsync(["config", "--includes", "--name-only", "--get-regexp",
            "^(diff\\.|extensions\\.partialclone$|remote\\..*\\.(promisor|partialclonefilter)$)"], token, allowNoMatch: true).ConfigureAwait(false);
        foreach (var name in configuration.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (!Neutralized.Contains(name.TrimEnd('\r')))
                throw Fail("unsupported-diff", "Ask the maintainer to remove unsupported local diff drivers/partial-clone configuration or qualify a separate diff policy; do not skip files.");
        if (_configurationSnapshot is not null && _configurationSnapshot != configuration)
            throw Fail("identity-mismatch", "Repository configuration changed; reconcile source context and regenerate in a dedicated checkout.");
        _configurationSnapshot ??= configuration;
        if ((await RunAsync(["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false)).TrimEnd('\r', '\n') != "false")
            throw Fail("merge-base-unavailable", "Provide complete pinned base/head history before regenerating; no automatic fetch is performed.");
        var common = (await RunAsync(["rev-parse", "--git-common-dir"], token).ConfigureAwait(false)).TrimEnd('\r', '\n');
        var commonPath = Path.GetFullPath(common, _root);
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(commonPath, false, false)
            ?? throw Fail("unsupported-diff", "Ask the maintainer to restore a supported local Git directory.");
        var info = Path.Join(commonPath, "info");
        if (Directory.Exists(info))
        {
            using var retained = CoverageRunOutputLease.AcquireArtifactDirectory(info, false, false)
                ?? throw Fail("unsupported-diff", "Ask the maintainer to resolve unsupported Git metadata.");
            var entries = retained.EnumerateArtifactEntries(100000);
            foreach (var name in new[] { "attributes", "grafts" })
            {
                if (!entries.Any(x => x.Name == name)) continue;
                using var file = retained.OpenArtifactFile(name, false, false);
                if (file.Length != 0) throw Fail("unsupported-diff", "Nonempty local info/attributes or grafts are unsupported; ask the maintainer to resolve them before fresh proof.");
            }
            retained.VerifyArtifactBinding();
        }
        directory.VerifyArtifactBinding();
    }

    /// <summary>Executes isolated, strictly decoded commands under the current operation token and per-probe deadline.</summary>
    internal async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token,
        string? workingDirectory = null, int maximumOutput = StreamLimit, byte[]? input = null, bool allowNoMatch = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.Expected.Limits.GetProperty("identitySeconds").GetInt32()));
        var runner = request.IdentityRunner ?? new CliWrapCoverageRunProcessRunner();
        var command = new CoverageRunProcessRequest("git", Prefix().Concat(arguments).ToArray(), workingDirectory ?? _root,
            null, null, CoverageRunProcessLease.Detached())
        {
            // A submodule is a separate object database: its identity probes must use its own
            // HEAD attributes after the caller checks that HEAD against the pinned gitlink.
            EnvironmentOverrides = workingDirectory is not null && System.IO.Path.GetFullPath(workingDirectory) != _root
                ? new Dictionary<string, string?>(_environment) { ["GIT_ATTR_SOURCE"] = "HEAD" } : _environment,
            StandardOutputLimitBytes = maximumOutput,
            StrictUtf8 = true,
            StandardInputBytes = input,
            BudgetCharge = budget.Charge
        };
        CoverageRunProcessResult result;
        try { result = await runner.RunAsync(command, deadline.Token).ConfigureAwait(false); }
        catch (DecoderFallbackException)
        { throw Fail("malformed-diff", "Git output is not valid UTF8; restore a supported source encoding before regenerating complete evidence."); }
        if (result.OutputTruncated) throw Fail("truncated-diff", "Regenerate complete bounded evidence; do not treat truncated data as an empty patch.");
        if ((result.ExitCode != 0 && !(allowNoMatch && result.ExitCode == 1)) || !string.IsNullOrEmpty(result.StandardError))
            throw Fail("unsupported-diff", "Inspect the supported Git prerequisites and full pinned history; resolve the cause with the maintainer before a fresh retry.");
        return result.StandardOutput ?? result.Output;
    }

    private static string[] Prefix()
    {
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return ["--no-pager", "--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.quotePath=true",
            "-c", "core.attributesFile=" + nullDevice, "-c", "diff.renames=true", "-c", "diff.renameLimit=1000",
            "-c", "diff.orderFile=" + nullDevice, "-c", "diff.mnemonicPrefix=false", "-c", "diff.noprefix=false",
            "-c", "diff.suppressBlankEmpty=false"];
    }

    private static string[] DiffOptions(bool renames) => renames
        ? ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--no-renames", "--find-renames=50%",
            "--no-rename-empty", "-l1000", "--diff-algorithm=myers", "--no-indent-heuristic", "--ignore-submodules=none", "--submodule=short"]
        : ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--no-renames", "--no-rename-empty",
            "-l1000", "--diff-algorithm=myers", "--no-indent-heuristic", "--ignore-submodules=none", "--submodule=short"];

    /// <summary>Reads both independent Git views after preflighting bounded blob work, without trusting a retained claim.</summary>
    internal async Task<CoverageFanInDiffInput> ReadAsync(CancellationToken token)
    {
        await VerifyMetadataAsync(token).ConfigureAwait(false);
        var head = request.Expected.Source.GetProperty("commit").GetString()!;
        var requestedBase = request.Expected.Gate.GetProperty("diffBase").GetString()!;
        var format = (await RunAsync(["rev-parse", "--show-object-format"], token).ConfigureAwait(false)).TrimEnd('\r', '\n');
        var length = format switch { "sha1" => 40, "sha256" => 64, _ => throw Fail("unsupported-diff", "Use a qualified sha1 or sha256 Git object format.") };
        if (!IsOid(head, length) || !IsOid(requestedBase, length) || !IsOid(request.Expected.Source.GetProperty("tree").GetString()!, length))
            throw Fail("identity-mismatch", "Use full source/base object IDs from the matching repository object format.");
        string mergeBases;
        try { mergeBases = await RunAsync(["merge-base", "--all", requestedBase, head], token).ConfigureAwait(false); }
        catch (CoverageFanInEvidenceException exception) when (exception.Cause == "unsupported-diff")
        { throw Fail("merge-base-unavailable", "Restore complete local requested-base/head history with one merge base before regenerating; no automatic fetch or rebase is performed."); }
        var bases = mergeBases.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (bases.Length != 1 || !IsOid(bases[0], length))
            throw Fail("merge-base-unavailable", "The patch needs one complete merge base; ask the owner to restructure history or qualify a different policy. Do not automatically rebase.");
        var endpoints = new[] { bases[0], head, "--" };
        var preflight = await RunAsync(DiffOptions(false).Concat(["--raw", "-z", "--no-abbrev"]).Concat(endpoints).ToArray(), token).ConfigureAwait(false);
        var candidates = CoverageFanInDiffParser.ReadRaw(preflight, length);
        if (candidates.Count(x => x.Kind is "added" or "deleted") > 1000)
            throw Fail("limit-exceeded", "Over1000 rename candidates require owner-approved restructuring or a separately qualified policy; no automatic no-renames fallback.");
        var objects = candidates.SelectMany(x => new[] { x.OldObject, x.NewObject }).Where(x => x.Any(c => c != '0'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var stdin = Utf8.GetBytes(string.Concat(objects.Select(x => x + "\n")));
        if (stdin.Length > StreamLimit) throw Fail("limit-exceeded", "The object inventory exceeds the fixed input limit; ask the maintainer to restructure the change.");
        if (objects.Length != 0)
        {
            var sizes = await RunAsync(["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"], token, input: stdin).ConfigureAwait(false);
            AdmitBlobSizes(objects, sizes, budget);
        }
        var raw = await RunAsync(DiffOptions(true).Concat(["--raw", "-z", "--no-abbrev"]).Concat(endpoints).ToArray(), token).ConfigureAwait(false);
        var patch = await RunAsync(DiffOptions(true).Concat(["--patch", "--unified=0", "--inter-hunk-context=0", "--full-index",
            "--src-prefix=a/", "--dst-prefix=b/", "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= "])
            .Concat(endpoints).ToArray(), token, maximumOutput: PatchLimit).ConfigureAwait(false);
        await VerifyMetadataAsync(token).ConfigureAwait(false);
        return new(requestedBase, bases[0], head, length, Utf8.GetBytes(patch), raw)
        { MaximumRecords = request.Expected.Bounds.TotalRecords };
    }

    /// <summary>Validates exactly one result per distinct object and charges logical blob work before content/rename diff.</summary>
    internal static void AdmitBlobSizes(IReadOnlyList<string> objects, string output, CoverageFanInBudget budget)
    {
        var lines = output.Split('\n');
        if (lines.Length != objects.Count + 1 || lines[^1] != string.Empty) throw Fail("malformed-diff", "Restore a complete supported Git object inventory before retrying.");
        long total = 0;
        for (var i = 0; i < objects.Count; i++)
        {
            var fields = lines[i].Split(' ');
            if (fields.Length != 3 || fields[0] != objects[i] || fields[1] != "blob"
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw Fail("malformed-diff", "The pinned object inventory is missing, invalid or unsupported; restore complete objects.");
            if (size > BlobLimit || size > BlobTotalLimit - total)
                throw Fail("limit-exceeded", "The patch exceeds20MiB/blob or256MiB distinct blob work; restructure with owner approval or separately qualify a policy.");
            total = checked(total + size);
        }
        budget.Charge(total);
    }

    internal static bool IsOid(string text, int length) => text.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static CoverageFanInEvidenceException Fail(string cause, string recovery) => new(cause, "git", recovery);
}

/// <summary>Admitted raw Git views; neither contains a trusted applicability classification.</summary>
internal sealed record CoverageFanInDiffInput(string RequestedBase, string MergeBase, string Head, int ObjectIdLength, byte[] Patch, string Raw)
{
    /// <summary>Existing total-record bound applied to added-line expansion, never a new exemption or increased budget.</summary>
    internal int MaximumRecords { get; init; } = 2000000;
}

/// <summary>Exact raw-object change with independently cross-checked surviving addition ranges.</summary>
internal sealed record CoverageFanInDiffChange(string Kind, string? OldPath, string? NewPath, string OldObject, string NewObject,
    string OldMode, string NewMode, IReadOnlyList<CoverageFanInAddedRange> AddedRanges);

/// <summary>Positive, sorted and coalesced surviving new-side line range.</summary>
internal sealed record CoverageFanInAddedRange(int Start, int Count);

/// <summary>Writer-owned raw/hunk parser. The verifier deliberately owns a separate semantic implementation.</summary>
internal static class CoverageFanInDiffParser
{
    /// <summary>Decodes NUL-separated regular-file raw entries, preserving rename origins and exact Unicode identity.</summary>
    internal static IReadOnlyList<CoverageFanInDiffChange> ReadRaw(string raw, int oidLength)
    {
        if (raw.Length == 0) return [];
        var fields = raw.Split('\0');
        if (fields[^1].Length != 0) throw Invalid();
        var result = new List<CoverageFanInDiffChange>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length - 1;)
        {
            var header = fields[i++].Split(' ');
            if (header.Length != 5 || !header[0].StartsWith(':') || header[0].Length != 7
                || !CoverageFanInDiffContext.IsOid(header[2], oidLength) || !CoverageFanInDiffContext.IsOid(header[3], oidLength)
                || i >= fields.Length - 1) throw Invalid();
            var oldMode = header[0][1..]; var newMode = header[1]; var status = header[4];
            var first = Path(fields[i++]);
            string? oldPath = first, newPath = first;
            var kind = status switch
            {
                "A" => "added",
                "D" => "deleted",
                "M" => "modified",
                _ when status.StartsWith('R') && int.TryParse(status.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var score)
                    && score is >= 50 and <= 100 => "renamed",
                _ => throw Unsupported()
            };
            if (kind == "added") oldPath = null;
            if (kind == "deleted") newPath = null;
            if (kind == "renamed")
            {
                if (i >= fields.Length - 1) throw Invalid();
                newPath = Path(fields[i++]);
                if (oldPath == newPath) throw Invalid();
            }
            if ((oldPath is null ? oldMode != "000000" || header[2].Any(c => c != '0') : oldMode is not ("100644" or "100755"))
                || (newPath is null ? newMode != "000000" || header[3].Any(c => c != '0') : newMode is not ("100644" or "100755")))
                throw Unsupported();
            if (oldPath is not null && header[2].All(c => c == '0') || newPath is not null && header[3].All(c => c == '0')) throw Invalid();
            if (kind == "modified" && header[2] == header[3])
            {
                if (oldMode == newMode) throw Invalid();
                kind = "modeChanged";
            }
            if (!names.Add(newPath ?? oldPath!)) throw Invalid();
            result.Add(new(kind, oldPath, newPath, header[2], header[3], oldMode, newMode, []));
        }
        return result;
    }

    /// <summary>Pairs every patch file with one raw record, validating object/mode headers and complete hunk counts.</summary>
    internal static IReadOnlyList<CoverageFanInDiffChange> Parse(CoverageFanInDiffInput input)
    {
        var changes = ReadRaw(input.Raw, input.ObjectIdLength);
        var patch = new UTF8Encoding(false, true).GetString(input.Patch);
        if (patch.Length == 0)
        {
            if (changes.Count != 0) throw Invalid();
            return changes;
        }
        if (!patch.EndsWith('\n')) throw Invalid();
        var sections = Regex.Split(patch, "^diff --git ", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (sections[0].Length != 0 || sections.Length != changes.Count + 1) throw Invalid();
        var result = new List<CoverageFanInDiffChange>();
        var records = 0;
        for (var i = 0; i < changes.Count; i++)
        {
            var parsed = ParseFile(changes[i], sections[i + 1], input.MaximumRecords - records);
            records = checked(records + parsed.AddedRanges.Sum(x => x.Count));
            result.Add(parsed);
        }
        return result.OrderBy(x => x.NewPath ?? x.OldPath, StringComparer.Ordinal)
            .ThenBy(x => x.OldPath, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal).ToArray();
    }

    private static CoverageFanInDiffChange ParseFile(CoverageFanInDiffChange change, string section, int remainingRecords)
    {
        var lines = section.Split('\n');
        if (lines[0] != Quote("a/" + (change.OldPath ?? change.NewPath)) + " " + Quote("b/" + (change.NewPath ?? change.OldPath))) throw Invalid();
        var oldRemaining = 0; var newRemaining = 0; var position = 0;
        var previousOldEnd = -1; var previousNewEnd = -1;
        var additions = new SortedSet<int>();
        var oldHeader = false; var newHeader = false; var index = false; var hunk = false;
        var renameFrom = false; var renameTo = false;
        for (var i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (line == "\\ No newline at end of file") { if (!hunk) throw Invalid(); continue; }
            if (oldRemaining != 0 || newRemaining != 0)
            {
                if (line.StartsWith('+') && newRemaining > 0)
                {
                    if (change.NewPath is null || position <= 0) throw Invalid();
                    if (additions.Count >= remainingRecords) throw CoverageFanInDiffContext.Fail("limit-exceeded", "The patch exceeds the existing total-record limit; resolve scope with the maintainer before fresh proof.");
                    if (!additions.Add(position)) throw Invalid();
                    position = checked(position + 1); newRemaining--;
                }
                else if (line.StartsWith('-') && oldRemaining > 0) oldRemaining--;
                else if (line.StartsWith(' ') && oldRemaining > 0 && newRemaining > 0) { position = checked(position + 1); oldRemaining--; newRemaining--; }
                else throw Invalid();
                continue;
            }
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (!index || !oldHeader || !newHeader) throw Invalid();
                var match = Regex.Match(line, @"^@@ -([0-9]+)(?:,([0-9]+))? \+([0-9]+)(?:,([0-9]+))? @@(?: .*)?$", RegexOptions.CultureInvariant);
                if (!match.Success) throw Invalid();
                var oldStart = Number(match.Groups[1].Value); var newStart = Number(match.Groups[3].Value);
                oldRemaining = match.Groups[2].Success ? Number(match.Groups[2].Value) : 1;
                newRemaining = match.Groups[4].Success ? Number(match.Groups[4].Value) : 1;
                if (oldRemaining == 0 && newRemaining == 0 || oldRemaining > 0 && oldStart == 0 || newRemaining > 0 && newStart == 0) throw Invalid();
                if (oldStart < previousOldEnd || newStart < previousNewEnd) throw Invalid();
                previousOldEnd = checked(oldStart + oldRemaining); previousNewEnd = checked(newStart + newRemaining);
                position = newStart; hunk = true; continue;
            }
            if (hunk) throw Invalid();
            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                if (index) throw Invalid();
                var fields = line[6..].Split(' '); var ids = fields[0].Split("..", StringSplitOptions.None);
                if (ids.Length != 2 || ids[0] != change.OldObject || ids[1] != change.NewObject
                    || fields.Length > 2 || fields.Length == 2 && (change.OldMode != change.NewMode || fields[1] != change.NewMode)) throw Invalid();
                index = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal)) { if (oldHeader) throw Invalid(); Header(line[4..], change.OldPath, "a/"); oldHeader = true; }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)) { if (newHeader || !oldHeader) throw Invalid(); Header(line[4..], change.NewPath, "b/"); newHeader = true; }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { if (change.Kind != "renamed" || Decode(line[12..]) != change.OldPath || renameFrom) throw Invalid(); renameFrom = true; }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal)) { if (change.Kind != "renamed" || Decode(line[10..]) != change.NewPath || renameTo) throw Invalid(); renameTo = true; }
            else if (line.StartsWith("similarity index ", StringComparison.Ordinal))
            { if (change.Kind != "renamed" || !line.EndsWith('%') || Number(line[17..^1]) is < 50 or > 100) throw Invalid(); }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal)) { if (change.Kind != "added" || line[14..] != change.NewMode) throw Invalid(); }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal)) { if (change.Kind != "deleted" || line[18..] != change.OldMode) throw Invalid(); }
            else if (line.StartsWith("old mode ", StringComparison.Ordinal)) { if (line[9..] != change.OldMode || change.OldMode == change.NewMode) throw Invalid(); }
            else if (line.StartsWith("new mode ", StringComparison.Ordinal)) { if (line[9..] != change.NewMode || change.OldMode == change.NewMode) throw Invalid(); }
            else throw Unsupported();
        }
        if (oldRemaining != 0 || newRemaining != 0 || oldHeader != newHeader
            || change.Kind == "renamed" && (!renameFrom || !renameTo)
            || change.OldObject != change.NewObject && !index
            || change.Kind == "modeChanged" && (hunk || index)) throw Invalid();
        var ranges = new List<CoverageFanInAddedRange>();
        foreach (var number in additions)
        {
            if (ranges.Count > 0 && (long)ranges[^1].Start + ranges[^1].Count == number) ranges[^1] = ranges[^1] with { Count = checked(ranges[^1].Count + 1) };
            else ranges.Add(new(number, 1));
        }
        return change with { AddedRanges = ranges.AsReadOnly() };
    }

    private static void Header(string field, string? path, string prefix)
    { if (Decode(field) != (path is null ? "/dev/null" : prefix + path)) throw Invalid(); }
    private static string Decode(string value) => global::ForgeTrust.AppSurface.Evidence.GitPatchPathHeader.TryDecode(value, out var path) ? path : throw Invalid();
    private static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw Invalid();

    /// <summary>Rejects portable aliases instead of silently mapping measurements to a differently named file.</summary>
    internal static string Path(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.StartsWith('/') || value.Contains('\\')
            || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            || value.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(['<', '>', ':', '|', '?', '*']) >= 0 || IsDevice(segment))) throw Unsupported();
        _ = new UTF8Encoding(false, true).GetByteCount(value);
        return value;
    }
    private static string Quote(string value)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.All(b => b >= 32 && b < 127 && b != (byte)'"' && b != (byte)'\\')) return value;
        var result = new StringBuilder("\"");
        foreach (var b in bytes)
            if (b >= 32 && b < 127 && b != (byte)'"' && b != (byte)'\\') result.Append((char)b);
            else if (b is (byte)'"' or (byte)'\\') result.Append('\\').Append((char)b);
            else result.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        return result.Append('"').ToString();
    }
    private static bool IsDevice(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }
    private static CoverageFanInEvidenceException Invalid() => CoverageFanInDiffContext.Fail("malformed-diff", "Regenerate the complete canonical patch and raw object metadata from the pinned source; do not edit applicability flags.");
    private static CoverageFanInEvidenceException Unsupported() => CoverageFanInDiffContext.Fail("unsupported-diff", "The patch contains unsupported binary/type/path data; ask the maintainer to resolve or qualify it without skipping files.");
}
