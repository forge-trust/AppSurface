using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>Dogfood release proof for the exact packed neutral facade and private dependency.</summary>
internal static class InstalledPackageProof
{
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
            foreach (var id in new[] { "ForgeTrust.AppSurface.Coverage", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Cli" })
            {
                var package = Path.Join(feed, $"{id}.{version}.nupkg");
                using var archive = ZipFile.OpenRead(package);
                var nuspec = archive.Entries.Single(x => x.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                using var stream = nuspec.Open(); var document = XDocument.Load(stream);
                var metadata = document.Descendants().Single(x => x.Name.LocalName == "metadata");
                if (metadata.Elements().Single(x => x.Name.LocalName == "id").Value != id || metadata.Elements().Single(x => x.Name.LocalName == "version").Value != version)
                    throw new InvalidDataException();
                var firstPartyDependencies = document.Descendants().Where(x => x.Name.LocalName == "dependency"
                    && ((string?)x.Attribute("id"))?.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase) == true).ToArray();
                if (id == "ForgeTrust.AppSurface.Coverage")
                {
                    var engineDependencies = firstPartyDependencies.Where(x =>
                        string.Equals((string?)x.Attribute("id"), "ForgeTrust.AppSurface.Evidence.Coverage", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (engineDependencies.Length == 0 || engineDependencies.Any(x =>
                        !string.Equals((string?)x.Attribute("version"), $"[{version}]", StringComparison.Ordinal)))
                        throw new InvalidDataException();
                }
                if (firstPartyDependencies.Any(x =>
                    !(id == "ForgeTrust.AppSurface.Coverage"
                        && string.Equals((string?)x.Attribute("id"), "ForgeTrust.AppSurface.Evidence.Coverage", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals((string?)x.Attribute("version"), version, StringComparison.Ordinal))) throw new InvalidDataException();
                hashes[id] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package)));
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
            async Task Run(string stage, params string[] command)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var process = new Process { StartInfo = new(dotnet) { WorkingDirectory = output, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
                foreach (var a in command) process.StartInfo.ArgumentList.Add(a);
                process.StartInfo.Environment["NUGET_PACKAGES"] = Path.Join(output, "packages");
                process.StartInfo.Environment["DOTNET_CLI_HOME"] = Path.Join(output, "cli-home");
                process.StartInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
                process.StartInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
                process.StartInfo.Environment["PATH"] = Path.GetDirectoryName(dotnet) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                process.Start();
                var stdout = ReadBounded(process.StandardOutput, deadline.Token); var stderr = ReadBounded(process.StandardError, deadline.Token);
                try { await process.WaitForExitAsync(deadline.Token); } catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
                var text = await stdout; var errors = await stderr;
                stages.Add(new { stage, exitCode = process.ExitCode });
                await File.WriteAllTextAsync(Path.Join(output, stage + ".log"), text + errors);
                if (process.ExitCode != 0) throw new InvalidDataException();
            }
            await Run("tool-install", "tool", "install", "ForgeTrust.AppSurface.Cli", "--tool-path", toolRoot, "--version", version, "--configfile", toolConfigPath);
            var installedCommand = Path.Join(toolRoot, OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface");
            if (!File.Exists(installedCommand)) throw new InvalidDataException();
            var cliStorePackages = Directory.EnumerateFiles(Path.Join(toolRoot, ".store"), "*.nupkg", SearchOption.AllDirectories).ToArray();
            if (!cliStorePackages.Any(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                == hashes["ForgeTrust.AppSurface.Cli"])) throw new InvalidDataException();
            await Run("restore", "restore", Path.Join(app, "Fixture.csproj"), "--configfile", Path.Join(output, "nuget.config"));
            foreach (var id in new[] { "ForgeTrust.AppSurface.Coverage", "ForgeTrust.AppSurface.Evidence.Coverage" })
            {
                var hash = hashes[id];
                var restored = Path.Join(output, "packages", id.ToLowerInvariant(), version.ToLowerInvariant(),
                    id.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg");
                if (!File.Exists(restored) || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(restored))) != hash)
                    throw new InvalidDataException();
            }
            using (var assets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(app, "obj", "project.assets.json"))))
            {
                var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject().Select(x => x.Name).ToArray();
                if (libraries.Any(x => x.Contains("Aspire", StringComparison.OrdinalIgnoreCase) || x.Contains("CliFx", StringComparison.OrdinalIgnoreCase) || x.Contains("Evidence.Planner", StringComparison.Ordinal) || x.StartsWith("ForgeTrust.AppSurface.Cli/", StringComparison.Ordinal))
                    || !libraries.Contains("ForgeTrust.AppSurface.Coverage/" + version) || !libraries.Contains("ForgeTrust.AppSurface.Evidence.Coverage/" + version)) throw new InvalidDataException();
            }
            await Run("build", "build", Path.Join(app, "Fixture.csproj"), "--no-restore", "--configuration", "Release", "-warnaserror");
            await Run("run", "run", "--project", Path.Join(app, "Fixture.csproj"), "--no-build", "--configuration", "Release", "--", Path.Join(output, "direct"), installedCommand);
            await Run("publish", "publish", Path.Join(app, "Fixture.csproj"), "--no-restore", "--configuration", "Release", "--output", Path.Join(output, "published"), "-warnaserror");
            await Run("published-run", Path.Join(output, "published", "Fixture.dll"), Path.Join(output, "published-proof"), installedCommand);
            await File.WriteAllBytesAsync(Path.Join(output, "proof.json"), JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, version, packages = hashes, stages, passed = true }));
            Console.WriteLine("Packed coverage baseline and tamper proof passed."); return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException)
        { Console.Error.WriteLine("ASCOV312 Packed coverage proof failed; inspect retained stage logs and exact candidate package closure."); return 1; }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        { if (text.Length + read > 1048576) throw new InvalidDataException(); text.Append(buffer, 0, read); }
        return text.ToString();
    }
}
