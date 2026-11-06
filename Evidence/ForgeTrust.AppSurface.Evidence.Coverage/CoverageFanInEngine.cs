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
            await VerifySourceAsync(request, deadline.Token).ConfigureAwait(false);
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

    private static async Task VerifySourceAsync(CoverageFanInEngineRequest r, CancellationToken token)
    {
        var root = Full(r.RepositoryRoot);
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, false, false)
            ?? throw new IOException("Source context unavailable.");
        if (Full(await IdentityAsync("git", ["rev-parse", "--show-toplevel"], r, token).ConfigureAwait(false)) != root
            || await IdentityAsync("git", ["rev-parse", "HEAD"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("commit").GetString()
            || await IdentityAsync("git", ["rev-parse", "HEAD^{tree}"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("tree").GetString()
            || await IdentityAsync("dotnet", ["--version"], r, token).ConfigureAwait(false) != r.Expected.Source.GetProperty("sdk").GetString()
            || HasSuppressedIndexEntries(await IdentityAsync("git", ["ls-files", "--recurse-submodules", "-v", "-z"], r, token).ConfigureAwait(false))
            || (await IdentityAsync("git", ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"], r, token).ConfigureAwait(false)).Length != 0)
            throw new InvalidDataException("Source, SDK or clean state differs from trusted expectations.");
        foreach (var project in r.Expected.TestProjects)
        {
            var path = Path.Join(root, project);
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(path)!, false, false)
                ?? throw new IOException("Declared project unavailable.");
            using var file = parent.OpenArtifactFile(Path.GetFileName(path), false, false);
        }
        directory.VerifyArtifactBinding();
    }

    // Git's -v lowers the tag for assume-unchanged; -t's S tag means skip-worktree.
    // NUL delimiting prevents a filename from introducing or hiding a record tag.
    private static bool HasSuppressedIndexEntries(string entries)
    {
        foreach (var entry in entries.AsSpan().Split('\0'))
        {
            var record = entries.AsSpan()[entry];
            if (!record.IsEmpty && (record[0] == 'S' || char.IsAsciiLetterLower(record[0])))
                return true;
        }
        return false;
    }
    private static async Task<string> IdentityAsync(string file, string[] arguments, CoverageFanInEngineRequest r, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(r.Expected.Limits.GetProperty("identitySeconds").GetInt32()));
        var runner = r.IdentityRunner ?? new CliWrapCoverageRunProcessRunner();
        // Identity validation must not execute a repository-configured fsmonitor hook.
        var safeArguments = file == "git" ? new[] { "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray() : arguments;
        var result = await runner.RunAsync(new(file, safeArguments, Full(r.RepositoryRoot), null, null,
            CoverageRunProcessLease.Detached()), deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidDataException("Identity command failed.");
        return (result.StandardOutput ?? result.Output).Trim();
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
