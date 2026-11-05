using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;

// This adapter supplies only AppSurface's checked-in policy and exact paths. All capture,
// composition, ownership, gate and verification behavior belongs to installed native commands.
return args.Length > 0 && args[0] == "--package-proof"
    ? await InstalledPackageProof.RunAsync(args[1..])
    : await CoverageDogfood.RunAsync(args);

internal static class CoverageDogfood
{
    internal const string NativeFilter = "FullyQualifiedName~CoverageRunArtifactReaderTests|FullyQualifiedName~CoverageRunOutputGuardSecurityTests|FullyQualifiedName~CoverageGateTests|FullyQualifiedName~CoverageArtifact|FullyQualifiedName~CoverageStorageCommand|FullyQualifiedName~CoverageFanIn";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("AppSurface policy adapter: --operation native|fanin --repository <clean-root> --cli <installed-appsurface> --output <fresh-parent> --configuration Debug|Release [--native-platform Linux|Windows|Darwin --report <normalized> --raw <collector>] [--primary <report> --timings <json> --native-inputs <directory> --diff-base <ref>] --run-scope <repository> --run-id <id> --attempt <positive-integer>");
            return 0;
        }
        try
        {
            if (args.Length % 2 != 0) throw new ArgumentException();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new[] { "operation", "repository", "cli", "output", "configuration", "native-platform", "report", "raw", "primary", "timings", "native-inputs", "diff-base", "run-scope", "run-id", "attempt" }.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..]) || !options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException();
            string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException();
            var root = Path.GetFullPath(Required("repository"));
            var cli = Path.GetFullPath(Required("cli"));
            var output = Path.GetFullPath(Required("output"));
            var configuration = Required("configuration");
            if (configuration is not ("Debug" or "Release") || Directory.Exists(output) || File.Exists(output)) throw new ArgumentException();
            var solution = XDocument.Load(Path.Join(root, "ForgeTrust.AppSurface.slnx"));
            // Repository-specific inventory is intentionally supplied by this adapter, never inferred by the reusable API.
            var projects = solution.Descendants("Project").Select(x => x.Attribute("Path")?.Value)
                .Where(x => x is not null && (x.EndsWith(".Tests.csproj", StringComparison.Ordinal) || x.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
                .Select(x => x!.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var nativeProjects = new[] { "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj" };
            var policy = new CoverageFanInPolicy(new(await Command(root, "git", ["rev-parse", "HEAD"]),
                await Command(root, "git", ["rev-parse", "HEAD^{tree}"]), await Command(root, "dotnet", ["--version"]), configuration),
                new("github-actions", Required("run-scope"), Required("run-id"), int.Parse(Required("attempt"), System.Globalization.CultureInfo.InvariantCulture)),
                projects, new[] { "Linux", "Windows", "Darwin" }.Select(x => new CoverageSupplement(x.ToLowerInvariant(), x, NativeFilter, nativeProjects)).ToArray(),
                options.TryGetValue("diff-base", out var diff) && !string.IsNullOrWhiteSpace(diff)
                    ? new(CoverageGateMode.AggregateAndPatch, DiffBase: diff) : new(CoverageGateMode.AggregateOnly),
                Limits: new CoverageOperationLimits(TotalRecords: 4_000_000));
            Directory.CreateDirectory(output);
            async Task Invoke(string operation, object request, string suffix, bool collect = false)
            {
                var requestFile = Path.Join(output, suffix + "-request.json");
                var resultFile = Path.Join(output, suffix + "-result.json");
                await File.WriteAllBytesAsync(requestFile, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, operation, request }, Json));
                var cliArgs = new List<string> { "coverage", "fan-in", operation == "collect" ? "capture" : operation, "--request", requestFile, "--result", resultFile, "--json" };
                if (collect) cliArgs.Add("--collect");
                _ = await Command(root, cli, cliArgs.ToArray());
            }
            if (Required("operation") == "native")
            {
                var platform = Required("native-platform");
                if (platform is not ("Linux" or "Windows" or "Darwin")) throw new ArgumentException();
                await Invoke("capture", new CoverageCaptureRequest(policy, root, Path.Join(output, "capture"), platform.ToLowerInvariant(),
                    Required("report"), RawCoveragePath: Required("raw")), "native");
            }
            else if (Required("operation") == "fanin")
            {
                var primary = Path.Join(output, "primary");
                await Invoke("capture", new CoverageCaptureRequest(policy, root, primary, "primary", Required("primary"), Required("timings")), "primary");
                var imported = new List<string>();
                foreach (var supplement in policy.Supplements)
                {
                    var inputs = Directory.GetFiles(Required("native-inputs"), "manifest.json", SearchOption.AllDirectories)
                        .Where(path => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("producerId").GetString() == supplement.Id).Take(2).ToArray();
                    if (inputs.Length != 1) throw new InvalidDataException();
                    using var manifest = JsonDocument.Parse(File.ReadAllBytes(inputs[0]));
                    var destination = Path.Join(output, "native-" + supplement.Id);
                    await Invoke("capture", new CoverageCaptureRequest(policy, root, destination, supplement.Id,
                        ImportDirectory: Path.GetDirectoryName(inputs[0]), OriginalSourceRoot: manifest.RootElement.GetProperty("sourceRoot").GetString()), supplement.Id);
                    imported.Add(destination);
                }
                var composed = Path.Join(output, "composed"); var accepted = Path.Join(output, "accepted");
                await Invoke("compose", new CoverageComposeRequest(policy, root, primary, imported, composed), "compose");
                await Invoke("accept", new CoverageAcceptRequest(policy, root, primary, imported, composed, accepted), "accept");
                await Invoke("verify", new CoverageVerifyRequest(policy, root, primary, imported, composed, accepted), "verify");
            }
            else throw new ArgumentException();
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or JsonException
            or InvalidOperationException or FormatException or OverflowException)
        { Console.Error.WriteLine("ASCOV311 Dogfood failed. Inspect retained native command result files and correct the explicit repository policy/configuration; preserve partial output."); return 1; }
    }
    private static async Task<string> Command(string root, string executable, string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var result = await stdout; var errors = await stderr;
        if (process.ExitCode != 0 || result.Length > 1048576 || errors.Length > 1048576) throw new InvalidDataException();
        return result.Trim();
    }
}
