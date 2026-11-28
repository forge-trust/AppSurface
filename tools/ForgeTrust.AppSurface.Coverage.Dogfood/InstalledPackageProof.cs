using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Validation;
using static ForgeTrust.AppSurface.Evidence.Validation.PackedCandidatePackages;

/// <summary>Dogfood release proof for the exact packed neutral facade and its private dependency closure.</summary>
internal static class InstalledPackageProof
{
    private const string PrivateDependencyFailure = "coverage.private-dependency-invalid";
    private const string CandidateArchiveLimit = "coverage.candidate-archive-limit";
    private const string CandidateNuspecInvalid = "coverage.candidate-nuspec-invalid";
    private const int MaximumCharactersPerStageStream = 1024 * 1024;

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 8) throw new ArgumentException();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
                if (!new[] { "--feed", "--version", "--output", "--dotnet" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException();
            var feed = Path.GetFullPath(options["--feed"]); var version = options["--version"];
            var output = Path.GetFullPath(options["--output"]); var dotnet = Path.GetFullPath(options["--dotnet"]);
            if (Directory.Exists(output) || File.Exists(output) || !Directory.Exists(feed) || !File.Exists(dotnet)
                || version.Length > 100 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) throw new ArgumentException();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            string? sourceCommit = null;
            // The catalog packs dependencies first. Admission checks the public facade first for stable diagnostics.
            var neutralLibraries = NeutralLibraries.Reverse().ToArray();
            foreach (var candidate in neutralLibraries.Append(Tool))
            {
                var id = candidate.Id;
                var package = Path.Join(feed, candidate.ArchiveName(version));
                var (capture, archiveBytes) = await PackedCandidateArchive.CaptureAsync(package, CancellationToken.None);
                if (capture == PackedCandidateArchive.CaptureStatus.TooLarge) throw new InvalidDataException(CandidateArchiveLimit);
                if (capture != PackedCandidateArchive.CaptureStatus.Complete) throw new InvalidDataException();
                using var archiveStream = new MemoryStream(archiveBytes, writable: false);
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                var nuspec = archive.Entries.Single(x => x.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                var metadata = ReadCandidateMetadata(nuspec);
                if (metadata.Elements().Single(x => x.Name.LocalName == "id").Value != id || metadata.Elements().Single(x => x.Name.LocalName == "version").Value != version)
                    throw new InvalidDataException();
                var commit = (string?)metadata.Elements().Single(x => x.Name.LocalName == "repository").Attribute("commit");
                if (string.IsNullOrWhiteSpace(commit) || (sourceCommit is not null && commit != sourceCommit))
                    throw new InvalidDataException();
                sourceCommit = commit;
                var firstPartyDependencies = metadata.Document!.Descendants().Where(x => x.Name.LocalName == "dependency"
                    && ((string?)x.Attribute("id"))?.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase) == true).ToArray();
                var privateDependencyId = PrivatePackageEdges.RequiredDependency(id);
                var declarations = metadata.Elements().Where(x => x.Name.LocalName == "dependencies").ToArray();
                var dependencies = declarations is [var declaration] ? declaration : null;
                if (privateDependencyId is not null
                    && (dependencies is null || !PrivatePackageEdges.HasSingleExactPin(dependencies, privateDependencyId, version)))
                    throw new InvalidDataException(PrivateDependencyFailure);

                // The shared check covers private edges inside the declaration. Reject stray private edges and
                // retain the separate same-version rule for every other first-party dependency.
                if (firstPartyDependencies.Any(x =>
                    string.Equals((string?)x.Attribute("id"), privateDependencyId, StringComparison.OrdinalIgnoreCase)
                        ? !x.Ancestors().Contains(dependencies)
                        : !string.Equals((string?)x.Attribute("version"), version, StringComparison.Ordinal))) throw new InvalidDataException(PrivateDependencyFailure);
                hashes[id] = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
            }
            Directory.CreateDirectory(output); var app = Path.Join(output, "app"); Directory.CreateDirectory(app);
            var toolRoot = Path.Join(output, "installed-tool"); Directory.CreateDirectory(toolRoot);
            var toolConfig = new XDocument(new XElement("configuration",
                new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed))),
                new XElement("fallbackPackageFolders", new XElement("clear"))));
            var toolConfigPath = Path.Join(output, "tool.nuget.config"); toolConfig.Save(toolConfigPath);
            var fixture = Path.Join(AppContext.BaseDirectory, "fixtures", "headless");
            await File.WriteAllTextAsync(Path.Join(app, "Fixture.csproj"), (await File.ReadAllTextAsync(Path.Join(fixture, "Fixture.csproj.template"))).Replace("CANDIDATE_VERSION", version, StringComparison.Ordinal));
            File.Copy(Path.Join(fixture, "Program.cs"), Path.Join(app, "Program.cs"));
            File.Copy(Path.Join(AppContext.BaseDirectory, "examples", "CoverageFanInApiExample.cs"),
                Path.Join(app, "CoverageFanInApiExample.cs"));
            var config = new XDocument(new XElement("configuration",
                new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)), new XElement("add", new XAttribute("key", "nuget"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
                new XElement("packageSourceMapping", new XElement("clear"), new XElement("packageSource", new XAttribute("key", "candidate"), new XElement("package", new XAttribute("pattern", "ForgeTrust.*"))), new XElement("packageSource", new XAttribute("key", "nuget"), new XElement("package", new XAttribute("pattern", "CliWrap")))),
                new XElement("fallbackPackageFolders", new XElement("clear"))));
            config.Save(Path.Join(output, "nuget.config"));
            var stages = new List<object>();
            var fixtureTargetFramework = OperatingSystem.IsWindows() ? "net10.0-windows" : "net10.0";
            async Task Run(string stage, params string[] command)
            {
                var start = new ProcessStartInfo(dotnet)
                {
                    WorkingDirectory = output,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var argument in command) start.ArgumentList.Add(argument);
                start.Environment["NUGET_PACKAGES"] = Path.Join(output, "packages");
                start.Environment["DOTNET_CLI_HOME"] = Path.Join(output, "cli-home");
                start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
                start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
                start.Environment["PATH"] = Path.GetDirectoryName(dotnet) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                var result = await PackedProofProcess.RunAsync(stage, start, Path.Join(output, stage + ".log"),
                    CancellationToken.None, stageTimeout: TimeSpan.FromMinutes(5),
                    maximumCharactersPerStream: MaximumCharactersPerStageStream);
                stages.Add(new { stage, exitCode = result.ExitCode });
                if (result.ExitCode != 0) throw new InvalidDataException();
            }
            await Run("tool-install", "tool", "install", ToolId, "--tool-path", toolRoot, "--version", version, "--configfile", toolConfigPath);
            var installedCommand = Path.Join(toolRoot, OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface");
            if (!File.Exists(installedCommand)) throw new InvalidDataException();
            var cliStorePackages = Directory.EnumerateFiles(Path.Join(toolRoot, ".store"), "*.nupkg", SearchOption.AllDirectories).ToArray();
            if (!cliStorePackages.Any(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                == hashes[ToolId])) throw new InvalidDataException();
            await Run("restore", "restore", Path.Join(app, "Fixture.csproj"), "--configfile", Path.Join(output, "nuget.config"));
            foreach (var candidate in neutralLibraries)
            {
                var id = candidate.Id;
                var hash = hashes[id];
                var restored = Path.Join(output, "packages", id.ToLowerInvariant(), version.ToLowerInvariant(),
                    candidate.ArchiveName(version).ToLowerInvariant());
                if (!File.Exists(restored) || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(restored))) != hash)
                    throw new InvalidDataException();
            }
            using (var assets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(app, "obj", "project.assets.json"))))
            {
                var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject().Select(x => x.Name).ToArray();
                if (libraries.Any(x => x.Contains("Aspire", StringComparison.OrdinalIgnoreCase) || x.Contains("CliFx", StringComparison.OrdinalIgnoreCase) || x.Contains("Evidence.Planner", StringComparison.Ordinal) || x.StartsWith(ToolId + "/", StringComparison.Ordinal))
                    || neutralLibraries.Any(candidate => !libraries.Contains(candidate.Id + "/" + version, StringComparer.Ordinal)))
                    throw new InvalidDataException();
            }
            await Run("build", "build", Path.Join(app, "Fixture.csproj"), "--no-restore", "--configuration", "Release", "-warnaserror");
            RequireReportGeneratorPayload(Path.Join(app, "bin", "Release", fixtureTargetFramework));
            await Run("run", "run", "--project", Path.Join(app, "Fixture.csproj"), "--no-build", "--configuration", "Release", "--", Path.Join(output, "direct"), installedCommand);
            await Run("publish", "publish", Path.Join(app, "Fixture.csproj"), "--no-restore", "--configuration", "Release", "--output", Path.Join(output, "published"), "-warnaserror");
            RequireReportGeneratorPayload(Path.Join(output, "published"));
            await Run("published-run", Path.Join(output, "published", "Fixture.dll"), Path.Join(output, "published-proof"), installedCommand);
            await File.WriteAllBytesAsync(Path.Join(output, "proof.json"), JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, version, sourceCommit, packages = hashes, stages, passed = true }));
            Console.WriteLine("Packed coverage baseline and tamper proof passed."); return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException
            or JsonException or OperationCanceledException or UnauthorizedAccessException or Win32Exception or NotSupportedException or AggregateException)
        {
            var admission = exception is InvalidDataException
                && exception.Message is PrivateDependencyFailure or CandidateArchiveLimit or CandidateNuspecInvalid
                ? " Code: " + exception.Message + "." : string.Empty;
            Console.Error.WriteLine("ASCOV312 Packed coverage proof failed; inspect retained stage logs and exact candidate package closure." + admission);
            return 1;
        }
    }

    private static XElement ReadCandidateMetadata(ZipArchiveEntry specification)
    {
        try
        {
            return PackedCandidateArchive.ReadBoundedMetadata(specification)
                ?? throw new InvalidDataException(CandidateNuspecInvalid);
        }
        catch (Exception exception) when (exception is XmlException or EndOfStreamException or InvalidOperationException)
        {
            throw new InvalidDataException(CandidateNuspecInvalid, exception);
        }
    }
    private static void RequireReportGeneratorPayload(string outputRoot)
    {
        var payloadRoot = Path.Join(outputRoot, "appsurface-coverage-runtime", "reportgenerator", "5.5.10", "net10.0");
        foreach (var file in new[] { "runtime-manifest.json", "ReportGenerator.dll", "ReportGenerator.deps.json", "ReportGenerator.runtimeconfig.json" })
            if (!File.Exists(Path.Join(payloadRoot, file))) throw new InvalidDataException();
    }
}
