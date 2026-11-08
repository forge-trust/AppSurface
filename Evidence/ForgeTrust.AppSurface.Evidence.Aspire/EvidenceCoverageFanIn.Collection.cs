using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Explicit inputs used to snapshot one successful coverage producer run.</summary>
/// <param name="RepositoryRoot">Absolute source checkout used by the producer.</param>
/// <param name="OutputDirectory">Fresh destination for the capture bundle.</param>
/// <param name="ReportPath">Normalized Cobertura report produced by the runner.</param>
/// <param name="RawReportPath">Original collector report for native captures; null for solution captures.</param>
/// <param name="TimingsPath">Full solution timing inventory for solution captures; null for native captures.</param>
/// <param name="Scope">Either <c>solution</c> or <c>native</c>.</param>
/// <param name="Commit">Trusted expected checkout commit.</param>
/// <param name="Tree">Trusted expected checkout tree.</param>
/// <param name="Sdk">Trusted .NET SDK identity.</param>
/// <param name="Configuration">Build configuration, either Debug or Release.</param>
/// <param name="Run">Workflow provenance values, normally repository, run id and attempt.</param>
/// <param name="Platform">Native runner platform; solution captures use the current platform identity.</param>
/// <param name="ExpectedTestProjects">Complete expected solution test-project inventory.</param>
/// <remarks>The caller supplies source identity and workflow provenance through a trusted channel. Capture is a local snapshot, not a signature.</remarks>
public sealed record EvidenceCoverageCaptureRequest(
    string RepositoryRoot,
    string OutputDirectory,
    string ReportPath,
    string? RawReportPath,
    string? TimingsPath,
    string Scope,
    string Commit,
    string Tree,
    string Sdk,
    string Configuration,
    IReadOnlyDictionary<string, string?> Run,
    string Platform,
    ISet<string> ExpectedTestProjects);

/// <summary>Native capture and coverage fan-in composition operations.</summary>
public static partial class EvidenceCoverageFanIn
{
    /// <summary>Validates and snapshots a producer report into a fresh manifest-backed capture directory.</summary>
    /// <remarks>Native captures require the exact <see cref="NativeFilter"/> selection and the original collector XML. Solution captures require a complete successful timing inventory.</remarks>
    public static async ValueTask CaptureAsync(EvidenceCoverageCaptureRequest request, CancellationToken cancellationToken = default)
    {
        try { await CaptureCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Reject("Malformed coverage capture evidence.", exception); }
    }

    private static async ValueTask CaptureCoreAsync(EvidenceCoverageCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCaptureRequest(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        var output = Path.GetFullPath(request.OutputDirectory);
        var sources = new[] { request.ReportPath, request.RawReportPath, request.TimingsPath }
            .Where(x => x is not null).Select(x => Path.GetFullPath(x!)).ToArray();
        if (sources.Any(x => IsWithin(x, output))) throw new ArgumentException("Capture destination must not contain producer inputs.");
        if (Directory.Exists(output) || File.Exists(output)) throw Reject("Evidence destination must be fresh.");

        var normalized = await ReadXmlAsync(request.ReportPath, cancellationToken).ConfigureAwait(false);
        JsonElement? timings = null;
        if (request.Scope == "solution")
        {
            timings = await ReadJsonAsync(request.TimingsPath!, cancellationToken).ConfigureAwait(false);
            var verificationRequest = new EvidenceCoverageFanInRequest(output, [], output, root,
                Path.Combine(root, "ForgeTrust.AppSurface.slnx"), request.ExpectedTestProjects,
                request.Commit, request.Tree, request.Sdk, request.Configuration, request.Run,
                output, output, "capture");
            VerifySolution(timings.Value, verificationRequest);
        }
        else
        {
            var raw = await ReadXmlAsync(request.RawReportPath!, cancellationToken).ConfigureAwait(false);
            VerifyNormalization(raw, normalized, root);
        }

        Directory.CreateDirectory(output);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        await CopyAndHashAsync(request.ReportPath, Path.Combine(output, "coverage.cobertura.xml"), MaxXml, files, cancellationToken).ConfigureAwait(false);
        if (request.Scope == "native")
            await CopyAndHashAsync(request.RawReportPath!, Path.Combine(output, "collector.cobertura.xml"), MaxXml, files, cancellationToken).ConfigureAwait(false);
        else
            await CopyAndHashAsync(request.TimingsPath!, Path.Combine(output, "timings.json"), MaxJson, files, cancellationToken).ConfigureAwait(false);

        var manifest = new
        {
            schemaVersion = Schema,
            commit = request.Commit,
            tree = request.Tree,
            sdk = request.Sdk,
            configuration = request.Configuration,
            sourceRoot = root,
            platform = request.Platform,
            policy = ExpectedPolicy,
            scope = request.Scope,
            filter = request.Scope == "native" ? NativeFilter : null,
            run = request.Run,
            files,
        };
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates four capture bundles and writes the composed report, primary-path report, and verification receipt.</summary>
    /// <remarks>Gate files are deliberately not required here because CI generates them from these outputs before calling <see cref="VerifyAsync"/>. Fresh output is required; the final verifier must run after both gate reports exist.</remarks>
    public static async ValueTask ComposeAsync(EvidenceCoverageFanInRequest request, CancellationToken cancellationToken = default)
    {
        try { await ComposeCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Reject("Malformed coverage composition evidence.", exception); }
    }

    private static async ValueTask ComposeCoreAsync(EvidenceCoverageFanInRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.NativeCaptureDirectories.Count != 3) throw Reject("Exactly three native evidence directories are required.");
        var primary = await LoadAsync(request, request.PrimaryCaptureDirectory, "solution", cancellationToken).ConfigureAwait(false);
        var natives = new List<Loaded>(3);
        foreach (var directory in request.NativeCaptureDirectories)
            natives.Add(await LoadAsync(request, directory, "native", cancellationToken).ConfigureAwait(false));
        if (!natives.Select(x => x.Manifest.GetProperty("platform").GetString()).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(Platforms.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            throw Reject("Exactly one Linux, Windows and Darwin native input is required.");
        if (!JsonEquals(primary.Manifest.GetProperty("run"), request.Run)
            || natives.Any(x => !JsonEquals(x.Manifest.GetProperty("run"), request.Run))) throw Reject("Workflow provenance differs.");
        if (request.Run.Count == 0 || request.Run.Any(x => string.IsNullOrWhiteSpace(x.Value))) throw Reject("Workflow provenance is required.");
        VerifySolution(primary.Timings!.Value, request);

        var all = new[] { primary }.Concat(natives.OrderBy(x => x.Manifest.GetProperty("platform").GetString(), StringComparer.Ordinal)).ToArray();
        var composition = Compose(all);
        var outputDirectory = Path.GetFullPath(request.ComposedOutputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw Reject("Fan-in destination must be fresh.");
        Directory.CreateDirectory(outputDirectory);
        var composedPath = Path.Combine(outputDirectory, "coverage.cobertura.xml");
        var primaryPath = Path.Combine(outputDirectory, "primary-paths.cobertura.xml");
        await WriteXmlAsync(composedPath, composition.Document, cancellationToken).ConfigureAwait(false);
        var primaryPaths = new XDocument(primary.Document);
        foreach (var cls in primaryPaths.Descendants("class"))
            cls.SetAttributeValue("filename", Relative(cls.Attribute("filename")?.Value ?? "", primary.Manifest.GetProperty("sourceRoot").GetString()!));
        await WriteXmlAsync(primaryPath, primaryPaths, cancellationToken).ConfigureAwait(false);

        var classCounts = all.Select(x => x.Document.Descendants("class").Elements("lines").Elements("line").Count()).ToArray();
        var methodCounts = all.Select(x => x.Document.Descendants("method").Elements("lines").Elements("line").Count()).ToArray();
        var inputs = all.Select(x => new { manifestSha256 = x.ManifestHash, manifest = x.Manifest }).ToArray();
        var receipt = new
        {
            commit = request.Commit,
            tree = request.Tree,
            sdk = request.Sdk,
            configuration = request.Configuration,
            policy = ExpectedPolicy,
            run = request.Run,
            inventory = new
            {
                inputClassLines = classCounts,
                retainedClassLines = composition.ClassLines,
                inputMethodLines = methodCounts,
                retainedMethodLines = composition.MethodLines,
                linesCovered = composition.LinesCovered,
                branchesValid = composition.BranchesValid,
                branchesCovered = composition.BranchesCovered,
            },
            inputs,
            outputSha256 = await Sha256Async(composedPath, MaxXml, cancellationToken).ConfigureAwait(false),
            primaryPathsSha256 = await Sha256Async(primaryPath, MaxXml, cancellationToken).ConfigureAwait(false),
        };
        await WriteJsonAsync(Path.Combine(outputDirectory, "verification.json"), receipt, cancellationToken).ConfigureAwait(false);
        var parsedReceipt = await ReadJsonAsync(Path.Combine(outputDirectory, "verification.json"), cancellationToken).ConfigureAwait(false);
        await VerifyReceiptAsync(parsedReceipt, request, all, outputDirectory, composition, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCaptureRequest(EvidenceCoverageCaptureRequest r)
    {
        foreach (var value in new[] { r.RepositoryRoot, r.OutputDirectory, r.ReportPath, r.Commit, r.Tree, r.Sdk, r.Configuration, r.Platform })
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Coverage capture inputs must be explicit.");
        if (r.Scope is not ("solution" or "native")) throw new ArgumentException("Capture scope must be solution or native.");
        if (r.Configuration is not ("Debug" or "Release")) throw new ArgumentException("Configuration must be Debug or Release.");
        if (r.ExpectedTestProjects is null || r.ExpectedTestProjects.Count == 0) throw new ArgumentException("The trusted solution test-project inventory is required.");
        if (r.Run is null || r.Run.Count == 0 || r.Run.Any(x => string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Value)))
            throw new ArgumentException("Workflow provenance is required.");
        if (r.Scope == "native" && (string.IsNullOrWhiteSpace(r.RawReportPath) || r.TimingsPath is not null))
            throw new ArgumentException("Native captures require the original collector report and no solution timings.");
        if (r.Scope == "solution" && (string.IsNullOrWhiteSpace(r.TimingsPath) || r.RawReportPath is not null))
            throw new ArgumentException("Solution captures require timings and no native collector report.");
        if (r.Scope == "native" && r.Platform is not ("Linux" or "Windows" or "Darwin")) throw new ArgumentException("Native platform must be Linux, Windows or Darwin.");
        if (r.Scope == "solution")
        {
            var root = Path.GetFullPath(r.RepositoryRoot);
            var solution = Path.Combine(root, "ForgeTrust.AppSurface.slnx");
            if (!File.Exists(solution)) throw new ArgumentException("The repository solution inventory is unavailable.");
            var actual = XDocument.Load(solution).Descendants("Project").Select(x => x.Attribute("Path")?.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x) && (x.EndsWith(".Tests.csproj", StringComparison.Ordinal) || x.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
                .Select(x => x!.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(r.ExpectedTestProjects)) throw new ArgumentException("The trusted solution test-project inventory differs from the actual .slnx inventory.");
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static async ValueTask CopyAndHashAsync(string source, string destination, long limit, Dictionary<string, string> files, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(source, limit, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);
        files[Path.GetFileName(destination)] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async ValueTask WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    private static async ValueTask WriteXmlAsync(string path, XDocument document, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false });
        await document.WriteToAsync(writer, ct).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
