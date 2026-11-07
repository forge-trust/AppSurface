using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Native v2 fan-in orchestration. Owns invocation custody and marker-last publication.</summary>
internal static partial class CoverageFanInEngine
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };
    private const string ManifestName = "manifest.json";
    private const string CoverageName = "coverage.cobertura.xml";
    private const string PrimaryName = "primary-paths.cobertura.xml";
    private const string ReceiptName = "verification.json";
    private const string PrimaryGateName = "primary-gate.json";
    private const string ComposedGateName = "composed-gate.json";

    /// <summary>Returns safe typed failures and truthful effects; never catches process-fatal exceptions.</summary>
    internal static async ValueTask<CoverageFanInEngineResult> ExecuteAsync(string operation, CoverageFanInEngineRequest request,
        CoverageTextWriters writers, CancellationToken callerToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new InvocationState(operation, id, request.Expected.Digest, request.HistoricalReplay);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(operation == "collect" ? Math.Max(1, request.DeadlineSeconds)
            : request.Expected.Limits.GetProperty("operationSeconds").GetInt32()));
        try
        {
            ValidateRequest(operation, request);
            if (operation == "collect" && request.NoBuild)
                return state.Failure("Rejected", "ASCOV301", "noBuild",
                    "Observed collection requires a rebuild; omit noBuild or set it to false.");
            deadline.Token.ThrowIfCancellationRequested();
            await VerifySourceAsync(request, deadline.Token, collecting: operation == "collect").ConfigureAwait(false);
            if (operation == "collect") return await CollectAsync(request, writers, state, deadline.Token).ConfigureAwait(false);
            using var io = new CoverageFanInIO(request.Expected.Bounds, deadline.Token);
            var inputs = InputDirectories(operation, request);
            if (operation == "capture")
                foreach (var input in inputs) RejectOverlap([input], request.OutputDirectory);
            else RejectOverlap(inputs, request.OutputDirectory);
            io.Acquire(inputs.Select(path => (path, false)), requireCoordination: operation != "capture");
            Dictionary<string, byte[]> files;
            if (operation == "capture") files = Capture(request, io, state);
            else
            {
                var bundles = LoadSet(request, io);
                state.Observation = bundles[0].Manifest.GetProperty("observation").GetString();
                state.PrimaryImported = IsImported(bundles[0]);
                state.Imported = state.PrimaryImported || bundles.Skip(1).Any(IsImported);
                bool IsImported(CoverageFanInBundle bundle) =>
                    Parse(io.Read(Path.Join(bundle.Directory, CoverageFanInIO.CompletionName)))
                        .GetProperty("materialization").GetString() == "imported";
                if (operation == "compose") files = Compose(request, io, bundles);
                else if (operation == "accept")
                {
                    var candidate = await AcceptAsync(request, io, bundles, deadline.Token).ConfigureAwait(false);
                    if (!candidate.Passed)
                    {
                        io.VerifyBindings();
                        io.AdmitOutput(candidate.Files.Values);
                        await VerifySourceAsync(request, deadline.Token).ConfigureAwait(false);
                        await PublishAsync(request, "rejected-gates", candidate.Files, state, deadline.Token, io).ConfigureAwait(false);
                        return state.Failure("Rejected", "ASCOV307", "gate",
                            "Inspect retained primary and composed gate reports; add honest tests without changing the declared thresholds or measurement scope.");
                    }
                    files = candidate.Files;
                }
                else if (operation == "verify")
                {
                    await VerifyAcceptedAsync(request, io, bundles, deadline.Token).ConfigureAwait(false);
                    await VerifySourceAsync(request, deadline.Token).ConfigureAwait(false);
                    io.VerifyBindings();
                    state.Verified = true;
                    state.Patch = PatchOutcome(request);
                    return state.Result("Succeeded");
                }
                else throw new ArgumentException("Unknown fan-in operation.");
            }
            io.VerifyBindings();
            io.AdmitOutput(files.Values);
            await VerifySourceAsync(request, deadline.Token).ConfigureAwait(false);
            await PublishAsync(request, operation == "capture" ? "capture" : operation == "compose" ? "composition" : "acceptance",
                files, state, deadline.Token, io).ConfigureAwait(false);
            if (operation == "accept") { state.Accepted = true; state.Verified = true; state.Patch = PatchOutcome(request); }
            return state.Result("Succeeded");
        }
        catch (OperationCanceledException)
        {
            if (state.Committed) return state.Result("Succeeded");
            return state.Failure(callerToken.IsCancellationRequested ? "Cancelled" : "TimedOut", "ASCOV306", "deadline",
                "Reconcile retained partial output; use a fresh destination and an explicit adequate deadline.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException
            or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or FormatException or OverflowException or XmlException or KeyNotFoundException or CoverageExecutionException)
        {
            var code = exception is ArgumentException ? "ASCOV301" : exception is CoverageExecutionException { ExitCode: 124 } ? "ASCOV306" : "ASCOV302";
            var outcome = exception is CoverageExecutionException { ExitCode: 124 } ? "TimedOut" : state.EffectsStarted ? "Incomplete" : "Rejected";
            return state.Failure(outcome, code, operation,
                "Check the declared policy and retained inputs; preserve unknown output and retry once into a fresh destination after correcting the cause.");
        }
    }

    private sealed class InvocationState(string operation, string id, string digest, bool historical)
    {
        internal bool Created, EffectsStarted, Committed, Imported, PrimaryImported, Accepted, Verified;
        internal string? Observation;
        internal string Patch = "NotEvaluated";
        internal string? Generation;
        internal readonly List<CoverageFanInEngineArtifact> Artifacts = [];
        internal CoverageFanInEngineResult Result(string outcome, IReadOnlyList<CoverageFanInEngineDiagnostic>? diagnostics = null) =>
            new(operation, outcome, id, digest, Created, Committed, !EffectsStarted || Committed, Imported, Generation,
                Artifacts.ToArray(), diagnostics ?? [], Accepted, Verified, Patch, Observation, historical,
                PrimaryImported);
        internal CoverageFanInEngineResult Failure(string outcome, string code, string field, string recovery) =>
            Result(outcome, [new(code, operation, field, recovery)]);
    }

    private static void ValidateRequest(string operation, CoverageFanInEngineRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.RepositoryRoot)) throw new ArgumentException("Explicit repository required.");
        if (operation != "verify" && string.IsNullOrWhiteSpace(r.OutputDirectory)) throw new ArgumentException("Explicit output required.");
        if (operation == "capture")
        {
            _ = Producer(r);
            if (r.ImportDirectory is not null)
            {
                if (r.CoveragePath is not null || r.TimingsPath is not null || r.RawCoveragePath is not null || r.RawCoveragePaths is not null
                    || string.IsNullOrWhiteSpace(r.OriginalSourceRoot)) throw new ArgumentException("Import selectors conflict.");
            }
            else if ((r.RawCoveragePaths is null ? string.IsNullOrWhiteSpace(r.CoveragePath) : r.CoveragePath is not null)
                || (r.ProducerId == "primary"
                ? r.TimingsPath is null || r.RawCoveragePath is not null || r.RawCoveragePaths is not null
                : r.RawCoveragePaths is not null
                    ? r.RawCoveragePath is not null || r.TimingsPath is null
                    : r.RawCoveragePath is null || r.TimingsPath is not null))
                throw new ArgumentException("Producer inputs incomplete.");
        }
        if (operation is "compose" or "accept" or "verify")
        {
            if (string.IsNullOrWhiteSpace(r.PrimaryCaptureDirectory) || r.SupplementaryCaptureDirectories is null
                || r.SupplementaryCaptureDirectories.Count != r.Expected.Supplements.Length)
                throw new ArgumentException("Explicit capture inventory differs.");
        }
        if (operation is "accept" or "verify" && string.IsNullOrWhiteSpace(r.ComposedOutputDirectory))
            throw new ArgumentException("Explicit composition required.");
        if (operation == "verify" && string.IsNullOrWhiteSpace(r.AcceptedOutputDirectory))
            throw new ArgumentException("Explicit accepted generation required.");
        if (operation == "collect" && (r.DeadlineSeconds < 1 || r.DeadlineSeconds > 86400
            || r.Parallelism < 1 || string.IsNullOrWhiteSpace(r.CollectionDirectory)))
            throw new ArgumentException("Explicit collection deadline and destination required.");
    }

    private static JsonElement? Producer(CoverageFanInEngineRequest r) => r.ProducerId == "primary" ? null :
        r.Expected.Supplements.SingleOrDefault(x => x.GetProperty("id").GetString() == r.ProducerId) is var value
            && value.ValueKind != JsonValueKind.Undefined ? value : throw new ArgumentException("Unknown producer.");

    private static List<string> InputDirectories(string operation, CoverageFanInEngineRequest r)
    {
        if (operation == "capture") return r.ImportDirectory is not null ? [Full(r.ImportDirectory)] :
            new[] { r.CoveragePath, r.TimingsPath, r.RawCoveragePath }.Concat(r.RawCoveragePaths?.Values ?? []).Where(x => x is not null)
                .Select(x => Path.GetDirectoryName(Full(x!))!).Distinct(StringComparer.Ordinal).ToList();
        var paths = new List<string> { Full(r.PrimaryCaptureDirectory!) };
        paths.AddRange(r.SupplementaryCaptureDirectories!.Select(Full));
        if (r.ComposedOutputDirectory is not null) paths.Add(Full(r.ComposedOutputDirectory));
        if (r.AcceptedOutputDirectory is not null) paths.Add(Full(r.AcceptedOutputDirectory));
        return paths;
    }
    private static string Full(string path) => string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("Explicit path required.")
        : CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(path));
    private static void RejectOverlap(IReadOnlyList<string> inputs, string? output)
    {
        var paths = output is null ? inputs.ToArray() : inputs.Append(Full(output)).ToArray();
        for (var i = 0; i < paths.Length; i++) for (var j = 0; j < i; j++)
        {
            var a = paths[i].TrimEnd(Path.DirectorySeparatorChar); var b = paths[j].TrimEnd(Path.DirectorySeparatorChar);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Fan-in roots overlap.");
        }
    }

    private static async Task VerifySourceAsync(CoverageFanInEngineRequest r, CancellationToken token, bool collecting = false)
    {
        var root = Full(r.RepositoryRoot);
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, false, false)
            ?? throw new IOException("Source context unavailable.");
        if (Full(await IdentityAsync("git", ["rev-parse", "--show-toplevel"], r, token).ConfigureAwait(false)) != root
            || await IdentityAsync("git", ["rev-parse", "HEAD"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("commit").GetString()
            || await IdentityAsync("git", ["rev-parse", "HEAD^{tree}"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("tree").GetString()
            || await IdentityAsync("dotnet", ["--version"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("sdk").GetString()
            || (await IdentityAsync("git", ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"], r, token).ConfigureAwait(false)).Length != 0)
            throw new InvalidDataException("Source, SDK or clean state differs from trusted expectations.");
        var projects = new HashSet<string>(r.Expected.TestProjects, StringComparer.Ordinal);
        foreach (var producer in r.Expected.Supplements)
        {
            if (producer.TryGetProperty("testProjects", out var inventory) && inventory.ValueKind == JsonValueKind.Array)
                projects.UnionWith(inventory.EnumerateArray().Select(project => project.GetString()!));
        }
        await VerifySourceIndexesAsync(root, projects, r, token, collecting).ConfigureAwait(false);
        foreach (var project in projects)
        {
            var path = Path.Join(root, project);
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(path)!, false, false)
                ?? throw new IOException("Declared project unavailable.");
            using var file = parent.OpenArtifactFile(Path.GetFileName(path), false, false);
        }
        directory.VerifyArtifactBinding();
    }

    // Each clean checkout's index agrees with its pinned HEAD. Require regular stage-zero entries
    // for the declared projects; status alone does not report an ignored project that is absent from Git.
    private static async Task VerifySourceIndexesAsync(string root, IReadOnlySet<string> projects,
        CoverageFanInEngineRequest r, CancellationToken token, bool collecting)
    {
        const int maximumCheckouts = 32;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(r.Expected.Limits.GetProperty("identitySeconds").GetInt32()));
        var unseenProjects = new HashSet<string>(projects, StringComparer.Ordinal);
        var projectDirectories = collecting ? new HashSet<string>(StringComparer.Ordinal) : null;
        var ignoredInventories = new List<(string Prefix, string Entries)>();
        var pending = new Queue<(string Path, string Prefix, string? Commit)>();
        pending.Enqueue((root, string.Empty, null));
        var checkouts = 1;
        while (pending.TryDequeue(out var checkout))
        {
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(checkout.Path, false, false)
                ?? throw new IOException("Pinned source checkout is unavailable.");
            if (checkout.Commit is not null
                && (Full(await IdentityAsync("git", ["rev-parse", "--show-toplevel"], r, deadline.Token, checkout.Path).ConfigureAwait(false)) != checkout.Path
                    || await IdentityAsync("git", ["rev-parse", "HEAD"], r, deadline.Token, checkout.Path).ConfigureAwait(false) != checkout.Commit
                    || (await IdentityAsync("git", ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"],
                        r, deadline.Token, checkout.Path).ConfigureAwait(false)).Length != 0))
                throw new InvalidDataException("Pinned submodule source or clean state differs.");
            var entries = await IdentityAsync("git", ["ls-files", "--stage", "-v", "-z"], r, deadline.Token, checkout.Path).ConfigureAwait(false);
            foreach (var (path, commit) in ReadSourceIndex(entries, checkout.Prefix, unseenProjects, projectDirectories))
            {
                if (++checkouts > maximumCheckouts)
                    throw new InvalidDataException("Source checkout inventory exceeds the limit.");
                var child = Full(Path.Join(checkout.Path, path));
                if (child == checkout.Path || !child.StartsWith(Path.TrimEndingDirectorySeparator(checkout.Path) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                    throw new InvalidDataException("Pinned submodule path escapes its checkout.");
                pending.Enqueue((child, checkout.Prefix + path + "/", commit));
            }
            if (collecting)
            {
                var ignored = await IdentityAsync("git", ["ls-files", "--others", "--ignored", "--exclude-standard",
                    "--directory", "--no-empty-directory", "-z"], r, deadline.Token, checkout.Path).ConfigureAwait(false);
                ignoredInventories.Add((checkout.Prefix, ignored));
            }
            directory.VerifyArtifactBinding();
        }
        if (unseenProjects.Count != 0)
            throw new InvalidDataException("Declared test projects must be tracked regular files in the pinned source.");
        if (projectDirectories is not null)
            ValidateCollectionIgnoredFiles(ignoredInventories, projectDirectories);
    }

    // A default SDK glob can see nested projects' outputs, including across a Git submodule.
    // Allow ordinary output only for outermost project directories, preserving exact Git path spelling.
    private static void ValidateCollectionIgnoredFiles(List<(string Prefix, string Entries)> inventories,
        HashSet<string> projectDirectories)
    {
        var exact = projectDirectories.GetAlternateLookup<ReadOnlySpan<char>>();
        var caseFolded = new HashSet<string>(projectDirectories, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (prefix, entries) in inventories)
        {
            foreach (var entry in entries.AsSpan().Split('\0'))
            {
                var path = entries.AsSpan()[entry];
                if (path.IsEmpty) continue;
                if (!(path.SequenceEqual("bin/") || path.SequenceEqual("obj/")
                    || path.EndsWith("/bin/", StringComparison.Ordinal) || path.EndsWith("/obj/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Ignored files outside conventional build output can change observed coverage inputs.");
                var project = string.Concat(prefix, path[..^4]).AsSpan();
                if (!exact.Contains(project))
                    throw new InvalidDataException("Ignored output does not belong to a tracked project directory.");
                while (!project.IsEmpty)
                {
                    project = project[..(project[..^1].LastIndexOf('/') + 1)];
                    if (caseFolded.Contains(project))
                        throw new InvalidDataException("Ignored nested output can be a build input for an ancestor project.");
                }
            }
        }
    }

    // Git lowers the tag for assume-unchanged; S means skip-worktree. NUL separates filenames.
    private static List<(string Path, string Commit)> ReadSourceIndex(string entries, string prefix,
        HashSet<string> unseenProjects, HashSet<string>? projectDirectories)
    {
        var links = new List<(string, string)>();
        var projectLookup = unseenProjects.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var entry in entries.AsSpan().Split('\0'))
        {
            var record = entries.AsSpan()[entry];
            if (record.IsEmpty) continue;
            if (record[0] == 'S' || char.IsAsciiLetterLower(record[0]))
                throw new InvalidDataException("Source index suppresses tracked changes.");
            var gitlink = record.StartsWith("H 160000 ", StringComparison.Ordinal);
            if (!gitlink && !record.StartsWith("H 100644 ", StringComparison.Ordinal)
                && !record.StartsWith("H 100755 ", StringComparison.Ordinal)) continue;
            var tab = record.IndexOf('\t');
            if (tab <= 11 || tab == record.Length - 1 || !record[..tab].EndsWith(" 0", StringComparison.Ordinal))
                throw new InvalidDataException("Pinned source index entry is invalid.");
            var path = record[(tab + 1)..];
            if (gitlink)
            {
                links.Add((path.ToString(), record[9..(tab - 2)].ToString()));
                continue;
            }
            if (prefix.Length == 0) projectLookup.Remove(path);
            else if (unseenProjects.Count != 0) unseenProjects.Remove(string.Concat(prefix, path));
            if (projectDirectories is not null && (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)))
                projectDirectories.Add(string.Concat(prefix, path[..(path.LastIndexOf('/') + 1)]));
        }
        return links;
    }
    private static async Task<string> IdentityAsync(string file, string[] arguments, CoverageFanInEngineRequest r,
        CancellationToken token, string? workingDirectory = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(r.Expected.Limits.GetProperty("identitySeconds").GetInt32()));
        var runner = r.IdentityRunner ?? new CliWrapCoverageRunProcessRunner();
        // Inspect the pinned objects without replacement refs or a repository-configured fsmonitor hook.
        var safeArguments = file == "git" ? new[] { "--no-replace-objects", "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray() : arguments;
        var result = await runner.RunAsync(new(file, safeArguments, workingDirectory ?? Full(r.RepositoryRoot), null, null,
            CoverageRunProcessLease.Detached()), deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidDataException("Identity command failed.");
        var output = result.StandardOutput ?? result.Output;
        if (file != "git" || !arguments.Contains("-z", StringComparer.Ordinal)) return output.TrimEnd('\r', '\n');
        if (output.Contains('\uFFFD')) throw new InvalidDataException("Git path output contains invalid or ambiguous UTF-8.");
        return output;
    }

    private static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static JsonElement Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        CheckSyntax(doc.RootElement); return doc.RootElement.Clone();
    }
    private static void CheckSyntax(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate evidence field."); CheckSyntax(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var x in value.EnumerateArray()) CheckSyntax(x);
    }
    private static bool Same(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);
    private static string PatchOutcome(CoverageFanInEngineRequest r) => r.Expected.Gate.GetProperty("mode").GetString() == "aggregateOnly"
        ? "PatchNotRequested" : "PatchAccepted";
}
