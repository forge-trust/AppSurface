using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs the repository's package manifest, generated-documentation and package-artifact checks.</summary>
internal sealed class PackageChecksProducer : IEvidenceProducer
{
    private const string ProducerId = "package-checks";
    private const int MaxProofBytes = 2 * 1024 * 1024;
    private static readonly string[] AssertionIds =
    [
        "package/manifest@1",
        "package/generated-docs@1",
        "package/archives-and-consumers@1",
    ];

    private readonly RepositoryCheckSettings _settings;
    private readonly Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> _command;

    /// <summary>Creates the producer with a bounded command runner or a deterministic test seam.</summary>
    public PackageChecksProducer(RepositoryCheckSettings settings,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var runner = new EvidenceCommandRunner();
        _command = command ?? runner.RunAsync;
    }

    /// <inheritdoc />
    public string Id => ProducerId;

    /// <summary>Runs the package gate, generated-documentation verification and isolated package validation.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Producer.Id != ProducerId) return Failure("Package-checks declaration mismatch.");

        var output = Path.GetFullPath(_settings.OutputDirectory);
        var packageOutput = Path.Join(output, "package-checks");
        var cache = Path.Join(packageOutput, "nuget-packages");
        var artifacts = Path.Join(packageOutput, "artifacts");
        var manifestPath = Path.Join(artifacts, "package-artifact-manifest.json");
        var reportPath = Path.Join(artifacts, "package-validation-report.md");
        var version = "0.0.0-evidence." + Guid.NewGuid().ToString("N");
        var commands = new List<CommandEvidence>();
        var stage = "settings";
        try
        {
            ValidateSettings(output, packageOutput);
            Directory.CreateDirectory(packageOutput);
            Directory.CreateDirectory(artifacts);
            Directory.CreateDirectory(cache);
            var environment = new Dictionary<string, string>(_settings.CommandEnvironment, StringComparer.Ordinal) { ["NUGET_PACKAGES"] = cache };
            var project = Path.Join(_settings.RepositoryRoot, "tools", "ForgeTrust.AppSurface.PackageIndex", "ForgeTrust.AppSurface.PackageIndex.csproj");

            stage = "gate";
            await RunAsync("gate", ["run", "--project", project, "--configuration", "Release",
                "--property:RestoreConfigFile=" + Path.Join(_settings.RepositoryRoot, "NuGet.package-gate.config"), "--", "gate"], environment, commands, cancellationToken).ConfigureAwait(false);
            stage = "verify";
            await RunAsync("verify", ["run", "--project", project, "--configuration", "Release", "--no-build", "--no-restore", "--", "verify"], environment, commands, cancellationToken).ConfigureAwait(false);
            stage = "verify-packages";
            await RunAsync("verify-packages", ["run", "--project", project, "--configuration", "Release", "--no-build", "--no-restore", "--", "verify-packages",
                "--package-version", version, "--artifacts-output", artifacts, "--artifact-manifest", manifestPath, "--report", reportPath],
                environment, commands, cancellationToken).ConfigureAwait(false);

            var manifest = await ReadBoundedAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var report = await ReadBoundedAsync(reportPath, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest, version);
            ValidateReport(report, version);
            await VerifyArchivesAsync(manifest, artifacts, cancellationToken).ConfigureAwait(false);
            if (context.Artifacts is null) return Failure("Package-checks producer has no artifact writer.");

            await context.Artifacts.WriteAsync("package-manifest", "proof/package-artifact-manifest.json", manifest, cancellationToken).ConfigureAwait(false);
            await context.Artifacts.WriteAsync("package-report", "proof/package-validation-report.md", report, cancellationToken).ConfigureAwait(false);
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                completed = true,
                packageVersion = version,
                artifactManifestSha256 = Hash(manifest),
                validationReportSha256 = Hash(report),
                commands,
                assertions = AssertionIds,
            });
            await context.Artifacts.WriteAsync("transcript", "proof/transcript.json", JsonSerializer.SerializeToUtf8Bytes(commands), cancellationToken).ConfigureAwait(false);
            await context.Artifacts.WriteAsync("result", "proof/result.json", resultBytes, cancellationToken).ConfigureAwait(false);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, AssertionIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var artifactDiagnostic = string.Empty;
            if (context.Artifacts is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { completed = false, stage, commands, failure = exception.GetType().Name });
                artifactDiagnostic = await FailureArtifactDiagnostics.WriteAsync(context.Artifacts,
                    [("result", bytes), ("transcript", JsonSerializer.SerializeToUtf8Bytes(commands))]).ConfigureAwait(false);
            }
            return Failure($"Package checks failed at {stage}." + artifactDiagnostic);
        }
    }

    private async Task RunAsync(string name, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        List<CommandEvidence> commands, CancellationToken cancellationToken)
    {
        var request = new EvidenceCommandRequest(_settings.DotnetPath, arguments, Path.GetFullPath(_settings.RepositoryRoot), TimeSpan.FromMinutes(25))
        { Environment = environment, MaximumOutputBytes = 4 * 1024 * 1024 };
        var result = await _command(request, cancellationToken).ConfigureAwait(false);
        commands.Add(new CommandEvidence(name, result.ExitCode, result.ElapsedMilliseconds, result.OutputTruncated,
            HashText(result.StandardOutput), HashText(result.StandardError)));
        await File.WriteAllTextAsync(Path.Join(_settings.OutputDirectory, "package-checks", name + ".log"),
            result.StandardOutput + "\n" + result.StandardError, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidOperationException("Package command did not complete successfully.");
    }

    private void ValidateSettings(string output, string packageOutput)
    {
        if (!Path.IsPathFullyQualified(_settings.RepositoryRoot) || !Directory.Exists(_settings.RepositoryRoot)
            || !Path.IsPathFullyQualified(_settings.DotnetPath) || !File.Exists(_settings.DotnetPath)
            || !Path.IsPathFullyQualified(output) || !Path.IsPathFullyQualified(_settings.OutputDirectory)
            || !IsContained(output, packageOutput)) throw new InvalidOperationException("Invalid package-check settings.");
        foreach (var forbidden in new[] { "bin", "obj", ".git" })
            if (IsContained(Path.GetFullPath(_settings.RepositoryRoot), output) &&
                Path.GetRelativePath(_settings.RepositoryRoot, output).Split(Path.DirectorySeparatorChar).Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Evidence output must be outside build and source-control directories.");
        if (Directory.Exists(packageOutput) || File.Exists(packageOutput)) throw new InvalidOperationException("Package-check output must be fresh.");
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxProofBytes) throw new InvalidDataException("Package proof missing or outside byte bounds.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateManifest(byte[] bytes, string version)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("package_version", out var packageVersion)
            || packageVersion.GetString() != version || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new InvalidDataException("Package artifact manifest did not validate expected version and entries.");
    }

    private static void ValidateReport(byte[] bytes, string version)
    {
        var report = System.Text.Encoding.UTF8.GetString(bytes);
        if (!report.Contains(version, StringComparison.Ordinal) || !report.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || report.Contains("validation failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package validation report did not describe a successful versioned run.");
    }

    private static async Task VerifyArchivesAsync(byte[] bytes, string artifacts, CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(bytes);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.RootElement.GetProperty("entries").EnumerateArray())
        {
            var name = entry.GetProperty("artifact_file_name").GetString();
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('\\')
                || !name.EndsWith(".nupkg", StringComparison.Ordinal) || !seen.Add(name))
                throw new InvalidDataException("Package artifact inventory is invalid.");
            var path = Path.Join(artifacts, name);
            if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Package artifacts must not be linked.");
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (hash != entry.GetProperty("sha512").GetString()) throw new InvalidDataException("Package artifact digest differs from its validation manifest.");
        }
        if (Directory.EnumerateFiles(artifacts, "*.nupkg").Count() != seen.Count)
            throw new InvalidDataException("Package archive inventory differs from its validation manifest.");
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashText(string value) => Hash(System.Text.Encoding.UTF8.GetBytes(value));
    private static EvidenceProducerResult Failure(string diagnostic) => new(ProducerId, EvidenceProducerOutcome.Failed, [], diagnostic);
    private sealed record CommandEvidence(string Stage, int ExitCode, long ElapsedMilliseconds, bool OutputTruncated, string StdoutSha256, string StderrSha256);
}
